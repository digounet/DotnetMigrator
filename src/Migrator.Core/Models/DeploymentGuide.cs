namespace Migrator.Core.Models;

/// <summary>
/// Everything that has to be configured for the application to run on AWS, organized the way an operations team reads it:
/// hosting units, infrastructure files and parameters, databases, secrets, application settings (environment variables /
/// Parameter Store), storage and queues, network and integrations, pipeline and a go-live checklist. Built once per run from
/// the architecture, the externalized settings and the secrets plan, with the same names the IaC generator writes, so the
/// report, the repository README and the JSON agree with the files in <c>infra/</c>.
/// </summary>
public sealed class DeploymentGuide
{
    /// <summary>FeatureName used by the platform (solution name, letters only).</summary>
    public required string Feature { get; init; }
    public required string Target { get; init; }
    public required string Iac { get; init; }
    /// <summary>True when the infrastructure files were written to the output (migrate); false on analyze or --no-infra, where the guide describes what migrate would generate.</summary>
    public bool InfrastructureWritten { get; set; }
    public List<string> Environments { get; } = [];
    public List<DeploymentUnit> Units { get; } = [];
    public List<InfraFile> Files { get; } = [];
    public List<InfraParameter> Parameters { get; } = [];
    public List<DatabaseSetup> Databases { get; } = [];
    public List<SecretSetup> Secrets { get; } = [];
    public List<SettingSetup> Settings { get; } = [];
    public List<ResourceSetup> Storage { get; } = [];
    public List<ResourceSetup> Queues { get; } = [];
    public List<IntegrationSetup> Integrations { get; } = [];
    public List<PipelineSetup> Pipeline { get; } = [];
    public List<ChecklistStep> Checklist { get; } = [];
}

/// <summary>One deployable project and where it runs.</summary>
public sealed class DeploymentUnit
{
    public required string Project { get; init; }
    public required string Kind { get; init; }
    /// <summary>MicroServiceName (project without the solution prefix, letters only).</summary>
    public required string Micro { get; init; }
    public required AwsHosting Hosting { get; init; }
    public required string HostingLabel { get; init; }
    /// <summary>How the process runs on the host: "site IIS", "serviço Windows", "tarefa agendada (Agendador)", "container ARM64", "função Lambda"...</summary>
    public required string Runtime { get; init; }
    public string? TemplateFile { get; init; }
    public string? Stack { get; init; }
    public string? ParametersFile { get; init; }
    public string? Endpoint { get; init; }
    public string? HealthCheck { get; init; }
    public string? Schedule { get; init; }
    public bool RequiresWindows { get; init; }
    /// <summary>Why it is not generated (VB.NET not converted, no output project), when it is not.</summary>
    public string? NotGenerated { get; init; }
    public List<string> Prerequisites { get; } = [];
}

public sealed class InfraFile
{
    public required string Path { get; init; }
    public required string Purpose { get; init; }
    public string? Stack { get; init; }
    public string? ParametersFile { get; init; }
    /// <summary>Order in which stacks are created (data first, then services, then lambdas); null for non-template files.</summary>
    public int? DeployOrder { get; init; }
}

/// <summary>A CloudFormation parameter (or Terraform variable) with its value per environment; placeholders must be filled before the first deploy.</summary>
public sealed class InfraParameter
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>"Compartilhada" (VPC, subnets, roles, listener: comes from the platform), "Dimensionamento", "Esteira", "Tags", "Aplicação" (URLs/e-mails) or "Dados".</summary>
    public required string Group { get; init; }
    /// <summary>Parameter files (per environment) that carry it.</summary>
    public SortedSet<string> Files { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    /// <summary>True when the generated value is an example that must be replaced (vpc-xxxx, 123456789012, empresa.com.br...).</summary>
    public bool Placeholder { get; init; }
}

public sealed class DatabaseSetup
{
    /// <summary>Database name (or connection-string name when unknown).</summary>
    public required string Name { get; init; }
    public required string Provider { get; init; }
    public string? SourceServer { get; init; }
    public bool IntegratedSecurity { get; init; }
    public List<string> ConnectionNames { get; } = [];
    public List<string> UsedBy { get; } = [];
    public int Tables { get; set; }
    public int Procedures { get; set; }
    /// <summary>Target engine and sizing proposed in parameters-data.json.</summary>
    public string? RdsEngine { get; init; }
    public Dictionary<string, string> InstanceClass { get; } = new(StringComparer.Ordinal);
    public string? Endpoint { get; init; }
    /// <summary>Where the application reads the connection string from on AWS.</summary>
    public List<string> ConnectionSecrets { get; } = [];
    public List<string> Notes { get; } = [];
}

public sealed class SecretSetup
{
    /// <summary>Secrets Manager name.</summary>
    public required string Name { get; init; }
    /// <summary>What it holds ("ConnectionStrings:DefaultConnection de LegacyShop.Web", "JSON chave→valor gravado no config...").</summary>
    public required string Holds { get; init; }
    public List<string> UsedBy { get; } = [];
    /// <summary>Environment variable (ECS/Lambda) or config key (EC2) the value lands in.</summary>
    public string? DeliveredAs { get; init; }
    /// <summary>How the value gets there: "_secrets/<proj>/create-secrets.sh", "preencher manualmente", "RDS (ManageMasterUserPassword)".</summary>
    public required string HowToFill { get; init; }
    public string? CreatedBy { get; init; }
}

/// <summary>A value the application reads from configuration that is now delivered by the infrastructure.</summary>
public sealed class SettingSetup
{
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required string Parameter { get; init; }
    public string? EnvironmentVariable { get; init; }
    public string? ParameterStorePath { get; init; }
    public required string Source { get; init; }
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public List<string> UsedBy { get; } = [];
}

/// <summary>Bucket, file system, queue or topic the application owns.</summary>
public sealed class ResourceSetup
{
    public required string Service { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }
    public string? Replaces { get; init; }
    public List<string> UsedBy { get; } = [];
    /// <summary>How the application finds it (export name, environment variable, parameter).</summary>
    public string? DeliveredAs { get; init; }
    public List<string> Notes { get; } = [];
}

public sealed class IntegrationSetup
{
    public required string Kind { get; init; }
    public required string Target { get; init; }
    public required string Action { get; init; }
    public List<string> UsedBy { get; } = [];
}

public sealed class PipelineSetup
{
    public required string File { get; init; }
    public required string Key { get; init; }
    public required string Value { get; init; }
    public required string Action { get; init; }
}

public sealed class ChecklistStep
{
    public required string Phase { get; init; }
    public required string Step { get; init; }
    public string? Detail { get; init; }
}
