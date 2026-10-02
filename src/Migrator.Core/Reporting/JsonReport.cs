using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

/// <summary>Machine-readable result (migration-result.json) for dashboards, pipelines and the portfolio mode.</summary>
public static class JsonReport
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public const string FileName = "migration-result.json";

    public static string Render(SolutionResult result) => JsonSerializer.Serialize(Build(result), Options);

    public static MigrationResultDto Build(SolutionResult result) => new(
        Solution: result.SolutionName,
        RootDir: result.RootDir,
        OutputDir: result.OutputDir,
        Mode: result.Options.DryRun ? "analyze" : "migrate",
        GeneratedAt: result.FinishedAt,
        NuGetChecked: result.NuGetChecked,
        BuildSucceeded: result.BuildSucceeded,
        Cloud: result.Options.Cloud,
        LlmModel: result.LlmModel,
        LlmCalls: result.LlmCalls,
        Totals: new TotalsDto(
            result.AllItems.Count(i => i.RequiresAction && i.Severity == InventorySeverity.Breaking),
            result.AllItems.Count(i => i.RequiresAction && i.Severity == InventorySeverity.Warning),
            result.AllItems.Count(i => i.AutoMigrated),
            result.AllModernizations.Count(),
            result.AllModernizations.Count(m => m.Impact == Impact.High)),
        Projects: result.Projects.Select(p => new ProjectDto(
            p.Project.Name, p.Project.Kind, p.Project.Language, p.Project.TargetFramework, p.RelativeDir, p.OutputProjectPath,
            p.Breaking.Count(), p.Warnings.Count(), p.Automatic.Count(), p.Informational.Count(), p.AutomationPercent,
            p.Build == null ? null : new BuildDto(p.Build.Errors, p.Build.Warnings, p.Build.BlockedBy, p.Build.Succeeded),
            p.Tests == null ? null : new TestsDto(p.Tests.Passed, p.Tests.Failed, p.Tests.Skipped, p.Tests.Succeeded, p.Tests.FailedTests),
            p.Smoke == null ? null : new SmokeDto(p.Smoke.Succeeded, p.Smoke.StatusCode, p.Smoke.Detail),
            p.DockerBuildSucceeded,
            p.Hosting == null ? null : new HostingDto(p.Hosting.Primary, p.Hosting.Primary.Display(), p.Hosting.RequiresWindows, p.Hosting.HardWindowsDependencies, p.Hosting.SoftWindowsDependencies, p.Hosting.Rationale, p.Hosting.Prerequisites, p.Hosting.Alternatives, p.Hosting.DockerfileGenerated),
            p.Inventory.Select(Item).ToList(),
            p.Modernizations.Select(Modernization).ToList())).ToList(),
        GlobalItems: result.GlobalItems.Select(Item).ToList(),
        GlobalModernizations: result.GlobalModernizations.Select(Modernization).ToList(),
        Databases: result.Databases.Select(d => new DatabaseDto(d.Provider, d.Server, d.Database, d.IntegratedSecurity, d.Name, d.Project)).ToList(),
        InternalHosts: result.InternalHosts.ToList(),
        Architecture: result.Architecture == null ? null : new ArchitectureDto(
            result.Architecture.Summary, result.Architecture.ExecutiveSummary, result.Architecture.ExecutiveSummaryModel,
            result.Architecture.Components.Select(c => new ComponentDto(c.Id, c.Service, c.Role, c.Replaces, c.Why, c.Required, c.UsedBy.ToList(), c.Notes)).ToList(),
            result.Architecture.Phases, result.Architecture.Risks, result.Architecture.CostNotes, result.Architecture.Diagram));

    private static ItemDto Item(InventoryItem i) => new(i.Project, i.RuleId, i.Severity, i.Category, i.AutoMigrated, i.RequiresAction, i.Title, i.Description, i.Suggestion, i.FilePath, i.Line, i.Occurrences);

    private static ModernizationDto Modernization(ModernizationItem m) => new(m.Project, m.RuleId, m.Kind, m.Impact, m.Effort, m.Title, m.Why, m.Proposal, m.Evidence, m.Occurrences, m.AwsService);

    // ----------------------------------------------------------------------------- DTOs

    public sealed record MigrationResultDto(string Solution, string RootDir, string? OutputDir, string Mode, DateTime GeneratedAt, bool NuGetChecked, bool? BuildSucceeded,
        CloudTarget Cloud, string? LlmModel, int LlmCalls, TotalsDto Totals, List<ProjectDto> Projects, List<ItemDto> GlobalItems, List<ModernizationDto> GlobalModernizations,
        List<DatabaseDto> Databases, List<string> InternalHosts, ArchitectureDto? Architecture);

    public sealed record TotalsDto(int Breaking, int Warnings, int Automatic, int Modernizations, int HighImpactModernizations);

    public sealed record ProjectDto(string Name, ProjectKind Kind, string Language, string SourceTargetFramework, string RelativeDir, string? OutputProjectPath,
        int Breaking, int Warnings, int Automatic, int Informational, int AutomationPercent, BuildDto? Build, TestsDto? Tests, SmokeDto? Smoke, bool? DockerBuildSucceeded,
        HostingDto? Hosting, List<ItemDto> Inventory, List<ModernizationDto> Modernizations);

    public sealed record BuildDto(int Errors, int Warnings, string? BlockedBy, bool Succeeded);
    public sealed record TestsDto(int Passed, int Failed, int Skipped, bool Succeeded, IReadOnlyList<string> FailedTests);
    public sealed record SmokeDto(bool Succeeded, int? StatusCode, string Detail);
    public sealed record HostingDto(AwsHosting Primary, string PrimaryLabel, bool RequiresWindows, List<string> HardWindowsDependencies, List<string> SoftWindowsDependencies,
        List<string> Rationale, List<string> Prerequisites, List<string> Alternatives, bool DockerfileGenerated);
    public sealed record ItemDto(string Project, string RuleId, InventorySeverity Severity, InventoryCategory Category, bool AutoMigrated, bool RequiresAction,
        string Title, string Description, string Suggestion, string? FilePath, int? Line, int Occurrences);
    public sealed record ModernizationDto(string Project, string RuleId, ModernizationKind Kind, Impact Impact, Effort Effort, string Title, string Why, string Proposal,
        string? Evidence, int Occurrences, string? AwsService);
    public sealed record DatabaseDto(string Provider, string? Server, string? Database, bool IntegratedSecurity, string Name, string Project);
    public sealed record ComponentDto(string Id, string Service, string Role, string Replaces, string Why, bool Required, List<string> UsedBy, string? Notes);
    public sealed record ArchitectureDto(string Summary, string? ExecutiveSummary, string? ExecutiveSummaryModel, List<ComponentDto> Components, List<string> Phases,
        List<string> Risks, List<string> CostNotes, string Diagram);
}
