using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>
/// What the first real migration (an already-modern console that keeps its configuration in Resources/DES.json and PRD.json) showed:
/// credentials outside appsettings*.json, credentials in dictionary initializers, UNC paths, e-mail lists, proxies, consoles that must become
/// workers, and the platform Terraform (--iac terraform-platform).
/// </summary>
public sealed class PlatformTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("migrator-platform-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private string CreateRobot()
    {
        var dir = Path.Combine(_work, "Fenaseg");
        Directory.CreateDirectory(Path.Combine(dir, "Resources"));
        File.WriteAllText(Path.Combine(dir, "Fenaseg.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net6.0</TargetFramework></PropertyGroup>
              <ItemGroup><PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="6.0.0" /></ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), """
            using System.Diagnostics;
            using System.Net;
            using Microsoft.Extensions.Configuration;

            class Program
            {
                static void Main(string[] args)
                {
                    var environment = Environment.GetEnvironmentVariable("COD_AMBI_EXEO");
                    var config = new ConfigurationBuilder().AddJsonFile($"Resources/{environment}.json").Build();
                    try
                    {
                        var headers = new Dictionary<string, string>
                        {
                            { "tokenPass", "s3gredo-do-token" },
                            { "cookie", "LBtoAutSustProd=abc123def456" },
                        };
                        var proxy = new WebProxy("proxynew.itau", 8080);
                        var pasta = @"\\fswcorp\SISTEMAS\ENTRADA\";
                        var para = "um@itau.com.br;dois@itau.com.br";
                        Console.WriteLine(config["Config:Caixa_Email"] + pasta + para + proxy + headers.Count);
                    }
                    catch (Exception)
                    {
                        Process.GetCurrentProcess().Kill();
                    }
                    Process.GetCurrentProcess().Kill();
                }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Resources", "DES.json"), """
            {
              // comentário que o parser precisa tolerar
              "Config": { "Caixa_Email": "des@itau.com.br", "NomeUsuarioEmail": "BAEPRIS", "SenhaUsuarioEmail": "102pri", "UrlExchangeWebService": "https://mail.itau.com.br/EWS/Exchange.asmx" },
              "ConnectionStrings": { "DBFV508": "Data Source=sqnprc024;Initial Catalog=DBFV508;User Id=FV5BDTI;Token=123456789:fv5bdti:EFAF8F9D5EE3D600" },
              "Path": { "PATH_DOWNLOAD": "C:\\Users\\Public\\Download\\" }
            }
            """);
        File.WriteAllText(Path.Combine(dir, "Resources", "PRD.json"), """
            {
              "Config": { "Caixa_Email": "prd@itau.com.br", "NomeUsuarioEmail": "BAEPRIS", "SenhaUsuarioEmail": "102pri", "UrlExchangeWebService": "https://mail.itau.com.br/EWS/Exchange.asmx" },
              "ConnectionStrings": { "DBFV508": "Data Source=sqnprc024;Initial Catalog=DBFV508;User Id=FV5BDTI;Token=123456789:fv5bdti:EFAF8F9D5EE3D600" },
              "Path": { "PATH_DOWNLOAD": "/mnt/fenaseg/download/" }
            }
            """);
        return dir;
    }

    private static string Resolve(string output, string relative) =>
        MigrationEngine.IsRepositoryRootPath(relative) || relative is ".gitignore" or ".gitattributes" or "README.md" or ".migrator-output"
            ? Path.Combine(output, relative) : Path.Combine(output, "app", "src", relative);

    [Fact]
    public void Environment_json_files_under_Resources_are_read_normalized_and_scrubbed()
    {
        var dir = CreateRobot();
        var project = new ProjectInfo { ProjectPath = Path.Combine(dir, "Fenaseg.csproj"), Name = "Fenaseg", Kind = ProjectKind.Console, IsSdkStyle = true, IsAlreadyModern = true, TargetFramework = "net6.0" };
        var files = AppSettingsAnalyzer.Load(project)!;

        Assert.Equal(["Development", "Production"], files.EnvironmentFiles.Keys.OrderBy(k => k));
        Assert.Equal(Path.Combine("Resources", "PRD.json"), files.RelativePaths["Production"]);

        var secrets = SecretsExtractor.Extract(files.Config, "Fenaseg", "Fenaseg");
        var production = secrets.Secrets.Where(s => s.Environment == "Production").Select(s => s.ConfigPath).ToList();
        Assert.Equal(["Config:NomeUsuarioEmail", "Config:SenhaUsuarioEmail", "ConnectionStrings:DBFV508"], production);   // Token= in a connection string is a credential
        Assert.DoesNotContain("102pri", files.Config.EnvironmentJson["Production"]);
        Assert.Contains("<secret: fenaseg/fenaseg/production/Config/SenhaUsuarioEmail>", files.Config.EnvironmentJson["Production"]);

        var settings = AppSettingsAnalyzer.Settings(project, files);
        Assert.Contains(settings, s => s.Key == "Config:UrlExchangeWebService" && s.Kind == SettingKind.Url);
        Assert.Contains(settings, s => s.Key == "Config:Caixa_Email" && s.Kind == SettingKind.Email && s.ValueFor("Production") == "prd@itau.com.br");
        Assert.All(settings.Where(s => s.Key == "Config:Caixa_Email"), s => Assert.True(s.EnvironmentOnly));
    }

    [Fact]
    public void Literals_in_dictionary_pairs_proxies_network_paths_and_email_lists_are_externalized()
    {
        var session = new LiteralExternalizer.Session();
        var text = LiteralExternalizer.Externalize("""
            class A
            {
                void M(System.Net.Http.HttpRequestMessage request)
                {
                    var d = new Dictionary<string, string>
                    {
                        { "tokenPass", "s3gredo-do-token" },
                        { "user", "abc" },
                    };
                    request.Headers.Add("Cookie", "LBtoAutSustProd=abc123def456");
                    var proxy = new System.Net.WebProxy("proxynew.itau", 8080);
                    var pasta = @"\\fswcorp\SISTEMAS\ENTRADA\";
                    var para = "um@itau.com.br;dois@itau.com.br";
                }
            }
            """, "A.cs", session, LiteralExternalizer.ReadStyle.MigratorSettings);

        Assert.DoesNotContain("s3gredo-do-token", text);
        Assert.DoesNotContain("abc123def456", text);
        Assert.DoesNotContain("proxynew.itau", text);
        Assert.DoesNotContain("fswcorp", text);
        Assert.DoesNotContain("um@itau.com.br", text);
        Assert.Contains("{ \"user\", \"abc\" }", text);                                    // too short to be a credential
        Assert.Contains("MigratorSettings.Get(\"AppSettings:Credenciais:TokenPass\")", text);
        Assert.Contains("request.Headers.Add(\"Cookie\", MigratorSettings.Get(\"AppSettings:Credenciais:Cookie\"))", text);
        Assert.Contains("new System.Net.WebProxy(MigratorSettings.Get(\"AppSettings:Urls:ProxyHost\"), 8080)", text);
        Assert.Contains(session.Literals, l => l.Rewritten && l.Kind == SettingKind.Path);
        Assert.Contains(session.Literals, l => l.Rewritten && l.Kind == SettingKind.Email && l.Value.Contains(';'));
    }

    [Fact]
    public void Classic_Main_becomes_RunOnce_and_kill_becomes_return()
    {
        var converted = PlatformWorkerRewriter.Convert([("Program.cs", """
            using System.Diagnostics;
            class Program
            {
                static void Main(string[] args)
                {
                    try { Console.WriteLine("x"); }
                    catch (Exception) { Process.GetCurrentProcess().Kill(); }
                    Process.GetCurrentProcess().Kill();
                }
            }
            """)], "Fenaseg")!;

        Assert.Equal("Program.cs", converted.EntryFile);
        Assert.Contains("class LegacyEntryPoint", converted.EntryText);
        Assert.Contains("internal static void RunOnce(string[] args)", converted.EntryText);
        Assert.DoesNotContain("Kill()", converted.EntryText);
        Assert.Equal(2, converted.EntryText.Split("return;").Length - 1);
        Assert.Contains("MapHealthChecks(\"/health\")", converted.WorkerText);
        Assert.Contains("LegacyEntryPoint.RunOnce(arguments)", converted.WorkerText);
    }

    [Fact]
    public void Top_level_statements_move_into_an_async_RunOnce_and_keep_usings_and_types()
    {
        var converted = PlatformWorkerRewriter.Convert([("Program.cs", """
            using System;
            var total = 0;
            await Task.Delay(1);
            if (total > 0) return 1;
            Console.WriteLine(total);
            record Item(int Id);
            """)], "Robo")!;

        Assert.StartsWith("using System;", converted.EntryText.TrimStart('\uFEFF'));
        Assert.Contains("internal static async System.Threading.Tasks.Task RunOnce(string[] args)", converted.EntryText);
        Assert.Contains("record Item(int Id);", converted.EntryText);
        Assert.DoesNotContain("return 1;", converted.EntryText);
        Assert.Contains("await LegacyEntryPoint.RunOnce(arguments);", converted.WorkerText);
    }

    [Fact]
    public void Worker_project_file_gets_the_aspnetcore_framework_and_loses_the_packages_it_ships()
    {
        var csproj = PlatformWorkerRewriter.AddAspNetCoreFramework("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="10.0.0" />
                <PackageReference Include="Dapper" Version="2.0.143" />
              </ItemGroup>
            </Project>
            """);
        Assert.Contains("<FrameworkReference Include=\"Microsoft.AspNetCore.App\" />", csproj);
        Assert.DoesNotContain("Microsoft.Extensions.Configuration.Json", csproj);
        Assert.Contains("Dapper", csproj);
    }

    [Fact]
    public async Task Terraform_platform_generates_the_worker_module_secret_keys_inventories_and_pipeline_files()
    {
        var dir = CreateRobot();
        var output = Path.Combine(_work, "out");
        var result = await new MigrationEngine().RunAsync(new MigrationOptions { InputPath = Path.Combine(dir, "Fenaseg.csproj"), OutputDir = output, Offline = true, VerifyBuild = false, Target = MigrationTarget.Net10, Iac = IacTool.TerraformPlatform });
        string Read(string relative) => File.ReadAllText(Resolve(output, relative));
        bool Exists(string relative) => File.Exists(Resolve(output, relative));

        // 1. credentials left the config files and the code; the single platform secret holds them
        var prd = Read("Resources/PRD.json");
        Assert.DoesNotContain("102pri", prd);
        Assert.DoesNotContain("Token=123456789", prd);
        Assert.Contains("<secret:", prd);
        Assert.DoesNotContain("s3gredo-do-token", Read("Program.cs"));
        var secretProd = Read("_secrets/platform/secret-prod.json");
        Assert.Contains("\"Config__SenhaUsuarioEmail\": \"102pri\"", secretProd);
        Assert.Contains("s3gredo-do-token", secretProd);
        Assert.DoesNotContain("102pri", Read("infra/terraform/inventories/prod/terraform.tfvars"));   // Terraform only sees names

        // 2. the console is a worker now
        Assert.Contains("internal static void RunOnce", Read("Program.cs"));
        Assert.Contains("class MigratorWorker", Read("MigratorWorker.cs"));
        Assert.Contains("Microsoft.AspNetCore.App", Read("Fenaseg.csproj"));
        Assert.Equal(AwsHosting.EcsFargateWorker, result.Projects.Single().Hosting!.Primary);

        // 3. platform modules, not a VPC/ALB from scratch
        var main = Read("infra/terraform/main.tf");
        Assert.Contains("itau-hn8-modules-ecs-service.git//modules/worker", main);
        Assert.Contains("task_secrets", main);
        Assert.Contains("itau-ey4-modulo-iamsr", Read("infra/terraform/iamsr-module.tf"));
        Assert.False(Exists("infra/terraform/network.tf"));
        Assert.Contains("microservice_name                  = \"worker-fenaseg\"", main);
        var tfvars = Read("infra/terraform/inventories/prod/terraform.tfvars");
        Assert.Contains("\"ConnectionStrings__DBFV508\"", tfvars);
        Assert.Contains("\"COD_AMBI_EXEO\" = \"PRD\"", tfvars);                                       // picks Resources/PRD.json
        Assert.Contains("\"Config__Caixa_Email\" = \"prd@itau.com.br\"", System.Text.RegularExpressions.Regex.Replace(tfvars, " +", " "));    // value of the environment
        Assert.Contains("\\\\\\\\fswcorp\\\\SISTEMAS\\\\ENTRADA\\\\", tfvars);                                // UNC path, HCL-escaped
        Assert.Contains("\"COD_AMBI_EXEO\" = \"DES\"", Read("infra/terraform/inventories/dev/terraform.tfvars"));
        Assert.Contains("secret:", Read("infra/terraform/iamsr/policy-template/policy-secrets-manager.json"));

        // 4. pipeline descriptor, image and test specs in the platform shape
        var iupipes = Read(".iupipes.yml");
        Assert.Contains("infra:\n  terraform:", iupipes);
        Assert.DoesNotContain("cloudformation:", iupipes);
        Assert.Contains("service: 'service-fenaseg'", iupipes);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"Fenaseg.dll\"]", Read("Dockerfile"));
        Assert.Contains("alpine-arm64v8", Read("Dockerfile"));
        Assert.True(Exists("tests/testspec-dev.yml"));
        Assert.False(Exists("Fenaseg/Dockerfile"));
        Assert.Contains(result.Projects.Single().Inventory, i => i.RuleId == "PLAT-WORKER");
    }
}
