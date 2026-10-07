using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>End-to-end runs against samples/LegacyShop (offline, without the verification build).</summary>
public sealed class SampleSolutionTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("migrator-e2e-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static string SampleSolution()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Migrator.slnx"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "samples", "LegacyShop", "LegacyShop.sln");
    }

    [Fact]
    public async Task Analyze_inventories_the_whole_solution_without_writing_code()
    {
        var reportDir = Path.Combine(_work, "report");

        var result = await new MigrationEngine().RunAsync(new MigrationOptions
        {
            InputPath = SampleSolution(), DryRun = true, Offline = true, ReportDir = reportDir
        });

        Assert.Equal(["LegacyShop.Web", "LegacyShop.Core", "LegacyShop.Worker", "LegacyShop.Tests", "LegacyShop.Importador", "LegacyShop.Relatorios"], result.Projects.Select(p => p.Project.Name));
        Assert.DoesNotContain(result.GlobalItems, i => i.RuleId == "SLN-SKIPPED" && i.Title.Contains(".vbproj")); // VB projects are loaded and profiled now
        Assert.Null(result.OutputDir);

        var web = result.Projects[0];
        Assert.Equal(ProjectKind.Web, web.Project.Kind);
        string[] expectedWebRules = ["WEB002", "WEB003", "WEB013", "WEB029", "VW002", "VW003", "CFG-FORMSAUTH", "CS-WEBAPI-ATTR", "VW-BUNDLE", "PRJ-DLL", "STARTUP-DI"];
        foreach (var rule in expectedWebRules)
            Assert.Contains(web.Inventory, i => i.RuleId == rule);
        Assert.DoesNotContain(web.Inventory, i => i.RuleId == "PRJ-FRAMEWORKREF" && i.Title.Contains("System.Web.Services"));

        var worker = result.Projects[2];
        Assert.Equal(ProjectKind.WindowsService, worker.Project.Kind);
        Assert.Contains(worker.Inventory, i => i.RuleId == "NET002");
        Assert.Contains(worker.Inventory, i => i.RuleId == "CS-ENCODING");

        foreach (var file in new[] { "migration-report.html", "migration-report.md", "inventory.csv", "inventory.xlsx", "modernization.csv", "data-access.csv" })
            Assert.True(File.Exists(Path.Combine(reportDir, file)), file);

        // Data access inventory: database, engine, tables and columns per project (always, whatever the target)
        var core = result.Projects[1];
        var produto = Assert.Single(core.DataAccess, t => t.Name == "Produto");
        Assert.Equal("LegacyShop", produto.Database);                                   // Core has no own connection string: resolved from the hosts (Web/Importador) by name
        Assert.Equal("SQL Server", produto.Technology);
        Assert.Equal(["Categoria", "Descricao", "Destaque", "Id", "Nome", "Preco"], produto.Columns);
        Assert.Contains("Dapper", produto.Access);
        Assert.Contains("EF6", produto.Access);
        Assert.Contains("arquivo .sql", produto.Access);
        Assert.Contains("DELETE", produto.Operations);
        Assert.Contains("EF (escrita)", produto.Operations);
        var pedido = Assert.Single(result.Projects[4].DataAccess);
        Assert.Equal(("PedidoImportado", "LegacyShop"), (pedido.Name, pedido.Database));
        Assert.Equal(["Cliente", "Data", "Numero", "Valor"], pedido.Columns);
        var vendas = Assert.Single(result.Projects[5].DataAccess);                       // VB: SELECT * + reader columns, connection "Relatorios"
        Assert.Equal(("Vendas", "Relatorios"), (vendas.Name, vendas.Database));
        Assert.Equal(["*", "Mes", "Produto", "Valor"], vendas.Columns);
        Assert.Contains("3 tabela(s) acessadas", result.Architecture!.Components.Single(c => c.Id == "rds-sqlserver").Role);

        // Modernization advice and AWS architecture
        Assert.Contains(web.Modernizations, m => m.RuleId == "MOD-PKG-AUTOMAPPER" && m.Kind == ModernizationKind.License);
        Assert.Contains(web.Modernizations, m => m.RuleId == "MOD-PKG-ITEXTSHARP");
        Assert.Contains(web.Modernizations, m => m.RuleId == "MOD-ARCH-SESSION");
        Assert.Contains(web.Modernizations, m => m.RuleId == "MOD-CS-STATIC-STATE");   // static List<Pedido> in PedidosController
        Assert.Contains(web.Modernizations, m => m.RuleId == "MOD-SEC-SECRETS" && m.Evidence!.Contains("network/@password"));
        Assert.Contains(web.Modernizations, m => m.RuleId == "MOD-SEC-SECRETS-CODE" && m.Evidence!.Contains("TokenIntegracaoErp"));
        Assert.Contains(web.Inventory, i => i.RuleId == "CFG-SECRETS-EXTRACTED" && i.Suggestion.Contains("Secrets Manager"));
        Assert.DoesNotContain(worker.Modernizations, m => m.RuleId == "MOD-WIN-SERVICE"); // converted deterministically
        Assert.Contains(worker.Inventory, i => i.RuleId == "CS-WORKER-PROGRAM");
        Assert.Contains(worker.Modernizations, m => m.RuleId == "MOD-ARCH-DB-INTEGRATED");
        Assert.Contains(worker.Modernizations, m => m.RuleId == "MOD-ARCH-HYBRID" && m.Evidence!.Contains("erp.interno"));
        Assert.Contains(result.Projects[1].Modernizations, m => m.RuleId == "MOD-PKG-EF6");
        Assert.Empty(result.Projects[3].Modernizations);

        Assert.Equal(AwsHosting.EcsFargate, web.Hosting!.Primary);
        Assert.Empty(web.Hosting.HardWindowsDependencies); // System.Drawing/EnterpriseServices are unused template references
        Assert.Equal(AwsHosting.EcsScheduledTask, worker.Hosting!.Primary);
        Assert.Equal(AwsHosting.NotDeployable, result.Projects[1].Hosting!.Primary);

        // Back-office automation (console run by Task Scheduler: folder + mailbox + SQL) → Lambda with event triggers
        var importador = result.Projects[4];
        Assert.Equal(ProjectKind.Console, importador.Project.Kind);
        Assert.Equal(AwsHosting.Lambda, importador.Hosting!.Primary);
        Assert.Contains(importador.Hosting.Rationale, r => r.Contains("S3 Event Notifications") && r.Contains("SES"));
        Assert.Contains(importador.Hosting.Prerequisites, p => p.Contains("EWS"));
        Assert.Contains(importador.Modernizations, m => m.RuleId == "MOD-PKG-EWS");
        Assert.Contains(importador.Modernizations, m => m.RuleId == "MOD-ARCH-MAILBOX");
        Assert.Contains(importador.Modernizations, m => m.RuleId == "MOD-SEC-SECRETS" && m.Evidence!.Contains("CaixaPostalSenha"));
        Assert.Contains(importador.Modernizations, m => m.RuleId == "MOD-CS-PARSE-CULTURE");
        Assert.Contains(importador.Inventory, i => i.RuleId == "AWS-LAMBDA");

        // VB.NET: profiled, not converted
        var relatorios = result.Projects[5];
        Assert.True(relatorios.Project.IsVisualBasic);
        Assert.Equal(ProjectKind.Console, relatorios.Project.Kind);
        Assert.Contains(relatorios.Inventory, i => i.RuleId == "PRJ-VB" && i.Severity == InventorySeverity.Breaking && i.Title.Contains("3 arquivo(s) .vb"));
        Assert.Null(relatorios.OutputProjectPath);
        Assert.Equal(AwsHosting.EcsWindows, relatorios.Hosting!.Primary); // Office Interop (COM) is a hard Windows dependency
        Assert.Contains(relatorios.Hosting.Prerequisites, p => p.Contains("VB.NET"));
        Assert.Contains(relatorios.Modernizations, m => m.RuleId == "MOD-WIN-COM");
        Assert.Contains(relatorios.Modernizations, m => m.RuleId == "MOD-ARCH-SMTP");
        Assert.Contains(relatorios.Modernizations, m => m.RuleId == "MOD-ARCH-FILES"); // \\arquivos\relatorios in VB code (case-insensitive probes)
        Assert.Contains("VB.NET (não convertido)", result.Architecture!.Summary);

        // Web Forms page inside the MVC project: sized, moved to _Legacy, MVC/API still migrate
        Assert.Contains(web.Inventory, i => i.RuleId == "WEB-WEBFORMS" && i.Title.Contains("1 página(s) .aspx") && i.Suggestion.Contains("dias"));
        Assert.Contains(web.Modernizations, m => m.RuleId == "MOD-ARCH-WEBFORMS");
        Assert.Equal(AwsHosting.EcsFargate, web.Hosting!.Primary); // mixed MVC + 1 aspx: stays Linux, with a rewrite prerequisite
        Assert.Contains(web.Hosting.Prerequisites, p => p.Contains("Web Forms"));

        var arch = result.Architecture!;
        var services = arch.Components.Select(c => c.Id).ToList();
        foreach (var id in new[] { "ecs", "ecr", "alb", "rds-sqlserver", "s3", "ses", "elasticache", "secrets", "ssm", "cloudwatch", "eventbridge", "vpn", "cicd", "lambda", "s3-events", "storage-gateway", "ses-inbound" })
            Assert.Contains(id, services);
        Assert.Equal(["LegacyShop.Importador"], arch.Components.Single(c => c.Id == "lambda").UsedBy);
        Assert.Contains("LegacyShop_Importador", arch.Diagram);
        Assert.Contains("ses_inbound -- e-mail recebido --> LegacyShop_Importador", arch.Diagram);
        Assert.Equal(["LegacyShop.Importador", "LegacyShop.Relatorios", "LegacyShop.Web", "LegacyShop.Worker"], arch.Components.Single(c => c.Id == "ses").UsedBy); // Importador ships Core (sends e-mail); the VB report tool uses SmtpClient
        Assert.Contains("erp.interno", arch.Components.Single(c => c.Id == "vpn").Replaces);
        Assert.Contains("embutidas no código", arch.Components.Single(c => c.Id == "secrets").Replaces);
        Assert.Contains("LegacyShop_Web --> rds_sqlserver", arch.Diagram);
        Assert.Contains("eventbridge -- agenda --> LegacyShop_Worker", arch.Diagram);
        Assert.Contains("ECS Fargate (Linux) + ALB", arch.Summary);
        Assert.NotEmpty(arch.Phases);
        Assert.Contains(arch.Risks, r => r.Contains("Integrated Security"));

        var markdown = File.ReadAllText(Path.Combine(reportDir, "migration-report.md"));
        Assert.Contains("## Arquitetura alvo (AWS)", markdown);
        Assert.Contains("```mermaid", markdown);
        Assert.Contains("## Modernização", markdown);
    }

    [Fact]
    public async Task Cloud_none_disables_architecture_and_cloud_items()
    {
        var result = await new MigrationEngine().RunAsync(new MigrationOptions
        {
            InputPath = SampleSolution(), DryRun = true, Offline = true, ReportDir = Path.Combine(_work, "report-none"), Cloud = CloudTarget.None
        });
        Assert.Null(result.Architecture);
        Assert.All(result.Projects, p => Assert.Null(p.Hosting));
        Assert.DoesNotContain(result.AllModernizations, m => m.Kind == ModernizationKind.Cloud);
        Assert.Contains(result.AllModernizations, m => m.RuleId == "MOD-PKG-AUTOMAPPER");
        Assert.DoesNotContain(result.AllItems, i => i.RuleId == "AWS-DOCKERFILE");
    }

    [Fact]
    public async Task Migrate_produces_an_sdk_style_solution()
    {
        var output = Path.Combine(_work, "LegacyShop.net10");

        var result = await new MigrationEngine().RunAsync(new MigrationOptions
        {
            InputPath = SampleSolution(), OutputDir = output, Offline = true, VerifyBuild = false
        });

        string Read(string relative) => File.ReadAllText(Path.Combine(output, relative));
        bool Exists(string relative) => File.Exists(Path.Combine(output, relative));

        Assert.True(Exists("LegacyShop.slnx"));
        Assert.True(Exists(".migrator-output"));
        Assert.True(Exists("lib/Legacy.Barcode.dll"));
        Assert.True(Exists("Shared/VersaoInfo.cs"));

        Assert.Contains("<Project Sdk=\"Microsoft.NET.Sdk.Web\">", Read("LegacyShop.Web/LegacyShop.Web.csproj"));
        Assert.Contains("app.MapControllerRoute(name: \"Default\", pattern: \"{controller=Home}/{action=Index}/{id?}\");", Read("LegacyShop.Web/Program.cs"));
        Assert.Contains("options.Filters.Add(new RequireHttpsAttribute());", Read("LegacyShop.Web/Program.cs"));
        Assert.True(Exists("LegacyShop.Web/wwwroot/Scripts/jquery-3.4.1.js"));
        Assert.True(Exists("LegacyShop.Web/_Legacy/Global.asax.cs"));
        Assert.True(Exists("LegacyShop.Web/_Legacy/Relatorios/Vendas.aspx"));
        Assert.True(Exists("LegacyShop.Web/_Legacy/Relatorios/Vendas.aspx.cs"));
        Assert.False(Exists("LegacyShop.Web/Relatorios/Vendas.aspx.cs"));
        Assert.False(Exists("LegacyShop.Relatorios/LegacyShop.Relatorios.vbproj"));
        Assert.DoesNotContain("Relatorios", Read("LegacyShop.slnx"));
        Assert.False(Exists("LegacyShop.Web/Controllers/LegadoController.cs"));
        Assert.False(Exists("LegacyShop.Web/packages.config"));
        Assert.Contains("Encrypt=False", Read("LegacyShop.Web/appsettings.Production.json"));

        // Credentials leave the appsettings and land in _secrets/ (outside git and Docker)
        var appsettings = Read("LegacyShop.Web/appsettings.json");
        Assert.DoesNotContain("Senha@123", appsettings);
        Assert.DoesNotContain("SmtpSenha!", appsettings);
        Assert.DoesNotContain("sk_test_123456789", appsettings);
        Assert.Contains("<secret: legacyshop/legacyshop.web/ConnectionStrings/RelatoriosConnection>", appsettings);
        Assert.Contains("Integrated Security=True", appsettings); // LocalDb connection string has no password: untouched
        Assert.Contains("Senha@123", Read("_secrets/LegacyShop.Web/appsettings.Secrets.json"));
        Assert.True(Exists("_secrets/LegacyShop.Web/create-secrets.sh"));
        Assert.True(Exists("_secrets/LegacyShop.Web/ecs-task-secrets.json"));
        Assert.True(Exists("_secrets/LegacyShop.Importador/appsettings.Secrets.json"));
        Assert.Contains("<UserSecretsId>", Read("LegacyShop.Web/LegacyShop.Web.csproj"));
        Assert.DoesNotContain("<UserSecretsId>", Read("LegacyShop.Core/LegacyShop.Core.csproj"));
        Assert.Contains("_secrets/", Read(".gitignore"));
        Assert.Contains("_secrets/", Read(".dockerignore"));
        Assert.Contains(result.Projects[0].Inventory, i => i.RuleId == "CFG-SECRETS-EXTRACTED" && i.AutoMigrated);
        Assert.DoesNotContain(result.Projects[0].Inventory, i => i.RuleId == "CFG-SECRETS");
        Assert.Contains("<script src=\"~/Scripts/jquery-3.4.1.js\" asp-append-version=\"true\"></script>", Read("LegacyShop.Web/Views/Shared/_Layout.cshtml"));

        var core = Read("LegacyShop.Core/LegacyShop.Core.csproj");
        Assert.Contains("<Compile Include=\"..\\Shared\\VersaoInfo.cs\" Link=\"Properties\\VersaoInfo.cs\" />", core);
        Assert.Contains("<EmbeddedResource Include=\"Sql\\ConsultaDestaques.sql\" />", core);
        Assert.Contains("<SolutionDir Condition=", core);

        Assert.True(Exists("LegacyShop.Web/Dockerfile"));
        Assert.True(Exists("LegacyShop.Worker/Dockerfile"));
        Assert.False(Exists("LegacyShop.Core/Dockerfile"));
        Assert.False(Exists("LegacyShop.Importador/Dockerfile")); // Lambda: packaged with Amazon.Lambda.Tools instead
        Assert.True(Exists("LegacyShop.Importador/LegacyShop.Importador.csproj"));
        Assert.Contains("<PackageReference Include=\"CsvHelper\"", Read("LegacyShop.Importador/LegacyShop.Importador.csproj"));
        Assert.True(Exists(".dockerignore"));
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:10.0 AS final", Read("LegacyShop.Web/Dockerfile"));
        Assert.Contains("app.MapHealthChecks(\"/health\")", Read("LegacyShop.Web/Program.cs"));
        Assert.Contains(result.Projects[0].Inventory, i => i.RuleId == "AWS-DOCKERFILE" && i.AutoMigrated);

        var worker = Read("LegacyShop.Worker/LegacyShop.Worker.csproj");
        // Windows Service converted to a Worker Service: Linux-capable TFM, generic host packages, no ServiceController
        Assert.Contains("<TargetFramework>net10.0</TargetFramework>", worker);
        Assert.Contains("Microsoft.Extensions.Hosting.WindowsServices", worker);
        Assert.DoesNotContain("System.ServiceProcess.ServiceController", worker);
        var service = Read("LegacyShop.Worker/SincronizacaoService.cs");
        Assert.Contains(": BackgroundService", service);
        Assert.Contains("protected override Task ExecuteAsync(CancellationToken stoppingToken)", service);
        Assert.Contains("public override Task StopAsync(CancellationToken cancellationToken)", service);
        Assert.DoesNotContain("InitializeComponent();", service);
        Assert.Contains("AddHostedService<SincronizacaoService>()", Read("LegacyShop.Worker/Program.cs"));
        Assert.Contains("ServiceName = \"LegacyShopSincronizacao\"", Read("LegacyShop.Worker/Program.cs"));
        Assert.True(Exists("LegacyShop.Worker/_Legacy/Program.cs"));
        Assert.True(Exists("LegacyShop.Worker/_Legacy/SincronizacaoService.Designer.cs"));
        Assert.False(Exists("LegacyShop.Worker/SincronizacaoService.Designer.cs"));
        Assert.Contains(result.Projects[2].Inventory, i => i.RuleId == "CS-WORKER-PROGRAM" && i.AutoMigrated);
        Assert.DoesNotContain(result.Projects[2].Modernizations, m => m.RuleId == "MOD-WIN-SERVICE");
        Assert.DoesNotContain("ServiceBase", Read("LegacyShop.Worker/Dockerfile"));

        // Lambda scaffold for the event-driven automation
        Assert.True(Exists("LegacyShop.Importador/Function.cs"));
        Assert.Contains("FunctionHandler(S3Event evt, ILambdaContext context)", Read("LegacyShop.Importador/Function.cs"));
        Assert.Contains("LegacyShop.Importador::LegacyShop.Importador.Function::FunctionHandler", Read("LegacyShop.Importador/aws-lambda-tools-defaults.json"));
        var importadorCsproj = Read("LegacyShop.Importador/LegacyShop.Importador.csproj");
        Assert.Contains("Amazon.Lambda.Core", importadorCsproj);
        Assert.Contains("Amazon.Lambda.S3Events", importadorCsproj);
        Assert.Contains("<AWSProjectType>Lambda</AWSProjectType>", importadorCsproj);
        Assert.Contains("<Deterministic>false</Deterministic>", worker);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", worker);
        Assert.True(Exists("LegacyShop.Worker/App.config"));
        Assert.True(Exists("LegacyShop.Worker/_Legacy/ProjectInstaller.Designer.cs"));
        Assert.Contains("// Serviço de sincronização", Read("LegacyShop.Worker/SincronizacaoService.cs"));

        Assert.Contains("MSTest.TestAdapter", Read("LegacyShop.Tests/LegacyShop.Tests.csproj"));
        Assert.All(result.Projects.Where(p => !p.Project.IsVisualBasic), p => Assert.NotNull(p.OutputProjectPath));
    }

    [Fact]
    public async Task Output_inside_the_source_tree_is_rejected()
    {
        var sample = SampleSolution();
        var inside = Path.Combine(Path.GetDirectoryName(sample)!, "out");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => new MigrationEngine().RunAsync(new MigrationOptions
        {
            InputPath = sample, OutputDir = inside, Offline = true, VerifyBuild = false
        }));

        Assert.Contains("não pode ficar dentro", ex.Message);
        Assert.False(Directory.Exists(inside));
    }
}
