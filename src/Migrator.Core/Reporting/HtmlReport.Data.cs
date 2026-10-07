using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class HtmlReport
{
    internal const string DataAccessLead =
        "Bancos, tabelas, procedures e campos que o código acessa, extraídos de SQL em literais C#/VB e arquivos .sql, leitores ADO.NET, chamadas Dapper, modelos EF6/EF Core e EDMX. " +
        "O banco é resolvido pelas connection strings (nome três partes no SQL > nome da connection string citada no código > banco único do projeto ou de quem o hospeda). " +
        "Análise estática: tabelas montadas dinamicamente e colunas lidas por índice não aparecem; confira com o DBA antes de migrar os dados.";

    private static void RenderDataAccess(StringBuilder sb, SolutionResult result, List<(string Database, string Technology, List<TableAccess> Tables)> groups)
    {
        sb.Append("<div class=\"panel\" id=\"dados\"><h2>4. Dados acessados (bancos, tabelas e campos)</h2><div class=\"body\">");
        sb.Append($"<p class=\"lead\">{E(DataAccessLead)}</p>");
        foreach (var (database, technology, tables) in groups)
        {
            var unresolved = database.StartsWith("não identificado", StringComparison.Ordinal);
            sb.Append($"<h3>{E(database)} <span class=\"badge {(unresolved ? "warning" : "cloud")}\">{E(technology)}</span> <span class=\"opt\">{tables.Count(t => t.Kind == DataObjectKind.Table)} tabela(s), {tables.Count(t => t.Kind != DataObjectKind.Table)} procedure(s)</span></h3>");
            sb.Append("<table><thead><tr><th>Tabela / procedure</th><th>Campos acessados</th><th>Operações</th><th>Acesso</th><th>Projetos</th><th>Onde</th></tr></thead><tbody>");
            foreach (var t in tables)
            {
                var columns = t.Columns.Count == 0 ? "<span class=\"opt\">(não identificados)</span>" : E(string.Join(", ", t.Columns));
                sb.Append($"<tr><td><strong>{E(t.QualifiedName)}</strong>{(t.Kind != DataObjectKind.Table ? $"<br><span class=\"badge cat\">{E(t.Kind.Display())}</span>" : "")}</td>")
                  .Append($"<td class=\"wrap\">{columns}</td><td>{E(string.Join(", ", t.Operations))}</td><td>{E(string.Join(", ", t.Access))}</td><td>{E(t.Project)}</td>")
                  .Append($"<td class=\"wrap\"><span class=\"loc\">{E(string.Join("; ", t.Locations.Take(4)))}{(t.Locations.Count > 4 ? "; ..." : "")}</span></td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("<p class=\"mermaid-note\">Lista completa em data-access.csv e na aba “Dados acessados” do inventory.xlsx.</p>");
        sb.Append("</div></div>");
    }
}
