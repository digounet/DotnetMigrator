using Migrator.Core.Analysis;
using Migrator.Core.Migration;

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
    /// <summary>
    /// NetFramework (default) keeps the code untouched, only raises every project to .NET Framework 4.8.1 and hosts it on EC2
    /// Windows (lift-and-shift with CloudFormation); Net10 rewrites the code for .NET 10 (ECS Fargate). Analysis, data-access
    /// inventory, secrets protection and architecture run in both.
    /// </summary>
    public MigrationTarget Target { get; init; } = MigrationTarget.NetFramework;
    /// <summary>Infrastructure-as-code flavour. Null = CloudFormation (the platform's YAML layout) for both targets; Terraform is opt-in.</summary>
    public IacTool? Iac { get; init; }
    public IacTool EffectiveIac => Iac ?? IacTool.CloudFormation;
    public bool KeepsFramework => Target == MigrationTarget.NetFramework;
    /// <summary>
    /// Opt in to AWS Lambda for event-driven automations (requires rewriting the entry point as a handler). Off by default:
    /// the portfolio is a lift-and-shift, so consoles/services keep their Main() and run as ECS scheduled tasks or workers.
    /// </summary>
    public bool Serverless { get; init; }
    /// <summary>nuget.config of the private feed (Artifactory/Nexus...). Copied to the output root and used for compatibility lookups and the verification build. Falls back to MIGRATOR_NUGET_CONFIG, then the source root's nuget.config.</summary>
    public string? NuGetConfigPath { get; init; }
    /// <summary>Explicit v3 service index URL for compatibility lookups (overrides the nuget.config); a minimal nuget.config is generated from it when none exists.</summary>
    public string? NuGetSourceUrl { get; init; }
    /// <summary>Keep credentials inside the generated appsettings*.json instead of moving them to _secrets/ (not recommended).</summary>
    public bool KeepSecrets { get; init; }
    /// <summary>Run the migrated test projects (dotnet test) after a successful verification build.</summary>
    public bool RunTests { get; init; } = true;
    /// <summary>Start migrated web apps and request /health after a successful verification build.</summary>
    public bool SmokeTest { get; init; } = true;
    /// <summary>Build the generated Dockerfiles with the local Docker (slow; off by default).</summary>
    public bool VerifyDocker { get; init; }
    /// <summary>Generate infra/terraform and .github/workflows/deploy.yml from the architecture proposal (migrate + AWS only).</summary>
    public bool GenerateInfrastructure { get; init; } = true;
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
    /// <summary>Tables, views and stored procedures the project's code touches (ADO.NET, Dapper, EF, EDMX, .sql files).</summary>
    public List<TableAccess> DataAccess { get; } = [];
    /// <summary>URLs, e-mails and credentials that left the code/config of this project and became configuration (IaC parameters / secrets).</summary>
    public List<ExternalizedSetting> Settings { get; } = [];
    /// <summary>Own settings plus those of every referenced project: what the deployable actually needs at runtime (filled by the engine).</summary>
    public List<ExternalizedSetting> SettingsWithDependencies { get; } = [];
    /// <summary>Raw data-access findings, resolved solution-wide by DataAccessAnalyzer.Resolve into <see cref="DataAccess"/>.</summary>
    internal DataAccessScan? DataScan { get; set; }
    public ProjectBuildStatus? Build { get; set; }
    /// <summary>Credentials moved out of this project's appsettings (null when none or when --keep-secrets).</summary>
    public SecretsPlan? Secrets { get; set; }
    public TestRunStatus? Tests { get; set; }
    public SmokeTestStatus? Smoke { get; set; }
    public bool? DockerBuildSucceeded { get; set; }

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
    /// <summary>Where the migrated solution is written inside the output: &lt;OutputDir&gt;/app/src, the platform repository layout (infra/, tests/, .github/ and .iupipes.yml stay at the root).</summary>
    public string? SourceDir { get; set; }
    public string? ReportDir { get; set; }
    public List<ProjectResult> Projects { get; } = [];
    public List<InventoryItem> GlobalItems { get; } = [];
    public List<ModernizationItem> GlobalModernizations { get; } = [];
    public ArchitectureProposal? Architecture { get; set; }
    /// <summary>What must be configured for the application to run on AWS (hosting, infra parameters, databases, secrets, settings, storage, network, pipeline, checklist); null with --cloud none.</summary>
    public DeploymentGuide? Deployment { get; set; }
    /// <summary>Databases referenced by the deployable projects (from connection strings), for the portfolio view.</summary>
    public List<DatabaseUse> Databases { get; } = [];
    /// <summary>On-premises hosts (internal DNS names / private IPs) the application talks to.</summary>
    public SortedSet<string> InternalHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime StartedAt { get; } = DateTime.Now;
    public DateTime FinishedAt { get; set; }
    public bool? BuildSucceeded { get; set; }
    public string? BuildLogPath { get; set; }
    public bool NuGetChecked { get; set; }
    /// <summary>Feed used for compatibility lookups and expected by the restore ("nuget.org (https://api.nuget.org/v3/index.json)" or the private feed).</summary>
    public string? NuGetSource { get; set; }
    /// <summary>When the verification build was skipped on purpose (unreachable feed), why.</summary>
    public string? BuildSkippedReason { get; set; }
    /// <summary>Provider/model used for the LLM-assisted steps, or null when none was configured.</summary>
    public string? LlmModel { get; set; }
    public int LlmCalls { get; set; }
    /// <summary>Modernization items whose impact/notes were refined by the LLM triage (files temp vs persistent, cache vs state, idempotent jobs).</summary>
    public int LlmTriagedItems { get; set; }

    public IEnumerable<InventoryItem> AllItems => GlobalItems.Concat(Projects.SelectMany(p => p.Inventory));
    public IEnumerable<TableAccess> AllDataAccess => Projects.SelectMany(p => p.DataAccess);
    public IEnumerable<ModernizationItem> AllModernizations => GlobalModernizations.Concat(Projects.SelectMany(p => p.Modernizations));
}
