namespace Migrator.Core.Models;

public enum CloudTarget { None, Aws }

/// <summary>Where a deployable project should run on AWS.</summary>
public enum AwsHosting
{
    /// <summary>Library, test project or anything that ships inside another deployable.</summary>
    NotDeployable,
    /// <summary>Linux container on ECS Fargate behind an Application Load Balancer.</summary>
    EcsFargate,
    /// <summary>Windows container on ECS (Fargate Windows or EC2 launch type).</summary>
    EcsWindows,
    /// <summary>Long-running Linux worker container on ECS Fargate (queue consumer / background service).</summary>
    EcsFargateWorker,
    /// <summary>Linux container run on a schedule by EventBridge Scheduler (ECS RunTask).</summary>
    EcsScheduledTask,
    /// <summary>AWS Lambda (.NET 10 managed runtime or container image).</summary>
    Lambda,
    /// <summary>Windows EC2 instance (lift-and-shift when containers are not viable).</summary>
    Ec2Windows,
    /// <summary>Desktop app: not hosted in AWS (or Amazon AppStream 2.0 / WorkSpaces).</summary>
    Desktop
}

public sealed class HostingRecommendation
{
    public required string Project { get; init; }
    public required ProjectKind Kind { get; init; }
    public AwsHosting Primary { get; set; }
    /// <summary>Reasons that led to the primary choice.</summary>
    public List<string> Rationale { get; } = [];
    /// <summary>Other viable options, with the trade-off in one sentence.</summary>
    public List<string> Alternatives { get; } = [];
    /// <summary>Dependencies that force Windows (COM, Registry, WinForms...). Empty = Linux container is fine.</summary>
    public List<string> HardWindowsDependencies { get; } = [];
    /// <summary>Dependencies that work only on Windows today but have a Linux-friendly replacement.</summary>
    public List<string> SoftWindowsDependencies { get; } = [];
    /// <summary>Things to do before the first deploy (health endpoint, externalize state, etc.).</summary>
    public List<string> Prerequisites { get; } = [];
    public bool DockerfileGenerated { get; set; }

    public bool RequiresWindows => HardWindowsDependencies.Count > 0;
}

/// <summary>One managed service in the target architecture.</summary>
public sealed class AwsComponent
{
    public required string Id { get; init; }
    public required string Service { get; init; }
    public required string Role { get; init; }
    /// <summary>What it replaces in the legacy application ("MSMQ", "pasta C:\Exportacao", "SMTP interno"...).</summary>
    public string Replaces { get; set; } = string.Empty;
    public string Why { get; set; } = string.Empty;
    /// <summary>Projects that use this component.</summary>
    public SortedSet<string> UsedBy { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True when the application cannot run on AWS without it (database, hosting). False = recommended.</summary>
    public bool Required { get; set; } = true;
    public string? Notes { get; set; }
}

public sealed class ArchitectureProposal
{
    public CloudTarget Target { get; init; } = CloudTarget.Aws;
    public List<HostingRecommendation> Hosting { get; } = [];
    public List<AwsComponent> Components { get; } = [];
    /// <summary>Suggested order of work, solution-wide.</summary>
    public List<string> Phases { get; } = [];
    public List<string> Risks { get; } = [];
    public List<string> CostNotes { get; } = [];
    /// <summary>Mermaid flowchart source for the target architecture.</summary>
    public string Diagram { get; set; } = string.Empty;
    /// <summary>One-paragraph description of what the application is, as understood by the tool.</summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>Narrative written by the LLM (Markdown, pt-BR) when one is configured; otherwise null.</summary>
    public string? ExecutiveSummary { get; set; }
    public string? ExecutiveSummaryModel { get; set; }
}

public static class CloudText
{
    public static string Display(this AwsHosting hosting) => hosting switch
    {
        AwsHosting.NotDeployable => "não publicável (biblioteca/testes)",
        AwsHosting.EcsFargate => "ECS Fargate (Linux) + ALB",
        AwsHosting.EcsWindows => "ECS com containers Windows",
        AwsHosting.EcsFargateWorker => "ECS Fargate (worker Linux)",
        AwsHosting.EcsScheduledTask => "ECS Fargate (tarefa agendada via EventBridge Scheduler)",
        AwsHosting.Lambda => "AWS Lambda",
        AwsHosting.Ec2Windows => "EC2 Windows",
        _ => "Desktop (fora da AWS / AppStream 2.0)"
    };

    public static string Short(this AwsHosting hosting) => hosting switch
    {
        AwsHosting.NotDeployable => "—",
        AwsHosting.EcsFargate => "ECS Fargate",
        AwsHosting.EcsWindows => "ECS Windows",
        AwsHosting.EcsFargateWorker => "ECS Fargate (worker)",
        AwsHosting.EcsScheduledTask => "ECS agendado",
        AwsHosting.Lambda => "Lambda",
        AwsHosting.Ec2Windows => "EC2 Windows",
        _ => "Desktop"
    };
}
