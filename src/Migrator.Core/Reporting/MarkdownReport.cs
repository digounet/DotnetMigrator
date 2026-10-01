using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static class MarkdownReport
{
    private const int MaxBuildExamples = 15;

    public static string Render(SolutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Migração .NET Framework → .NET 10 — {result.SolutionName}");
        sb.AppendLine();
        sb.AppendLine($"- **Origem:** `{result.RootDir}`");
        sb.AppendLine($"- **Modo:** {ReportWriter.ModeLabel(result)}");
        if (result.OutputDir != null) sb.AppendLine($"- **Saída:** `{result.OutputDir}`");
        sb.AppendLine($"- **Gerado em:** {result.FinishedAt:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"- **Compatibilidade NuGet verificada:** {(result.NuGetChecked ? "sim" : "não")}");
        sb.AppendLine($"- **Build de verificação:** {result.BuildSucceeded switch { true => "sucesso", false => "com erros", null => "não executado" }}");
        sb.AppendLine();

        sb.AppendLine("## Resumo");
        sb.AppendLine();
        sb.AppendLine("| Projeto | Tipo | Origem | Bloqueantes | Atenção | Automático | % automatizado | Build |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---|");
        foreach (var p in result.Projects)
            sb.AppendLine($"| {Cell(p.Project.Name)} | {ReportWriter.KindLabel(p.Project.Kind)} | {Cell(p.Project.TargetFramework)} | {p.Breaking.Count()} | {p.Warnings.Count()} | {p.Automatic.Count()} | {p.AutomationPercent}% | {ReportWriter.BuildLabel(result, p)} |");
        sb.AppendLine();

        if (result.GlobalItems.Count > 0)
        {
            sb.AppendLine("## Solução");
            sb.AppendLine();
            RenderItems(sb, result.GlobalItems);
        }

        foreach (var p in result.Projects)
        {
            sb.AppendLine($"## {p.Project.Name}");
            sb.AppendLine();
            sb.AppendLine($"{ReportWriter.KindLabel(p.Project.Kind)} · {p.Project.TargetFramework} → net10.0 · pasta `{(p.RelativeDir.Length == 0 ? "." : p.RelativeDir)}`");
            sb.AppendLine();
            RenderItems(sb, p.Inventory);
        }
        return sb.ToString();
    }

    private static void RenderItems(StringBuilder sb, List<InventoryItem> items)
    {
        void Section(string title, IEnumerable<InventoryItem> source)
        {
            var list = ReportWriter.Ordered(source).ToList();
            if (list.Count == 0) return;
            sb.AppendLine($"### {title} ({list.Count})");
            sb.AppendLine();
            foreach (var item in list)
            {
                var location = item.FilePath != null ? $" — `{ReportWriter.Location(item)}`" : "";
                var count = item.Occurrences > 1 ? $" (×{item.Occurrences})" : "";
                sb.AppendLine($"- **[{item.RuleId}] {Inline(item.Title)}**{location}{count}");
                if (item.Description.Length > 0) sb.AppendLine($"  - {Inline(item.Description)}");
                if (item.Suggestion.Length > 0 && item.Suggestion != "Nenhuma ação necessária.") sb.AppendLine($"  - *Sugestão:* {Inline(item.Suggestion)}");
            }
            sb.AppendLine();
        }

        var nonBuild = items.Where(i => i.Category != InventoryCategory.Build).ToList();
        Section("Ações bloqueantes", nonBuild.Where(i => i.RequiresAction && i.Severity == InventorySeverity.Breaking));
        Section("Pontos de atenção", nonBuild.Where(i => i.RequiresAction && i.Severity == InventorySeverity.Warning));

        var build = items.Where(i => i.Category == InventoryCategory.Build).ToList();
        if (build.Count > 0)
        {
            sb.AppendLine($"### Build de verificação ({build.Count})");
            sb.AppendLine();
            sb.AppendLine($"> {HtmlReport.BuildNote}");
            sb.AppendLine();
            sb.AppendLine("| Código | Severidade | Ocorrências | Sugestão |");
            sb.AppendLine("|---|---|---:|---|");
            var groups = build.GroupBy(i => (i.RuleId, i.Severity)).OrderBy(g => g.Key.Severity).ThenByDescending(g => g.Count()).ToList();
            foreach (var g in groups)
                sb.AppendLine($"| {g.Key.RuleId} | {g.Key.Severity.Display()} | {g.Count()} | {Cell(g.First().Suggestion)} |");
            sb.AppendLine();
            foreach (var g in groups)
            {
                sb.AppendLine($"<details><summary>{g.Key.RuleId} — {g.Count()} ocorrência(s)</summary>");
                sb.AppendLine();
                foreach (var i in g.Take(MaxBuildExamples))
                    sb.AppendLine($"- `{ReportWriter.Location(i)}` {Inline(i.Description)}");
                if (g.Count() > MaxBuildExamples) sb.AppendLine($"- ... e mais {g.Count() - MaxBuildExamples} (veja inventory.xlsx)");
                sb.AppendLine();
                sb.AppendLine("</details>");
                sb.AppendLine();
            }
        }

        var automatic = ReportWriter.Ordered(items.Where(i => i.AutoMigrated)).ToList();
        if (automatic.Count > 0)
        {
            sb.AppendLine($"### Resolvido automaticamente ({automatic.Count})");
            sb.AppendLine();
            foreach (var item in automatic)
                sb.AppendLine($"- {Inline(item.Title)}{(item.Description.Length > 0 ? $" — {Inline(item.Description)}" : "")}");
            sb.AppendLine();
        }

        Section("Informativo", nonBuild.Where(i => !i.AutoMigrated && i.Severity == InventorySeverity.Info));
    }

    private static string Inline(string text) => text.Replace("\r", "").Replace("\n", " ").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Cell(string text) => Inline(text).Replace("|", "\\|");
}
