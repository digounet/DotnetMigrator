namespace Migrator.Core.Models;

public sealed record MigrationOptions
{
    public required string InputPath { get; init; }
    public string? OutputDir { get; init; }
    public string? ReportDir { get; init; }
    public bool DryRun { get; init; }
    public bool Offline { get; init; }
    public bool VerifyBuild { get; init; } = true;
    public bool Force { get; init; }
    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>Cloud provider for the architecture proposal and container artifacts. None disables the advisor.</summary>
    public CloudTarget Cloud { get; init; } = CloudTarget.Aws;
    /// <summary>Keep credentials inside the generated appsettings*.json instead of moving them to _secrets/ (not recommended).</summary>
    public bool KeepSecrets { get; init; }
    /// <summary>Optional LLM assistance (build-fix loop, conversion drafts, executive narrative). Disabled by default.</summary>
    public Llm.LlmOptions Llm { get; init; } = new();
}

public sealed class ProjectResult
{
    public required ProjectInfo Project { get; init; }
    public string RelativeDir { get; set; } = string.Empty;
    public string? OutputProjectPath { get; set; }
    public List<InventoryItem> Inventory { get; } = [];
    public List<ModernizationItem> Modernizations { get; } = [];
    public HostingRecommendation? Hosting { get; set; }
    public ProjectBuildStatus? Build { get; set; }

    public IEnumerable<InventoryItem> Breaking => Inventory.Where(i => i.RequiresAction && i.Severity == InventorySeverity.Breaking);
    public IEnumerable<InventoryItem> Warnings => Inventory.Where(i => i.RequiresAction && i.Severity == InventorySeverity.Warning);
    public IEnumerable<InventoryItem> Automatic => Inventory.Where(i => i.AutoMigrated);
    public IEnumerable<InventoryItem> Informational => Inventory.Where(i => !i.AutoMigrated && i.Severity == InventorySeverity.Info);

    public int AutomationPercent
    {
        get
        {
            var auto = Automatic.Count();
            var manual = Inventory.Count(i => i.RequiresAction && i.Category != InventoryCategory.Build);
            return auto + manual == 0 ? 100 : (int)Math.Round(auto * 100.0 / (auto + manual));
        }
    }
}

public sealed record ProjectBuildStatus(int Errors, int Warnings, string? BlockedBy = null)
{
    public bool Succeeded => Errors == 0 && BlockedBy == null;
}

public sealed class SolutionResult
{
    public required MigrationOptions Options { get; init; }
    public required string RootDir { get; init; }
    public string SolutionName { get; set; } = string.Empty;
    public string? OutputDir { get; set; }
    public string? ReportDir { get; set; }
    public List<ProjectResult> Projects { get; } = [];
    public List<InventoryItem> GlobalItems { get; } = [];
    public List<ModernizationItem> GlobalModernizations { get; } = [];
    public ArchitectureProposal? Architecture { get; set; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime FinishedAt { get; set; }
    public bool? BuildSucceeded { get; set; }
    public string? BuildLogPath { get; set; }
    public bool NuGetChecked { get; set; }
    /// <summary>Provider/model used for the LLM-assisted steps, or null when none was configured.</summary>
    public string? LlmModel { get; set; }
    public int LlmCalls { get; set; }

    public IEnumerable<InventoryItem> AllItems => GlobalItems.Concat(Projects.SelectMany(p => p.Inventory));
    public IEnumerable<ModernizationItem> AllModernizations => GlobalModernizations.Concat(Projects.SelectMany(p => p.Modernizations));
}
