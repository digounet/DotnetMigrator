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
    public const string DataAccessCsvFile = "data-access.csv";

    public static async Task WriteAllAsync(SolutionResult result, string directory)
    {
        Directory.CreateDirectory(directory);
        var utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        await File.WriteAllTextAsync(Path.Combine(directory, HtmlFile), HtmlReport.Render(result), utf8Bom);
        await File.WriteAllTextAsync(Path.Combine(directory, MarkdownFile), MarkdownReport.Render(result), utf8Bom);
        await File.WriteAllTextAsync(Path.Combine(directory, CsvFile), CsvReport.Render(result), utf8Bom);
        if (result.AllModernizations.Any())
            await File.WriteAllTextAsync(Path.Combine(directory, ModernizationCsvFile), CsvReport.RenderModernization(result), utf8Bom);
        if (result.AllDataAccess.Any())
            await File.WriteAllTextAsync(Path.Combine(directory, DataAccessCsvFile), CsvReport.RenderDataAccess(result), utf8Bom);
        await File.WriteAllTextAsync(Path.Combine(directory, JsonReport.FileName), JsonReport.Render(result), utf8Bom);
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

    public static string KindLabel(ProjectInfo project) => KindLabel(project.Kind) + (project.IsVisualBasic ? " · VB.NET" : "");

    public static string KindLabel(ProjectKind kind) => kind switch
    {
        ProjectKind.Web => "Web (MVC/Web API)",
        ProjectKind.Console => "Console",
        ProjectKind.WindowsService => "Windows Service",
        ProjectKind.Desktop => "Desktop (WinForms/WPF)",
        ProjectKind.Test => "Testes",
        _ => "Biblioteca"
    };

    public static string BuildLabel(SolutionResult result, ProjectResult project)
    {
        if (project.Build is null) return result.Options.DryRun ? "não executado (análise)" : result.BuildSkippedReason != null ? $"não executado ({result.BuildSkippedReason})" : result.Options.VerifyBuild ? "—" : "não executado";
        if (project.Build.BlockedBy != null) return $"bloqueado ({project.Build.BlockedBy} com erros)";
        if (project.Build.Errors > 0) return $"{project.Build.Errors} erro(s)";
        var extras = new List<string>();
        if (project.Tests != null) extras.Add(project.Tests.Succeeded ? $"testes {project.Tests.Passed}/{project.Tests.Total}" : project.Tests.Total == 0 ? "testes: nenhum executado" : $"testes {project.Tests.Failed} falha(s)");
        if (project.Smoke != null) extras.Add(project.Smoke.Succeeded ? "/health OK" : "/health falhou");
        if (project.DockerBuildSucceeded is { } docker) extras.Add(docker ? "docker OK" : "docker falhou");
        return extras.Count == 0 ? "OK" : "OK · " + string.Join(" · ", extras);
    }

    public static string ModeLabel(SolutionResult result) =>
        result.Options.DryRun ? "Análise (nenhum arquivo alterado)" : result.Options.KeepsFramework ? "Atualização de framework (código não alterado)" : "Migração";

    /// <summary>Report title: what the run produces.</summary>
    public static string Title(SolutionResult result) =>
        result.Options.KeepsFramework ? "Atualização para .NET Framework 4.8.1 + infraestrutura AWS" : "Migração .NET Framework → .NET 10";

    /// <summary>Target moniker shown next to each project ("net10.0" or "v4.8.1").</summary>
    public static string TargetMoniker(SolutionResult result) => result.Options.Target.Moniker();

    /// <summary>Which parameter files carry a parameter, short: "todos" when every template has it, otherwise the stack suffixes ("data, worker").</summary>
    internal static string FilesLabel(DeploymentGuide guide, InfraParameter parameter)
    {
        var all = guide.Files.Where(f => f.ParametersFile != null).Select(f => Path.GetFileName(f.ParametersFile!)).Distinct().ToList();
        if (all.Count > 1 && parameter.Files.Count >= all.Count) return "todos";
        var services = all.Where(f => f != "parameters-data.json").ToList();
        if (services.Count > 1 && services.All(parameter.Files.Contains) && !parameter.Files.Contains("parameters-data.json")) return "todos os serviços";
        return string.Join(", ", parameter.Files.Select(f => f == "parameters.json" ? "service" : f.Replace("parameters-", "").Replace(".json", "")));
    }

    /// <summary>Tables grouped by database for the "Dados acessados" section, with the projects that touch each one merged.</summary>
    internal static List<(string Database, string Technology, List<TableAccess> Tables)> DataAccessByDatabase(SolutionResult result) =>
        result.AllDataAccess
            .GroupBy(t => (t.Database, t.Technology))
            .OrderBy(g => g.Key.Database.StartsWith("não identificado", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(g => g.Key.Database, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key.Database, g.Key.Technology, g.GroupBy(t => (t.Kind, Name: t.QualifiedName), (k, items) =>
                {
                    var merged = new TableAccess { Project = string.Join(", ", items.Select(i => i.Project).Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)), Name = items.First().Name, Kind = k.Kind, Schema = items.First().Schema, Database = g.Key.Database, Technology = g.Key.Technology };
                    foreach (var i in items) merged.MergeFrom(i);
                    return merged;
                }).OrderBy(t => t.Kind).ThenBy(t => t.QualifiedName, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
}
