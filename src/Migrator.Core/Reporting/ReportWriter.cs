using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static class ReportWriter
{
    public const string HtmlFile = "migration-report.html";
    public const string MarkdownFile = "migration-report.md";
    public const string CsvFile = "inventory.csv";
    public const string ExcelFile = "inventory.xlsx";
    public const string ModernizationCsvFile = "modernization.csv";

    public static async Task WriteAllAsync(SolutionResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(Path.Combine(directory, HtmlFile), HtmlReport.Render(result), utf8Bom);
        await File.WriteAllTextAsync(Path.Combine(directory, MarkdownFile), MarkdownReport.Render(result), utf8Bom);
        await File.WriteAllTextAsync(Path.Combine(directory, CsvFile), CsvReport.Render(result), utf8Bom);
        if (result.AllModernizations.Any())
            await File.WriteAllTextAsync(Path.Combine(directory, ModernizationCsvFile), CsvReport.RenderModernization(result), utf8Bom);
        ExcelReport.Write(result, Path.Combine(directory, ExcelFile));
    }

    internal static IEnumerable<InventoryItem> Ordered(IEnumerable<InventoryItem> items) =>
        items.OrderBy(i => i.AutoMigrated)
             .ThenBy(i => i.Severity)
             .ThenBy(i => i.Category)
             .ThenBy(i => i.RuleId, StringComparer.Ordinal)
             .ThenBy(i => i.FilePath, StringComparer.OrdinalIgnoreCase)
             .ThenBy(i => i.Line);

    internal static IEnumerable<ModernizationItem> OrderedModernizations(IEnumerable<ModernizationItem> items) =>
        items.OrderBy(i => i.Impact)
             .ThenBy(i => i.Kind switch { ModernizationKind.Cloud => 0, ModernizationKind.License => 1, ModernizationKind.Security => 2, ModernizationKind.Deprecated => 3, _ => 4 })
             .ThenBy(i => i.Effort)
             .ThenBy(i => i.RuleId, StringComparer.Ordinal);

    internal static string Location(InventoryItem item) =>
        item.FilePath == null ? "" : item.Line is { } line ? $"{item.FilePath}:{line}" : item.FilePath;

    public static string KindLabel(ProjectKind kind) => kind switch
    {
        ProjectKind.Web => "Web (MVC/Web API)",
        ProjectKind.Console => "Console",
        ProjectKind.WindowsService => "Windows Service",
        ProjectKind.Desktop => "Desktop (WinForms/WPF)",
        ProjectKind.Test => "Testes",
        _ => "Biblioteca"
    };

    public static string BuildLabel(SolutionResult result, ProjectResult project) =>
        project.Build is null
            ? (result.Options.DryRun ? "não executado (análise)" : result.Options.VerifyBuild ? "—" : "não executado")
            : project.Build.BlockedBy != null ? $"bloqueado ({project.Build.BlockedBy} com erros)"
            : project.Build.Errors == 0 ? "OK" : $"{project.Build.Errors} erro(s)";

    public static string ModeLabel(SolutionResult result) =>
        result.Options.DryRun ? "Análise (nenhum arquivo alterado)" : "Migração";
}
