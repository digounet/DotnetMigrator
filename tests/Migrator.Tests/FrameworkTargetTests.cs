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
        var result = await new MigrationEngine().RunAsync(new MigrationOptions
        {
            InputPath = SampleSolution(), OutputDir = output, Offline = true, VerifyBuild = true, Target = MigrationTarget.NetFramework // the build is skipped on purpose for this target
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

        // Infrastructure: CloudFormation stacks + CodeDeploy bundles + Windows workflow (no Terraform).
        Assert.False(Directory.Exists(Path.Combine(output, "infra", "terraform")));
        foreach (var stack in new[] { "00-network", "10-data", "20-storage", "30-compute" })
        {
            Assert.True(Exists($"infra/cloudformation/{stack}.yaml"), stack);
            Assert.True(Exists($"infra/cloudformation/parameters/{stack}.json"), stack);
        }
        Assert.False(Exists("infra/cloudformation/40-lambda.yaml"));
        var compute = Read("infra/cloudformation/30-compute.yaml");
        Assert.Contains("LegacyShopWebAutoScalingGroup:", compute);
        Assert.Contains("LegacyShopWebTargetGroup:", compute);
        Assert.Contains("LegacyShopWorkerDeploymentGroup:", compute);
        Assert.Contains("Install-WindowsFeature Web-Server", compute);
        Assert.Contains("codedeploy-agent.msi", compute);
        Assert.Contains("/aws/service/ami-windows-latest/Windows_Server-2022-English-Full-Base", compute);
        Assert.DoesNotContain("Senha@123", compute);
        Assert.Contains("ManageMasterUserPassword: true", Read("infra/cloudformation/10-data.yaml"));
        Assert.Contains("AWS::FSx::FileSystem", Read("infra/cloudformation/20-storage.yaml"));
        Assert.Contains("appspec.yml", string.Join(",", Directory.GetFiles(Path.Combine(output, "infra", "codedeploy", "legacyshop-web"))));
        Assert.Contains("New-Website", Read("infra/codedeploy/legacyshop-web/scripts/after-install.ps1"));
        Assert.Contains("Get-SECSecretValue", Read("infra/codedeploy/legacyshop-web/scripts/after-install.ps1"));
        Assert.Contains("New-Service -Name \"LegacyShopSincronizacao\"", Read("infra/codedeploy/legacyshop-worker/scripts/after-install.ps1"));
        Assert.Contains("schtasks /Create", Read("infra/codedeploy/legacyshop-importador/scripts/after-install.ps1"));
        var workflow = Read(".github/workflows/deploy.yml");
        Assert.Contains("runs-on: windows-latest", workflow);
        Assert.Contains("microsoft/setup-msbuild@v2", workflow);
        Assert.Contains("aws deploy create-deployment", workflow);
        Assert.Contains(result.GlobalItems, i => i.RuleId == "AWS-INFRA" && i.Title.Contains("CloudFormation"));
        Assert.Contains("deploy.sh", string.Join(",", Directory.GetFiles(Path.Combine(output, "infra", "cloudformation"))));

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
        var result = await new MigrationEngine().RunAsync(new MigrationOptions
        {
            InputPath = SampleSolution(), OutputDir = output, Offline = true, VerifyBuild = false, Iac = IacTool.CloudFormation
        });
        string Read(string relative) => File.ReadAllText(Path.Combine(output, relative));

        Assert.False(Directory.Exists(Path.Combine(output, "infra", "terraform")));
        Assert.False(Directory.Exists(Path.Combine(output, "infra", "codedeploy")));
        foreach (var stack in new[] { "00-network", "10-data", "20-storage", "30-compute", "40-lambda" })
            Assert.True(File.Exists(Path.Combine(output, "infra", "cloudformation", stack + ".yaml")), stack);
        var compute = Read("infra/cloudformation/30-compute.yaml");
        Assert.Contains("LegacyShopWebService:", compute);                                   // web → ECS service behind the ALB
        Assert.Contains("LegacyShopWorkerSchedule:", compute);                                // worker → EventBridge Scheduler task
        Assert.Contains("AWS::Scheduler::Schedule", compute);
        Assert.Contains("ValueFrom: !Sub \"arn:aws:secretsmanager:${AWS::Region}:${AWS::AccountId}:secret:legacyshop/legacyshop.web/ConnectionStrings/RelatoriosConnection\"", compute);
        Assert.DoesNotContain("Senha@123", compute);
        Assert.DoesNotContain("legacyshop-relatorios", compute);                             // VB project is not converted
        var lambda = Read("infra/cloudformation/40-lambda.yaml");
        Assert.Contains("LegacyShopImportadorFunction:", lambda);
        Assert.Contains("LegacyShop.Importador::LegacyShop.Importador.Function::FunctionHandler", lambda);
        Assert.Contains("FilesEventsQueueArn", lambda);                                       // files trigger
        Assert.Contains("AWS::SES::ReceiptRule", lambda);                                     // mailbox trigger (opt-in)
        Assert.Contains("MailboxSchedule:", lambda);                                          // polling while the mailbox stays on Exchange
        Assert.Contains("QueueConfigurations:", Read("infra/cloudformation/20-storage.yaml"));
        Assert.Contains("aws-actions/amazon-ecr-login@v2", Read(".github/workflows/deploy.yml"));
        Assert.Contains("dotnet lambda deploy-function", Read(".github/workflows/deploy.yml"));
        Assert.Contains("LegacyShop.Relatorios", Read("infra/README.md"));
        Assert.Equal(AwsHosting.Lambda, result.Projects[4].Hosting!.Primary);
        Assert.Contains(result.GlobalItems, i => i.RuleId == "AWS-INFRA" && i.Title.Contains("CloudFormation"));
    }
}
