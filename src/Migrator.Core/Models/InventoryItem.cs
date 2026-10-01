namespace Migrator.Core.Models;

public sealed class InventoryItem
{
    public string Project { get; set; } = string.Empty;
    public InventorySeverity Severity { get; set; }
    public InventoryCategory Category { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
    public string? FilePath { get; set; }
    public int? Line { get; set; }
    public int Occurrences { get; set; } = 1;
    public bool AutoMigrated { get; set; }

    public bool RequiresAction => !AutoMigrated && Severity != InventorySeverity.Info;
}

public enum InventorySeverity { Breaking, Warning, Info }

public enum InventoryCategory
{
    Package,
    Code,
    View,
    Configuration,
    Startup,
    ProjectFile,
    Build
}

public static class InventoryText
{
    public static string Display(this InventorySeverity severity) => severity switch
    {
        InventorySeverity.Breaking => "Bloqueante",
        InventorySeverity.Warning => "Atenção",
        _ => "Informativo"
    };

    public static string Display(this InventoryCategory category) => category switch
    {
        InventoryCategory.Package => "Pacote",
        InventoryCategory.Code => "Código",
        InventoryCategory.View => "View",
        InventoryCategory.Configuration => "Configuração",
        InventoryCategory.Startup => "Inicialização",
        InventoryCategory.ProjectFile => "Projeto",
        _ => "Build"
    };
}
