using System.Text.Json.Serialization;
using Migrator.Core.Llm;
using Migrator.Core.Models;

namespace Migrator.Core.Portfolio;

public sealed record PortfolioOptions
{
    public required string RootDir { get; init; }
    public string? ReportDir { get; init; }
    public bool Offline { get; init; }
    public CloudTarget Cloud { get; init; } = CloudTarget.Aws;
    /// <summary>Net10 (default) or NetFramework: changes the hosting recommendation of every app (EC2 Windows lift-and-shift).</summary>
    public MigrationTarget Target { get; init; } = MigrationTarget.NetFramework;
    public bool Serverless { get; init; }
    public LlmOptions Llm { get; init; } = new();
    public string? NuGetConfigPath { get; init; }
    public string? NuGetSourceUrl { get; init; }
    /// <summary>A previous portfolio.json to compare against (evolution between tool versions or between remediation rounds).</summary>
    public string? BaselinePath { get; init; }
}

public enum EffortBand { Low, Medium, High }

public sealed class PortfolioApp
{
    public required string Name { get; init; }
    public required string InputPath { get; init; }
    public string? ReportDir { get; set; }
    public string? Error { get; set; }

    public int Projects { get; set; }
    public Dictionary<string, int> Kinds { get; } = new(StringComparer.Ordinal);
    public int Breaking { get; set; }
    public int Warnings { get; set; }
    public int Automatic { get; set; }
    public int AutomationPercent { get; set; }
    public int Modernizations { get; set; }
    public int HighImpact { get; set; }
    public int LicenseIssues { get; set; }
    public int WebFormsFiles { get; set; }
    public int VbProjects { get; set; }
    public bool RequiresWindows { get; set; }
    public Dictionary<string, int> Hosting { get; } = new(StringComparer.Ordinal);
    public List<string> Databases { get; } = [];
    public List<string> InternalHosts { get; } = [];
    public List<string> TopRisks { get; } = [];

    /// <summary>Reference score (see <see cref="PortfolioAggregator.Score"/>), not hours: sorts the apps and defines the band.</summary>
    public int EffortScore { get; set; }
    public EffortBand Effort { get; set; }
    public int Wave { get; set; }
    public string? WaveReason { get; set; }

    [JsonIgnore] public SolutionResult? Result { get; set; }
}

public sealed record PortfolioGap(string RuleId, string Title, string Source, string Severity, int Apps, int Occurrences, List<string> AppNames, string? AwsService);

public sealed record SharedResource(string Kind, string Name, List<string> Apps);

public sealed class PortfolioWave
{
    public int Number { get; init; }
    public List<string> Apps { get; } = [];
    public int EffortTotal { get; set; }
    public string Rationale { get; set; } = string.Empty;
}

public sealed record AppDelta(string App, int BreakingBefore, int BreakingAfter, int WarningsBefore, int WarningsAfter, int HighImpactBefore, int HighImpactAfter, int EffortBefore, int EffortAfter, string Status);

public sealed class PortfolioResult
{
    public required string RootDir { get; init; }
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public string? ReportDir { get; set; }
    public List<PortfolioApp> Apps { get; } = [];
    public List<PortfolioGap> InventoryGaps { get; } = [];
    public List<PortfolioGap> ModernizationGaps { get; } = [];
    public List<SharedResource> Shared { get; } = [];
    public List<PortfolioWave> Waves { get; } = [];
    public Dictionary<string, int> HostingTotals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> AwsServices { get; } = new(StringComparer.Ordinal);
    public List<AppDelta>? Baseline { get; set; }
    public string? BaselinePath { get; set; }

    public int TotalBreaking => Apps.Sum(a => a.Breaking);
    public int TotalWarnings => Apps.Sum(a => a.Warnings);
    public int TotalHighImpact => Apps.Sum(a => a.HighImpact);
}

public static class PortfolioText
{
    public static string Display(this EffortBand band) => band switch { EffortBand.Low => "Baixo", EffortBand.Medium => "Médio", _ => "Alto" };
}
