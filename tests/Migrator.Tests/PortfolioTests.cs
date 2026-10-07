using System.Text.Json;
using Migrator.Core.Analysis;
using Migrator.Core.Models;
using Migrator.Core.Portfolio;
using Migrator.Core.Reporting;

namespace Migrator.Tests;

public sealed class PortfolioTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("migrator-portfolio-").FullName;

    public void Dispose() => Directory.Delete(_work, recursive: true);

    private static SolutionResult Solution(string name, int breaking, int warnings, int highImpact, bool windows = false, params (string Server, string Db)[] databases)
    {
        var result = new SolutionResult { Options = new MigrationOptions { Target = MigrationTarget.Net10, InputPath = name }, RootDir = "/x/" + name, SolutionName = name };
        var project = new ProjectResult
        {
            Project = new ProjectInfo { ProjectPath = $"/x/{name}/{name}.Web/{name}.Web.csproj", Name = name + ".Web", Kind = ProjectKind.Web },
            Hosting = new HostingRecommendation { Project = name + ".Web", Kind = ProjectKind.Web, Primary = windows ? AwsHosting.EcsWindows : AwsHosting.EcsFargate }
        };
        if (windows) project.Hosting.HardWindowsDependencies.Add("COM");
        for (var i = 0; i < breaking; i++) project.Inventory.Add(new InventoryItem { Project = project.Project.Name, RuleId = "WEB00" + (i % 3), Severity = InventorySeverity.Breaking, Category = InventoryCategory.Code, Title = "Bloqueante " + i });
        for (var i = 0; i < warnings; i++) project.Inventory.Add(new InventoryItem { Project = project.Project.Name, RuleId = "CFG-X", Severity = InventorySeverity.Warning, Category = InventoryCategory.Configuration, Title = "Atenção" });
        project.Inventory.Add(new InventoryItem { Project = project.Project.Name, RuleId = "PRJ-SDK", Severity = InventorySeverity.Info, Category = InventoryCategory.ProjectFile, Title = "ok", AutoMigrated = true });
        for (var i = 0; i < highImpact; i++) project.Modernizations.Add(new ModernizationItem { Project = project.Project.Name, RuleId = "MOD-PKG-AUTOMAPPER", Kind = ModernizationKind.License, Impact = Impact.High, Title = "AutoMapper" });
        result.Projects.Add(project);
        foreach (var (server, db) in databases) result.Databases.Add(new DatabaseUse("SQL Server", server, db, false, db, project.Project.Name));
        result.Architecture = new ArchitectureProposal();
        result.Architecture.Components.Add(new AwsComponent { Id = "ecs", Service = "Amazon ECS on AWS Fargate", Role = "x", Required = true });
        result.Architecture.Risks.Add("Risco 1");
        return result;
    }

    [Fact]
    public void Scores_bands_and_orders_applications_by_effort()
    {
        var small = PortfolioAggregator.Summarize("Pequena", "/x/p", Solution("Pequena", breaking: 2, warnings: 3, highImpact: 1));
        var big = PortfolioAggregator.Summarize("Grande", "/x/g", Solution("Grande", breaking: 20, warnings: 15, highImpact: 6, windows: true));

        Assert.Equal(2 * 3 + 3 + 1 * 2 + 3, small.EffortScore);           // +3 for the web project
        Assert.Equal(EffortBand.Low, small.Effort);
        Assert.Equal(20 * 3 + 15 + 6 * 2 + 10 + 3, big.EffortScore);
        Assert.Equal(EffortBand.High, big.Effort);
        Assert.True(big.RequiresWindows);
        Assert.Equal(1, big.Hosting["ECS Windows"]);

        var portfolio = PortfolioAggregator.Aggregate("/x", [small, big]);
        Assert.Equal(["Grande", "Pequena"], portfolio.Apps.Select(a => a.Name));
        Assert.Equal(22, portfolio.TotalBreaking);
        Assert.Equal(2, portfolio.AwsServices["Amazon ECS on AWS Fargate"]);
        var gap = portfolio.ModernizationGaps.Single(g => g.RuleId == "MOD-PKG-AUTOMAPPER");
        Assert.Equal(2, gap.Apps);
        Assert.Equal(7, gap.Occurrences);
        Assert.Contains(portfolio.InventoryGaps, g => g.RuleId == "WEB000" && g.Apps == 2);
    }

    [Fact]
    public void Applications_sharing_a_database_land_in_the_same_wave_and_quick_wins_come_first()
    {
        var apps = new[]
        {
            PortfolioAggregator.Summarize("A", "/x/a", Solution("A", 1, 1, 0, databases: ("srv-sql01", "Vendas"))),
            PortfolioAggregator.Summarize("B", "/x/b", Solution("B", 30, 10, 5, databases: ("srv-sql01", "Vendas"))),   // shares Vendas with A
            PortfolioAggregator.Summarize("C", "/x/c", Solution("C", 2, 0, 0)),
            PortfolioAggregator.Summarize("D", "/x/d", Solution("D", 40, 20, 8, windows: true))
        };
        var portfolio = PortfolioAggregator.Aggregate("/x", apps);

        var shared = Assert.Single(portfolio.Shared);
        Assert.Equal("banco", shared.Kind);
        Assert.Equal(["A", "B"], shared.Apps);

        var a = portfolio.Apps.Single(x => x.Name == "A");
        var b = portfolio.Apps.Single(x => x.Name == "B");
        Assert.Equal(a.Wave, b.Wave);
        Assert.Contains("compartilha banco com B", a.WaveReason);
        Assert.Equal(1, portfolio.Apps.Single(x => x.Name == "C").Wave);
        Assert.True(portfolio.Apps.Single(x => x.Name == "D").Wave >= 2);
        Assert.Equal(portfolio.Apps.Count, portfolio.Waves.Sum(w => w.Apps.Count));
    }

    [Fact]
    public void Baseline_comparison_reports_improvements_regressions_new_and_removed_apps()
    {
        var before = PortfolioAggregator.Aggregate("/x", [
            PortfolioAggregator.Summarize("A", "/x/a", Solution("A", 10, 5, 2)),
            PortfolioAggregator.Summarize("Antiga", "/x/o", Solution("Antiga", 1, 1, 1))]);
        var baselineJson = PortfolioReports.Json(before);

        var after = PortfolioAggregator.Aggregate("/x", [
            PortfolioAggregator.Summarize("A", "/x/a", Solution("A", 3, 5, 1)),
            PortfolioAggregator.Summarize("Nova", "/x/n", Solution("Nova", 4, 0, 0))], baselineJson, "baseline.json");

        Assert.NotNull(after.Baseline);
        Assert.Equal("melhorou", after.Baseline!.Single(d => d.App == "A").Status);
        Assert.Equal((10, 3), (after.Baseline.Single(d => d.App == "A").BreakingBefore, after.Baseline.Single(d => d.App == "A").BreakingAfter));
        Assert.Equal("nova", after.Baseline.Single(d => d.App == "Nova").Status);
        Assert.Equal("removida", after.Baseline.Single(d => d.App == "Antiga").Status);
    }

    [Fact]
    public void Discovers_one_application_per_solution_and_directories_without_solutions()
    {
        Directory.CreateDirectory(Path.Combine(_work, "Loja", "Loja.Web"));
        File.WriteAllText(Path.Combine(_work, "Loja", "Loja.sln"), "");
        File.WriteAllText(Path.Combine(_work, "Loja", "Loja.slnx"), "<Solution/>");            // .slnx wins over .sln in the same folder
        File.WriteAllText(Path.Combine(_work, "Loja", "Loja.Web", "Loja.Web.csproj"), "<Project/>");
        Directory.CreateDirectory(Path.Combine(_work, "Robo"));
        File.WriteAllText(Path.Combine(_work, "Robo", "Robo.csproj"), "<Project/>");            // no solution: the folder is the app
        Directory.CreateDirectory(Path.Combine(_work, "Loja.net10"));
        File.WriteAllText(Path.Combine(_work, "Loja.net10", WorkspaceLoader.OutputMarkerFile), "");
        File.WriteAllText(Path.Combine(_work, "Loja.net10", "Loja.slnx"), "<Solution/>");      // previous Migrator output: skipped
        Directory.CreateDirectory(Path.Combine(_work, "Loja", "packages", "X"));
        File.WriteAllText(Path.Combine(_work, "Loja", "packages", "X", "X.csproj"), "<Project/>");

        var inputs = PortfolioRunner.Discover(_work);

        Assert.Equal(["Loja", "Robo"], inputs.Select(i => i.Name));
        Assert.EndsWith("Loja.slnx", inputs[0].Input);
        Assert.Equal(Path.Combine(_work, "Robo"), inputs[1].Input);
    }

    [Fact]
    public async Task Portfolio_over_the_samples_folder_analyzes_legacyshop_and_writes_reports()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Migrator.slnx"))) dir = dir.Parent;
        var samples = Path.Combine(dir!.FullName, "samples");
        var report = Path.Combine(_work, "portfolio");

        var result = await new PortfolioRunner().RunAsync(new PortfolioOptions { Target = MigrationTarget.Net10, Serverless = true, RootDir = samples, ReportDir = report, Offline = true });

        var app = Assert.Single(result.Apps);
        Assert.Equal("LegacyShop", app.Name);
        Assert.Null(app.Error);
        Assert.Equal(6, app.Projects);
        Assert.True(app.Breaking > 0);
        Assert.Equal(1, app.Hosting["ECS Fargate"]);
        Assert.Equal(1, app.Hosting["Lambda"]);
        Assert.True(app.RequiresWindows); // the VB report tool uses Excel COM interop
        Assert.Equal(1, app.VbProjects);
        Assert.Equal(1, app.WebFormsFiles);
        Assert.Contains(app.Databases, d => d.Contains("srv-sql01/LegacyShop"));
        Assert.Contains("erp.interno", app.InternalHosts);
        Assert.Equal(EffortBand.High, app.Effort);
        Assert.Contains(result.ModernizationGaps, g => g.RuleId == "MOD-PKG-AUTOMAPPER" && g.Apps == 1);
        Assert.Single(result.Waves);

        foreach (var file in new[] { "portfolio-report.html", "portfolio-report.md", "portfolio.xlsx", "portfolio.json" })
            Assert.True(File.Exists(Path.Combine(report, file)), file);
        Assert.True(File.Exists(Path.Combine(report, "apps", "LegacyShop", "migration-report.html")));
        Assert.True(File.Exists(Path.Combine(report, "apps", "LegacyShop", JsonReport.FileName)));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(report, "apps", "LegacyShop", JsonReport.FileName)));
        Assert.Equal("analyze", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(6, json.RootElement.GetProperty("projects").GetArrayLength());
        Assert.Equal("lambda", json.RootElement.GetProperty("projects")[4].GetProperty("hosting").GetProperty("primary").GetString());
        Assert.True(json.RootElement.GetProperty("architecture").GetProperty("components").GetArrayLength() > 5);
    }
}
