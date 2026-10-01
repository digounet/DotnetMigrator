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

        Assert.Equal(["LegacyShop.Web", "LegacyShop.Core", "LegacyShop.Worker", "LegacyShop.Tests"], result.Projects.Select(p => p.Project.Name));
        Assert.Contains(result.GlobalItems, i => i.RuleId == "SLN-SKIPPED" && i.Title.Contains(".vbproj"));
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

        foreach (var file in new[] { "migration-report.html", "migration-report.md", "inventory.csv", "inventory.xlsx" })
            Assert.True(File.Exists(Path.Combine(reportDir, file)), file);
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
        Assert.False(Exists("LegacyShop.Web/Controllers/LegadoController.cs"));
        Assert.False(Exists("LegacyShop.Web/packages.config"));
        Assert.Contains("Encrypt=False", Read("LegacyShop.Web/appsettings.Production.json"));
        Assert.Contains("<script src=\"~/Scripts/jquery-3.4.1.js\" asp-append-version=\"true\"></script>", Read("LegacyShop.Web/Views/Shared/_Layout.cshtml"));

        var core = Read("LegacyShop.Core/LegacyShop.Core.csproj");
        Assert.Contains("<Compile Include=\"..\\Shared\\VersaoInfo.cs\" Link=\"Properties\\VersaoInfo.cs\" />", core);
        Assert.Contains("<EmbeddedResource Include=\"Sql\\ConsultaDestaques.sql\" />", core);
        Assert.Contains("<SolutionDir Condition=", core);

        var worker = Read("LegacyShop.Worker/LegacyShop.Worker.csproj");
        Assert.Contains("<TargetFramework>net10.0-windows</TargetFramework>", worker);
        Assert.Contains("<Deterministic>false</Deterministic>", worker);
        Assert.Contains("<PlatformTarget>x64</PlatformTarget>", worker);
        Assert.True(Exists("LegacyShop.Worker/App.config"));
        Assert.True(Exists("LegacyShop.Worker/_Legacy/ProjectInstaller.Designer.cs"));
        Assert.Contains("// Serviço de sincronização", Read("LegacyShop.Worker/SincronizacaoService.cs"));

        Assert.Contains("MSTest.TestAdapter", Read("LegacyShop.Tests/LegacyShop.Tests.csproj"));
        Assert.All(result.Projects, p => Assert.NotNull(p.OutputProjectPath));
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
