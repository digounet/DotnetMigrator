using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class MarkdownReport
{
    private static void RenderDataAccess(StringBuilder sb, SolutionResult result)
    {
        var groups = ReportWriter.DataAccessByDatabase(result);
        if (groups.Count == 0) return;
        sb.AppendLine("## 4. Dados acessados (bancos, tabelas e campos)");
        sb.AppendLine();
        sb.AppendLine(HtmlReport.DataAccessLead);
        sb.AppendLine();
        foreach (var (database, technology, tables) in groups)
        {
            sb.AppendLine($"### {database} ({technology}) — {tables.Count(t => t.Kind == DataObjectKind.Table)} tabela(s), {tables.Count(t => t.Kind != DataObjectKind.Table)} procedure(s)");
            sb.AppendLine();
            sb.AppendLine("| Tabela / procedure | Campos acessados | Operações | Acesso | Projetos | Onde |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var t in tables)
                sb.AppendLine($"| **{Cell(t.QualifiedName)}**{(t.Kind != DataObjectKind.Table ? $" ({t.Kind.Display()})" : "")} | {(t.Columns.Count == 0 ? "_(não identificados)_" : Cell(string.Join(", ", t.Columns)))} | {Cell(string.Join(", ", t.Operations))} | {Cell(string.Join(", ", t.Access))} | {Cell(t.Project)} | `{Cell(string.Join("; ", t.Locations.Take(3)))}`{(t.Locations.Count > 3 ? " ..." : "")} |");
            sb.AppendLine();
        }
    }
}
