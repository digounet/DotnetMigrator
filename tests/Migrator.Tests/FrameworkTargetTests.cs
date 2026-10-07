using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>--target framework (code untouched, .NET Framework 4.8.1, EC2 Windows + CloudFormation) and --iac cloudformation for .NET 10.</summary>
public sealed class FrameworkTargetTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("migrator-fx-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static string SampleSolution()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Migrator.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "LegacyShop", "LegacyShop.sln");
    }

    [Fact]
    public void Project_and_config_rewrites_only_touch_the_framework_version()
    {
        var (csproj, before) = FrameworkProjectRewriter.Rewrite("<Project>\n  <PropertyGroup>\n    <TargetFrameworkVersion>v4.5</TargetFrameworkVersion>\n    <OutputType>Exe</OutputType>\n  </PropertyGroup>\n</Project>", sdkStyle: false);
        Assert.Equal("v4.5", before);
        Assert.Contains("<TargetFrameworkVersion>v4.8.1</TargetFrameworkVersion>", csproj);
        Assert.Contains("<OutputType>Exe</OutputType>", csproj);
        Assert.Null(FrameworkProjectRewriter.Rewrite("<Project><PropertyGroup><TargetFrameworkVersion>v4.8.1</TargetFrameworkVersion></PropertyGroup></Project>", false).Before);

        var (sdk, sdkBefore) = FrameworkProjectRewriter.Rewrite("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>net462;net48</TargetFrameworks></PropertyGroup></Project>", sdkStyle: true);
        Assert.Equal("net462", sdkBefore);
        Assert.Contains("<TargetFrameworks>net481</TargetFrameworks>", sdk);

        var (config, changed) = FrameworkProjectRewriter.RewriteConfig("""
            <configuration>
              <startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.5" /></startup>
              <system.web><compilation debug="true" targetFramework="4.5" /><httpRuntime targetFramework="4.5" maxRequestLength="20480" /></system.web>
            </configuration>
            """);
        Assert.Contains("sku=\".NETFramework,Version=v4.8.1\"", config);
        Assert.Contains("<compilation debug=\"true\" targetFramework=\"4.8.1\" />", config);
        Assert.Contains("<httpRuntime targetFramework=\"4.8.1\" maxRequestLength=\"20480\" />", config);
        Assert.Equal(3, changed.Count);
    }

    [Fact]
    public async Task Framework_target_keeps_code_raises_to_481_and_hosts_on_ec2_windows()
    {
        var output = Path.Combine(_work, "LegacyShop.net481");
        var result = await new MigrationEngine().RunAsync(new MigrationOptions { InputPath = SampleSolution(), OutputDir = output, Offline = true, VerifyBuild = true, Target = MigrationTarget.NetFramework, Serverless = true // the build is skipped on purpose for this target
        });
        string Read(string relative) => File.ReadAllText(Path.Combine(output, relative));
        bool Exists(string relative) => File.Exists(Path.Combine(output, relative));

        // Code untouched, framework raised, original project/solution format kept (VB included).
        Assert.True(Exists("LegacyShop.sln"));
        Assert.False(Exists("LegacyShop.slnx"));
        Assert.Contains("<TargetFrameworkVersion>v4.8.1</TargetFrameworkVersion>", Read("LegacyShop.Web/LegacyShop.Web.csproj"));
        Assert.DoesNotContain("Sdk=\"Microsoft.NET.Sdk", Read("LegacyShop.Web/LegacyShop.Web.csproj"));
        Assert.True(Exists("LegacyShop.Web/packages.config"));
        Assert.True(Exists("LegacyShop.Web/Global.asax.cs"));
        Assert.True(Exists("LegacyShop.Web/Relatorios/Vendas.aspx"));
        Assert.False(Exists("LegacyShop.Web/Program.cs"));
        Assert.False(Exists("LegacyShop.Web/appsettings.json"));
        Assert.False(Exists("LegacyShop.Web/Dockerfile"));
        Assert.Contains("targetFramework=\"4.8.1\"", Read("LegacyShop.Web/Web.config"));
        Assert.Contains("Senha@123", Read("LegacyShop.Web/Web.config")); // config is not converted: secrets stay (and are flagged)
        Assert.Contains("sku=\".NETFramework,Version=v4.8.1\"", Read("LegacyShop.Importador/App.config"));
        Assert.Contains("<TargetFrameworkVersion>v4.8.1</TargetFrameworkVersion>", Read("LegacyShop.Relatorios/LegacyShop.Relatorios.vbproj"));
        Assert.True(Exists("LegacyShop.Worker/SincronizacaoService.Designer.cs"));
        Assert.Contains("ServiceBase", Read("LegacyShop.Worker/SincronizacaoService.cs"));
        Assert.True(Exists("lib/Legacy.Barcode.dll"));
        Assert.True(Exists("Shared/VersaoInfo.cs"));
        Assert.False(Exists("LegacyShop.Importador/Function.cs"));

        var web = result.Projects[0];
        Assert.Contains(web.Inventory, i => i.RuleId == "PRJ-FX-UPGRADE" && i.AutoMigrated && i.Title.Contains("→ v4.8.1"));
        Assert.Contains(web.Inventory, i => i.RuleId == "CFG-FX-RUNTIME" && i.AutoMigrated);
        Assert.Contains(web.Inventory, i => i.RuleId == "CFG-FX-SECRETS");
        Assert.Contains(web.Inventory, i => i.RuleId == "WEB-WEBFORMS-KEPT");
        Assert.DoesNotContain(web.Inventory, i => i.RuleId is "WEB-WEBFORMS" or "PRJ-SDK" or "STARTUP-PROGRAM");
        Assert.DoesNotContain(result.Projects[5].Inventory, i => i.RuleId == "PRJ-VB"); // VB compiles on .NET Framework: just upgraded
        Assert.NotNull(result.Projects[5].OutputProjectPath);
        Assert.DoesNotContain(result.AllModernizations, m => m.RuleId.StartsWith("MOD-CS-") || m.RuleId.StartsWith("MOD-WIN-"));
        Assert.Contains(result.AllModernizations, m => m.RuleId == "MOD-SEC-SECRETS");
        Assert.Contains(result.GlobalItems, i => i.RuleId == "BUILD-FX-SKIPPED");
        Assert.Null(result.BuildSucceeded);

        // Hosting: every deployable on EC2 Windows, with the .NET 10 answer next to it.
        foreach (var p in result.Projects.Where(p => p.Project.Kind is ProjectKind.Web or ProjectKind.Console or ProjectKind.WindowsService))
            Assert.Equal(AwsHosting.Ec2Windows, p.Hosting!.Primary);
        Assert.Contains(web.Hosting!.Rationale, r => r.Contains("ECS Fargate (Linux) + ALB"));
        Assert.Contains(result.Projects[4].Hosting!.Rationale, r => r.Contains("AWS Lambda"));
        Assert.Contains(result.Projects[4].Hosting!.Prerequisites, p => p.Contains("FSx"));
        Assert.Contains(web.Hosting.Prerequisites, p => p.Contains("HealthCheckPath"));
        Assert.Equal(AwsHosting.NotDeployable, result.Projects[1].Hosting!.Primary);
        var services = result.Architecture!.Components.Select(c => c.Id).ToList();
        foreach (var id in new[] { "ec2", "alb", "rds-sqlserver", "fsx", "secrets", "ssm", "cloudwatch", "cicd", "vpc", "ad", "ses" }) Assert.Contains(id, services);
        Assert.DoesNotContain("ecs", services);
        Assert.DoesNotContain("lambda", services);
        Assert.Contains("lift-and-shift", result.Architecture.Summary);
        Assert.Contains("EC2 Windows - Auto Scaling", result.Architecture.Diagram);
        Assert.Contains(result.Architecture.Phases, p => p.Contains("--target framework"));

        // Infrastructure in the platform layout: service.yml per application (EC2 Windows + CodeDeploy), data.yml, <env>/parameters*.json, no Terraform.
        Assert.False(Directory.Exists(Path.Combine(output, "infra", "terraform")));
        Assert.False(Directory.Exists(Path.Combine(output, "infra", "cloudformation")));
        foreach (var file in new[] { "service.yml", "service-worker.yml", "service-importador.yml", "service-relatorios.yml", "data.yml", "deploy.sh", "deploy.ps1", "README.md" })
            Assert.True(Exists($"infra/{file}"), file);
        foreach (var environment in new[] { "dev", "hom", "prod" })
            foreach (var file in new[] { "parameters.json", "parameters-worker.json", "parameters-importador.json", "parameters-data.json" })
                Assert.True(Exists($"infra/{environment}/{file}"), $"{environment}/{file}");
        var service = Read("infra/service.yml");
        Assert.Contains("FeatureName:", service);
        Assert.Contains("AllowedPattern: \"[a-z]*\"", service);
        Assert.Contains("AutoScalingGroup:", service);
        Assert.Contains("TargetGroup:", service);
        Assert.Contains("LoadBalancerListenerArn:", service);                    // shared ALB, VPC and subnets come as parameters
        Assert.Contains("PrivateSubnetThree:", service);
        Assert.Contains("DeploymentGroup:", service);
        Assert.Contains("Install-WindowsFeature Web-Server", service);
        Assert.Contains("codedeploy-agent.msi", service);
        Assert.Contains("/aws/service/ami-windows-latest/Windows_Server-2022-English-Full-Base", service);
        Assert.Contains("UrlsErpProtocoloUrl:", service);                        // hardcoded URL in AppConfig.cs became a stack parameter...
        Assert.Contains("AWS::SSM::Parameter", service);                         // ...delivered through the Parameter Store
        Assert.DoesNotContain("Senha@123", service);
        Assert.DoesNotContain("erp-9f3b2c1d", service);
        Assert.DoesNotContain("AWS::EC2::VPC\n", service);
        var worker = Read("infra/service-worker.yml");
        Assert.DoesNotContain("TargetGroup:", worker);
        Assert.Contains("MaxCapacity:", worker);
        var data = Read("infra/data.yml");
        Assert.Contains("ManageMasterUserPassword: true", data);
        Assert.Contains("AWS::FSx::FileSystem", data);
        Assert.Contains("ArtifactsBucket:", data);
        Assert.Contains("AWS::SecretsManager::Secret", data);
        Assert.Contains("Name: \"legacyshop/legacyshop.web/AppSettings/Credenciais/TokenIntegracaoErp\"", data);
        Assert.Contains("SecretString: \"PREENCHER\"", data);
        Assert.DoesNotContain("erp-9f3b2c1d", data);
        var prodParameters = Read("infra/prod/parameters.json");
        Assert.Contains("\"Parameters\": {", prodParameters);
        Assert.Contains("\"FeatureName\": \"legacyshop\"", prodParameters);
        Assert.Contains("\"MicroServiceName\": \"web\"", prodParameters);
        Assert.Contains("\"Environment\": \"prod\"", prodParameters);
        Assert.Contains("\"UrlsErpProtocoloUrl\": \"https://erp.exemplo.com.br/api/protocolo\"", prodParameters);
        Assert.Contains("\"ApiBaseUrl\": \"https://loja.exemplo.com.br/api\"", prodParameters);   // Web.Release.config value for prod
        Assert.Contains("\"ApiBaseUrl\": \"http://localhost:51234/api\"", Read("infra/dev/parameters.json"));
        Assert.Contains("appspec.yml", string.Join(",", Directory.GetFiles(Path.Combine(output, "infra", "codedeploy", "web"))));
        var afterInstall = Read("infra/codedeploy/web/scripts/after-install.ps1");
        Assert.Contains("New-Website", afterInstall);
        Assert.Contains("Get-SSMParametersByPath", afterInstall);
        Assert.Contains("Get-SECSecretValue", afterInstall);
        Assert.Contains("'AppSettings:Credenciais:TokenIntegracaoErp' = 'legacyshop/legacyshop.web/AppSettings/Credenciais/TokenIntegracaoErp'", afterInstall);
        Assert.Contains("New-Service -Name \"LegacyShopSincronizacao\"", Read("infra/codedeploy/worker/scripts/after-install.ps1"));
        Assert.Contains("schtasks /Create", Read("infra/codedeploy/importador/scripts/after-install.ps1"));
        var workflow = Read(".github/workflows/deploy.yml");
        Assert.Contains("runs-on: windows-latest", workflow);
        Assert.Contains("microsoft/setup-msbuild@v2", workflow);
        Assert.Contains("aws deploy create-deployment", workflow);
        Assert.Contains("file://$ENV/$parameters", Read("infra/deploy.sh"));
        Assert.Contains(result.GlobalItems, i => i.RuleId == "AWS-INFRA" && i.Title.Contains("CloudFormation"));

        // Code: fixed URL/e-mail/credentials left AppConfig.cs for the config (and the secrets plan); const became static readonly.
        var appConfig = Read("LegacyShop.Web/Helpers/AppConfig.cs");
        Assert.Contains("public static readonly string ErpProtocoloUrl = System.Configuration.ConfigurationManager.AppSettings[\"Urls:ErpProtocoloUrl\"];", appConfig);
        Assert.Contains("AppSettings[\"Credenciais:TokenIntegracaoErp\"]", appConfig);
        Assert.DoesNotContain("erp-9f3b2c1d", appConfig);
        Assert.DoesNotContain("Senha@123", appConfig);
        var webConfig = Read("LegacyShop.Web/Web.config");
        Assert.Contains("<add key=\"Urls:ErpProtocoloUrl\" value=\"https://erp.exemplo.com.br/api/protocolo\" />", webConfig);
        Assert.Contains("<add key=\"Credenciais:TokenIntegracaoErp\" value=\"&lt;secret: legacyshop/legacyshop.web/AppSettings/Credenciais/TokenIntegracaoErp&gt;\" />", webConfig);
        Assert.Contains("erp-9f3b2c1d", Read("_secrets/LegacyShop.Web/secrets.template.json"));
        Assert.Contains("<Reference Include=\"System.Configuration\" />", Read("LegacyShop.Web/LegacyShop.Web.csproj"));
        Assert.Contains(web.Inventory, i => i.RuleId == "CS-CONFIG-EXTERNALIZED" && i.AutoMigrated);
        Assert.Contains(web.Inventory, i => i.RuleId == "CS-SECRET-EXTERNALIZED" && i.AutoMigrated);
        Assert.Contains(web.Settings, s => s.Key == "AppSettings:Urls:ErpProtocoloUrl" && s.Kind == SettingKind.Url && s.Source == SettingSource.Code);
        Assert.Contains(web.Settings, s => s.Key == "AppSettings:ApiBaseUrl" && s.Source == SettingSource.Config && s.EnvironmentValues["Production"] == "https://loja.exemplo.com.br/api");

        // Reports say what this run is.
        var markdown = File.ReadAllText(Path.Combine(result.ReportDir!, "migration-report.md"));
        Assert.Contains("# Atualização para .NET Framework 4.8.1 + infraestrutura AWS", markdown);
        Assert.Contains("v4.5 → v4.8.1", markdown);
        Assert.Contains("## Dados acessados", markdown);
    }

    [Fact]
    public async Task Net10_target_with_cloudformation_generates_ecs_and_lambda_stacks()
    {
        var output = Path.Combine(_work, "LegacyShop.net10");
        var result = await new MigrationEngine().RunAsync(new MigrationOptions { Target = MigrationTarget.Net10, InputPath = SampleSolution(), OutputDir = output, Offline = true, VerifyBuild = false, Iac = IacTool.CloudFormation, Serverless = true
        });
        string Read(string relative) => File.ReadAllText(Path.Combine(output, relative));

        Assert.False(Directory.Exists(Path.Combine(output, "infra", "terraform")));
        Assert.False(Directory.Exists(Path.Combine(output, "infra", "codedeploy")));
        foreach (var file in new[] { "service.yml", "service-worker.yml", "lambda-importador.yml", "data.yml", "dev/parameters.json", "hom/parameters-worker.json", "prod/parameters-lambda-importador.json", "prod/parameters-data.json" })
            Assert.True(File.Exists(Path.Combine(output, "infra", file)), file);
        var service = Read("infra/service.yml");
        Assert.Contains("ECSService:", service);                                              // web → ECS service behind the shared ALB
        Assert.Contains("EcsClusterName:", service);
        Assert.Contains("CpuArchitecture: !Ref CpuArchitecture", service);
        Assert.Contains("ValueFrom: !Sub \"arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:legacyshop/legacyshop.web/ConnectionStrings/RelatoriosConnection\"", service);
        Assert.Contains("- Name: AppSettings__Urls__ErpProtocoloUrl", service);               // externalized URL → environment variable from a stack parameter
        Assert.DoesNotContain("Senha@123", service);
        Assert.Contains("AWS::Scheduler::Schedule", Read("infra/service-worker.yml"));         // worker → scheduled task, Main() untouched
        Assert.False(File.Exists(Path.Combine(output, "infra", "service-relatorios.yml")));   // VB project is not converted
        var lambda = Read("infra/lambda-importador.yml");
        Assert.Contains("LegacyShop.Importador::LegacyShop.Importador.Function::FunctionHandler", lambda);
        Assert.Contains("FilesEventsQueueArn", lambda);                                       // files trigger
        Assert.Contains("AWS::SES::ReceiptRule", lambda);                                     // mailbox trigger (opt-in)
        Assert.Contains("MailboxSchedule:", lambda);                                          // polling while the mailbox stays on Exchange
        Assert.Contains("QueueConfigurations:", Read("infra/data.yml"));
        Assert.Contains("\"MicroServiceName\": \"importador\"", Read("infra/prod/parameters-lambda-importador.json"));
        Assert.Contains("docker buildx build --platform linux/arm64", Read(".github/workflows/deploy.yml"));
        Assert.Contains("dotnet lambda deploy-function", Read(".github/workflows/deploy.yml"));
        Assert.Contains("LegacyShop.Relatorios", Read("infra/README.md"));
        Assert.Equal(AwsHosting.Lambda, result.Projects[4].Hosting!.Primary);
        Assert.Contains(result.GlobalItems, i => i.RuleId == "AWS-INFRA" && i.Title.Contains("CloudFormation"));
    }
}
