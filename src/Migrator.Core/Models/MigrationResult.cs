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
}

public sealed class ProjectResult
{
    public required ProjectInfo Project { get; init; }
    public string RelativeDir { get; set; } = string.Empty;
    public string? OutputProjectPath { get; set; }
    public List<InventoryItem> Inventory { get; } = [];
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
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime FinishedAt { get; set; }
    public bool? BuildSucceeded { get; set; }
    public string? BuildLogPath { get; set; }
    public bool NuGetChecked { get; set; }

    public IEnumerable<InventoryItem> AllItems => GlobalItems.Concat(Projects.SelectMany(p => p.Inventory));
}
