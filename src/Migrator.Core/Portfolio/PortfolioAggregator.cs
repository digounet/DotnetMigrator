using System.Text.Json;
using Migrator.Core.Cloud;
using Migrator.Core.Models;
using Migrator.Core.Reporting;

namespace Migrator.Core.Portfolio;

/// <summary>Turns N solution results into one portfolio view: ranking, shared gaps, shared infrastructure, waves and baseline deltas.</summary>
public static class PortfolioAggregator
{
    public const int LowBandMax = 25;
    public const int MediumBandMax = 70;

    /// <summary>
    /// Reference effort score, meant to order applications and group them in bands, not to estimate hours:
    /// blocking items ×3, warnings ×1, high-impact modernizations ×2, +10 if Windows containers are required,
    /// +5 per Web Forms file, +8 per VB.NET project, +3 for each web project (routing/auth/session work).
    /// </summary>
    public static int Score(PortfolioApp app) =>
        app.Breaking * 3 + app.Warnings + app.HighImpact * 2 + (app.RequiresWindows ? 10 : 0) + app.WebFormsFiles * 5 + app.VbProjects * 8 + (app.Kinds.GetValueOrDefault("Web") * 3);

    public static EffortBand Band(int score) => score <= LowBandMax ? EffortBand.Low : score <= MediumBandMax ? EffortBand.Medium : EffortBand.High;

    public static PortfolioApp Summarize(string name, string inputPath, SolutionResult result)
    {
        var app = new PortfolioApp { Name = name, InputPath = inputPath, Result = result, ReportDir = result.ReportDir };
        app.Projects = result.Projects.Count;
        foreach (var g in result.Projects.GroupBy(p => p.Project.Kind.ToString())) app.Kinds[g.Key] = g.Count();
        app.Breaking = result.AllItems.Count(i => i.RequiresAction && i.Severity == InventorySeverity.Breaking);
        app.Warnings = result.AllItems.Count(i => i.RequiresAction && i.Severity == InventorySeverity.Warning);
        app.Automatic = result.AllItems.Count(i => i.AutoMigrated);
        var manual = result.AllItems.Count(i => i.RequiresAction && i.Category != InventoryCategory.Build);
        app.AutomationPercent = app.Automatic + manual == 0 ? 100 : (int)Math.Round(app.Automatic * 100.0 / (app.Automatic + manual));
        app.Modernizations = result.AllModernizations.Count();
        app.HighImpact = result.AllModernizations.Count(m => m.Impact == Impact.High);
        app.LicenseIssues = result.AllModernizations.Count(m => m.Kind == ModernizationKind.License);
        app.WebFormsFiles = result.Projects.SelectMany(p => p.Inventory).Where(i => i.RuleId == "WEB-WEBFORMS").Sum(i => WebFormsCount(i.Title));
        app.VbProjects = result.Projects.Count(p => p.Project.IsVisualBasic);
        app.RequiresWindows = result.Projects.Any(p => p.Hosting is { RequiresWindows: true });
        foreach (var g in result.Projects.Where(p => p.Hosting != null && p.Hosting.Primary != AwsHosting.NotDeployable).GroupBy(p => p.Hosting!.Primary.Short()))
            app.Hosting[g.Key] = g.Count();
        app.Databases.AddRange(result.Databases.Where(d => !(d.Server ?? "").Contains("localdb", StringComparison.OrdinalIgnoreCase))
            .Select(d => $"{d.Provider}: {(d.Server ?? "?").Split(',')[0]}/{d.Database ?? d.Name}").Distinct(StringComparer.OrdinalIgnoreCase));
        app.InternalHosts.AddRange(result.InternalHosts);
        if (result.Architecture != null) app.TopRisks.AddRange(result.Architecture.Risks.Take(3));
        app.EffortScore = Score(app);
        app.Effort = Band(app.EffortScore);
        return app;
    }

