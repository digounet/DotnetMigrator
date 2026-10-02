namespace Migrator.Core.Models;

/// <summary>Why a modernization is being proposed.</summary>
public enum ModernizationKind
{
    /// <summary>The library changed to a commercial license in recent versions.</summary>
    License,
    /// <summary>The library or API is discontinued / unmaintained.</summary>
    Deprecated,
    /// <summary>A better-supported, faster or simpler alternative exists.</summary>
    Modernize,
    /// <summary>Required or strongly recommended to run well on AWS (containers, managed services).</summary>
    Cloud,
    /// <summary>Security hardening.</summary>
    Security
}

public enum Impact { High, Medium, Low }

public enum Effort { Low, Medium, High }

/// <summary>
/// A suggestion that is not required for the code to compile on .NET 10, but that the team should
/// consider as part of the modernization program. Kept separate from <see cref="InventoryItem"/> so
/// that it does not count against the automation percentage or make the CLI exit code non-zero.
/// </summary>
public sealed class ModernizationItem
{
    public string Project { get; set; } = string.Empty;
    public ModernizationKind Kind { get; set; }
    public string RuleId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    /// <summary>What was found (package, API, config) and why it matters.</summary>
    public string Why { get; set; } = string.Empty;
    /// <summary>The proposed change, with concrete alternatives.</summary>
    public string Proposal { get; set; } = string.Empty;
    public Impact Impact { get; set; } = Impact.Medium;
    public Effort Effort { get; set; } = Effort.Medium;
    /// <summary>Package id, file path or config element that triggered the suggestion.</summary>
    public string? Evidence { get; set; }
    public int Occurrences { get; set; } = 1;
    /// <summary>AWS service that replaces or supports the current component, when applicable.</summary>
    public string? AwsService { get; set; }
}

public static class ModernizationText
{
    public static string Display(this ModernizationKind kind) => kind switch
    {
        ModernizationKind.License => "Licença",
        ModernizationKind.Deprecated => "Descontinuado",
        ModernizationKind.Modernize => "Modernização",
        ModernizationKind.Cloud => "Cloud (AWS)",
        _ => "Segurança"
    };

    public static string Display(this Impact impact) => impact switch
    {
        Impact.High => "Alto",
        Impact.Medium => "Médio",
        _ => "Baixo"
    };

    public static string Display(this Effort effort) => effort switch
    {
        Effort.Low => "Baixo",
        Effort.Medium => "Médio",
        _ => "Alto"
    };
}