    private static int WebFormsCount(string title)
    {
        // "Web Forms: 3 página(s) .aspx, 2 controle(s) .ascx, 1 master page(s)..."
        var total = 0;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(title, @"(\d+) (página|controle|master)"))
            total += int.Parse(m.Groups[1].Value);
        return total;
    }

    public static PortfolioResult Aggregate(string rootDir, IEnumerable<PortfolioApp> apps, string? baselineJson = null, string? baselinePath = null)
    {
        var result = new PortfolioResult { RootDir = rootDir, BaselinePath = baselinePath };
        result.Apps.AddRange(apps.OrderByDescending(a => a.EffortScore).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase));
        var analyzed = result.Apps.Where(a => a.Result != null).ToList();

        // Gaps across the portfolio: how many applications share each rule
        result.InventoryGaps.AddRange(analyzed
            .SelectMany(a => a.Result!.AllItems.Where(i => i.RequiresAction).Select(i => (App: a.Name, Item: i)))
            .GroupBy(x => x.Item.RuleId, StringComparer.Ordinal)
            .Select(g => new PortfolioGap(g.Key, Shorten(g.First().Item.Title), "inventário", g.First().Item.Severity.Display(),
                g.Select(x => x.App).Distinct().Count(), g.Sum(x => x.Item.Occurrences), g.Select(x => x.App).Distinct().OrderBy(n => n).ToList(), null))
            .OrderByDescending(g => g.Apps).ThenByDescending(g => g.Occurrences).ThenBy(g => g.RuleId, StringComparer.Ordinal));
        result.ModernizationGaps.AddRange(analyzed
            .SelectMany(a => a.Result!.AllModernizations.Select(m => (App: a.Name, Item: m)))
            .GroupBy(x => x.Item.RuleId, StringComparer.Ordinal)
            .Select(g => new PortfolioGap(g.Key, g.First().Item.Title, g.First().Item.Kind.Display(), g.First().Item.Impact.Display(),
                g.Select(x => x.App).Distinct().Count(), g.Sum(x => x.Item.Occurrences), g.Select(x => x.App).Distinct().OrderBy(n => n).ToList(), g.First().Item.AwsService))
            .OrderByDescending(g => g.Apps).ThenBy(g => g.Severity == "Alto" ? 0 : 1).ThenBy(g => g.RuleId, StringComparer.Ordinal));

        // Shared infrastructure: the same database or internal host used by more than one application
        result.Shared.AddRange(analyzed.SelectMany(a => a.Databases.Select(d => (Kind: "banco", Name: d, App: a.Name)))
            .Concat(analyzed.SelectMany(a => a.InternalHosts.Select(h => (Kind: "host interno", Name: h, App: a.Name))))
            .GroupBy(x => (x.Kind, x.Name), StringTupleComparer.Instance)
            .Where(g => g.Select(x => x.App).Distinct().Count() > 1)
            .Select(g => new SharedResource(g.Key.Kind, g.Key.Name, g.Select(x => x.App).Distinct().OrderBy(n => n).ToList()))
            .OrderByDescending(s => s.Apps.Count).ThenBy(s => s.Name));

        foreach (var (key, n) in analyzed.SelectMany(a => a.Hosting).GroupBy(kv => kv.Key).Select(g => (g.Key, g.Sum(kv => kv.Value)))) result.HostingTotals[key] = n;
        foreach (var g in analyzed.Where(a => a.Result!.Architecture != null).SelectMany(a => a.Result!.Architecture!.Components.Where(c => c.Required).Select(c => c.Service)).GroupBy(s => s))
            result.AwsServices[g.Key] = g.Count();

        BuildWaves(result, analyzed);
        if (baselineJson != null) result.Baseline = CompareWithBaseline(result, baselineJson);
        return result;
    }

    /// <summary>
    /// Three waves by cumulative effort (quick wins first). Applications that share a database are kept in the same wave,
    /// because moving the database to RDS is a joint cutover.
    /// </summary>
    private static void BuildWaves(PortfolioResult result, List<PortfolioApp> apps)
    {
        if (apps.Count == 0) return;
        var parent = apps.ToDictionary(a => a.Name, a => a.Name, StringComparer.OrdinalIgnoreCase);
        string Find(string x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        foreach (var shared in result.Shared.Where(s => s.Kind == "banco"))
            for (var i = 1; i < shared.Apps.Count; i++) parent[Find(shared.Apps[i])] = Find(shared.Apps[0]);

        var groups = apps.GroupBy(a => Find(a.Name), StringComparer.OrdinalIgnoreCase)
            .Select(g => (Apps: g.OrderBy(a => a.EffortScore).ToList(), Effort: g.Sum(a => a.EffortScore), MaxBand: g.Max(a => a.Effort)))
            .OrderBy(g => g.MaxBand).ThenBy(g => g.Effort).ToList();

        var total = groups.Sum(g => g.Effort);
        var waves = new[] { new PortfolioWave { Number = 1 }, new PortfolioWave { Number = 2 }, new PortfolioWave { Number = 3 } };
        var cumulative = 0;
        foreach (var group in groups)
        {
            var wave = total == 0 ? waves[0] : cumulative < total / 3.0 ? waves[0] : cumulative < 2 * total / 3.0 ? waves[1] : waves[2];
            if (group.MaxBand == EffortBand.High && wave.Number == 1) wave = waves[1];
            foreach (var app in group.Apps)
            {
                app.Wave = wave.Number;
                app.WaveReason = group.Apps.Count > 1 ? $"compartilha banco com {string.Join(", ", group.Apps.Where(o => o != app).Select(o => o.Name))}" : null;
                wave.Apps.Add(app.Name);
            }
            wave.EffortTotal += group.Effort;
            cumulative += group.Effort;
        }
        waves[0].Rationale = "Quick wins: aplicações de menor esforço, para validar a esteira (pipeline, ECS, RDS, Secrets Manager) com risco baixo.";
        waves[1].Rationale = "Esforço médio e grupos que compartilham banco: migram juntos para o cutover do RDS acontecer uma vez.";
        waves[2].Rationale = "Maior esforço: dependências Windows, Web Forms, VB.NET ou muitos itens bloqueantes; começam pela modernização enquanto as ondas anteriores rodam.";
        result.Waves.AddRange(waves.Where(w => w.Apps.Count > 0));
    }

    private static List<AppDelta> CompareWithBaseline(PortfolioResult current, string baselineJson)
    {
        var deltas = new List<AppDelta>();
        List<PortfolioApp>? before;
        try
        {
            using var doc = JsonDocument.Parse(baselineJson);
            var appsElement = doc.RootElement.TryGetProperty("apps", out var a) ? a : doc.RootElement.GetProperty("Apps");
            before = JsonSerializer.Deserialize<List<PortfolioApp>>(appsElement.GetRawText(), JsonReport.Options);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return [new AppDelta("(baseline)", 0, 0, 0, 0, 0, 0, 0, 0, $"baseline inválida: {ex.Message}")];
        }
        before ??= [];
        foreach (var app in current.Apps)
        {
            var old = before.FirstOrDefault(b => b.Name.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
            if (old == null) { deltas.Add(new AppDelta(app.Name, 0, app.Breaking, 0, app.Warnings, 0, app.HighImpact, 0, app.EffortScore, "nova")); continue; }
            var status = app.EffortScore < old.EffortScore ? "melhorou" : app.EffortScore > old.EffortScore ? "piorou" : "igual";
            deltas.Add(new AppDelta(app.Name, old.Breaking, app.Breaking, old.Warnings, app.Warnings, old.HighImpact, app.HighImpact, old.EffortScore, app.EffortScore, status));
        }
        foreach (var old in before.Where(b => current.Apps.All(a => !a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase))))
            deltas.Add(new AppDelta(old.Name, old.Breaking, 0, old.Warnings, 0, old.HighImpact, 0, old.EffortScore, 0, "removida"));
        return deltas;
    }

    private static string Shorten(string title) => title.Length <= 90 ? title : title[..90] + "...";

    private sealed class StringTupleComparer : IEqualityComparer<(string Kind, string Name)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Kind, string Name) x, (string Kind, string Name) y) => x.Kind == y.Kind && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Kind, string Name) obj) => HashCode.Combine(obj.Kind, obj.Name.ToLowerInvariant());
    }
}
