using System.Net;
using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class HtmlReport
{
    private const int MaxBuildRowsPerGroup = 300;

    internal const string BuildNote =
        "O compilador C# só aponta erros dentro dos métodos depois que os erros de declaração (tipos, atributos e assinaturas) são resolvidos. " +
        "Corrija, recompile e repita: os itens de Código/View acima já antecipam os pontos que aparecerão nas próximas rodadas.";

    private const string Css = """
        :root { --purple:#6a1b9a; --red:#c5221f; --red-bg:#fce8e6; --amber:#b06000; --amber-bg:#fef7e0; --blue:#1967d2; --blue-bg:#e8f0fe; --green:#137333; --green-bg:#e6f4ea; --border:#dadce0; --muted:#5f6368; }
        * { box-sizing: border-box; }
        body { margin: 0; font-family: "Segoe UI", system-ui, -apple-system, sans-serif; background: #f8f9fa; color: #202124; font-size: 14px; }
        header { background: #1a3a6b; color: #fff; padding: 24px 32px; }
        header h1 { margin: 0 0 8px; font-size: 22px; font-weight: 600; }
        header .meta { display: flex; flex-wrap: wrap; gap: 6px 24px; font-size: 13px; opacity: .9; }
        main { max-width: 1280px; margin: 0 auto; padding: 24px 32px 64px; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 12px; margin-bottom: 24px; }
        .card { background: #fff; border: 1px solid var(--border); border-radius: 8px; padding: 16px; }
        .card .n { font-size: 28px; font-weight: 600; }
        .card .l { color: var(--muted); font-size: 12px; margin-top: 4px; }
        .card.red .n { color: var(--red); } .card.amber .n { color: var(--amber); } .card.green .n { color: var(--green); } .card.blue .n { color: var(--blue); } .card.purple .n { color: var(--purple); }
        .section-h { font-size: 18px; margin: 32px 0 12px; }
        .panel .body { padding: 14px 16px; }
        .panel p.lead { margin: 0 0 12px; line-height: 1.5; }
        .panel h3 { margin: 18px 0 8px; font-size: 14px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); }
        .panel ol, .panel ul { margin: 0; padding-left: 22px; line-height: 1.5; }
        .panel li { margin-bottom: 6px; }
        .mermaid { background: #fff; padding: 12px; overflow: auto; font-size: 12px; }
        .mermaid-note { color: var(--muted); font-size: 12px; margin: 6px 0 0; }
        .badge.license { background: #fce8f3; color: #9c1b5d; } .badge.deprecated { background: #f1f3f4; color: #3c4043; } .badge.modernize { background: var(--blue-bg); color: var(--blue); }
        .badge.cloud { background: #e6f0ff; color: #1b4fa5; } .badge.security { background: var(--red-bg); color: var(--red); }
        .badge.high { background: var(--red-bg); color: var(--red); } .badge.medium { background: var(--amber-bg); color: var(--amber); } .badge.low { background: var(--green-bg); color: var(--green); }
        .item.mod { border-left-color: var(--purple); }
        .item .why { margin: 6px 0 0; color: #3c4043; }
        td.wrap { max-width: 420px; }
        .req { color: var(--green); font-weight: 600; } .opt { color: var(--muted); }
        .narrative { background: #f8f9fa; border-left: 4px solid var(--purple); padding: 4px 16px; border-radius: 6px; line-height: 1.55; }
        .narrative h4 { margin: 12px 0 4px; font-size: 14px; } .narrative p { margin: 8px 0; } .narrative ul { margin: 6px 0; }
        table { width: 100%; border-collapse: collapse; background: #fff; }
        th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
        th { background: #f1f3f4; font-weight: 600; font-size: 12px; text-transform: uppercase; letter-spacing: .03em; color: var(--muted); }
        td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
        .panel { background: #fff; border: 1px solid var(--border); border-radius: 8px; margin-bottom: 20px; overflow: hidden; }
        .panel > h2 { margin: 0; padding: 14px 16px; font-size: 16px; border-bottom: 1px solid var(--border); }
        .filters { display: flex; flex-wrap: wrap; gap: 16px; align-items: center; padding: 12px 16px; background: #fff; border: 1px solid var(--border); border-radius: 8px; margin-bottom: 20px; position: sticky; top: 0; z-index: 2; }
        .filters input[type=search] { flex: 1 1 260px; padding: 8px 10px; border: 1px solid var(--border); border-radius: 6px; font-size: 14px; }
        .filters label { display: flex; gap: 6px; align-items: center; cursor: pointer; }
        details.project { background: #fff; border: 1px solid var(--border); border-radius: 8px; margin-bottom: 16px; }
        details.project > summary { padding: 14px 16px; cursor: pointer; font-size: 16px; font-weight: 600; display: flex; gap: 12px; align-items: center; flex-wrap: wrap; }
        details.project > summary .sub { font-weight: 400; color: var(--muted); font-size: 13px; }
        .section-title { margin: 0; padding: 10px 16px; font-size: 13px; text-transform: uppercase; letter-spacing: .04em; color: var(--muted); background: #f8f9fa; border-top: 1px solid var(--border); }
        .item { padding: 12px 16px; border-top: 1px solid var(--border); border-left: 4px solid transparent; }
        .item.breaking { border-left-color: var(--red); } .item.warning { border-left-color: var(--amber); } .item.info { border-left-color: var(--blue); } .item.auto { border-left-color: var(--green); }
        .item .head { display: flex; flex-wrap: wrap; gap: 8px; align-items: baseline; }
        .item .title { font-weight: 600; }
        .item .loc { font-family: Consolas, "Cascadia Mono", monospace; font-size: 12px; color: var(--muted); margin-top: 4px; word-break: break-all; }
        .item .desc { margin: 6px 0 0; }
        .item .sugg { margin: 8px 0 0; padding: 8px 10px; background: #f1f3f4; border-radius: 6px; }
        .item .sugg b { color: #3c4043; }
        .badge { display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 11px; font-weight: 600; white-space: nowrap; }
        .badge.breaking { background: var(--red-bg); color: var(--red); } .badge.warning { background: var(--amber-bg); color: var(--amber); }
        .badge.info { background: var(--blue-bg); color: var(--blue); } .badge.auto { background: var(--green-bg); color: var(--green); }
        .badge.cat { background: #f1f3f4; color: #3c4043; }
        code.rule { font-size: 11px; color: var(--muted); }
        details.group { border-top: 1px solid var(--border); }
        details.group > summary { padding: 10px 16px; cursor: pointer; }
        details.group table { font-size: 12px; }
        details.group td.file { font-family: Consolas, monospace; white-space: nowrap; }
        .ok { color: var(--green); font-weight: 600; } .fail { color: var(--red); font-weight: 600; }
        .empty { padding: 16px; color: var(--muted); }
        footer { text-align: center; color: var(--muted); font-size: 12px; padding: 24px; }
        """;

    private const string Script = """
        (function () {
          const search = document.getElementById('q');
          const boxes = Array.from(document.querySelectorAll('[data-filter]'));
          const auto = document.getElementById('auto');
          function apply() {
            const text = search.value.trim().toLowerCase();
            const sev = new Set(boxes.filter(b => b.checked).map(b => b.dataset.filter));
            document.querySelectorAll('[data-sev]').forEach(el => {
              const visible = (el.dataset.auto === '1' ? auto.checked : sev.has(el.dataset.sev)) &&
                              (!text || el.dataset.text.includes(text));
              el.style.display = visible ? '' : 'none';
            });
          }
          [search, auto, ...boxes].forEach(el => el.addEventListener('input', apply));
          apply();
        })();
        """;

    public static string Render(SolutionResult result)
    {
        var sb = new StringBuilder();
        var all = result.AllItems.ToList();
        var breaking = all.Count(i => i.RequiresAction && i.Severity == InventorySeverity.Breaking);
        var warnings = all.Count(i => i.RequiresAction && i.Severity == InventorySeverity.Warning);
        var automatic = all.Count(i => i.AutoMigrated);
        var buildErrors = all.Count(i => i.Category == InventoryCategory.Build && i.Severity == InventorySeverity.Breaking);

        sb.Append("<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>Migração .NET 10 — {E(result.SolutionName)}</title><style>{Css}</style></head><body>");

        sb.Append("<header>");
        sb.Append($"<h1>Migração .NET Framework → .NET 10 — {E(result.SolutionName)}</h1><div class=\"meta\">");
        sb.Append($"<span>Origem: {E(result.RootDir)}</span>");
        sb.Append($"<span>Modo: {E(ReportWriter.ModeLabel(result))}</span>");
        if (result.OutputDir != null) sb.Append($"<span>Saída: {E(result.OutputDir)}</span>");
        sb.Append($"<span>Gerado em {result.FinishedAt:dd/MM/yyyy HH:mm}</span>");
        sb.Append($"<span>NuGet verificado: {(result.NuGetChecked ? "sim" : "não")}</span>");
        sb.Append($"<span>Build: {result.BuildSucceeded switch { true => "sucesso", false => "com erros", null => "não executado" }}</span>");
        if (result.LlmModel != null) sb.Append($"<span>LLM: {E(result.LlmModel)} ({result.LlmCalls} chamada(s))</span>");
        sb.Append("</div></header><main>");

        sb.Append("<div class=\"cards\">");
        Card(sb, "", result.Projects.Count.ToString(), "Projetos");
        Card(sb, "red", breaking.ToString(), "Ações bloqueantes");
        Card(sb, "amber", warnings.ToString(), "Pontos de atenção");
        Card(sb, "green", automatic.ToString(), "Resolvidos automaticamente");
        if (result.BuildSucceeded != null) Card(sb, buildErrors > 0 ? "red" : "green", buildErrors.ToString(), "Erros no build de verificação");
        var modernizations = result.AllModernizations.ToList();
        if (modernizations.Count > 0) Card(sb, "purple", modernizations.Count.ToString(), "Sugestões de modernização");
        sb.Append("</div>");

        sb.Append("<div class=\"panel\"><h2>Projetos</h2><table><thead><tr><th>Projeto</th><th>Tipo</th><th>Origem</th>")
          .Append("<th class=\"num\">Bloqueantes</th><th class=\"num\">Atenção</th><th class=\"num\">Automático</th><th class=\"num\">% automatizado</th><th>Build</th>" + (result.Architecture != null ? "<th>AWS</th>" : "") + "</tr></thead><tbody>");
        foreach (var p in result.Projects)
        {
            var build = ReportWriter.BuildLabel(result, p);
            var buildClass = p.Build == null ? "" : p.Build.Succeeded ? "ok" : "fail";
            sb.Append($"<tr><td><a href=\"#p-{Anchor(p.Project.Name)}\">{E(p.Project.Name)}</a></td><td>{E(ReportWriter.KindLabel(p.Project.Kind))}</td><td>{E(p.Project.TargetFramework)}</td>")
              .Append($"<td class=\"num\">{p.Breaking.Count()}</td><td class=\"num\">{p.Warnings.Count()}</td><td class=\"num\">{p.Automatic.Count()}</td>")
              .Append($"<td class=\"num\">{p.AutomationPercent}%</td><td class=\"{buildClass}\">{E(build)}</td>")
              .Append(result.Architecture != null ? $"<td>{E(p.Hosting?.Primary.Short() ?? "—")}</td>" : "")
              .Append("</tr>");
        }
        sb.Append("</tbody></table></div>");

        if (result.Architecture != null) RenderArchitecture(sb, result);
        if (modernizations.Count > 0) RenderModernization(sb, result, modernizations);

        sb.Append("<h2 class=\"section-h\">Inventário da migração</h2>");
        sb.Append("<div class=\"filters\"><input id=\"q\" type=\"search\" placeholder=\"Filtrar por texto, arquivo, regra...\">");
        sb.Append("<label><input type=\"checkbox\" data-filter=\"breaking\" checked> Bloqueante</label>");
        sb.Append("<label><input type=\"checkbox\" data-filter=\"warning\" checked> Atenção</label>");
        sb.Append("<label><input type=\"checkbox\" data-filter=\"info\" checked> Informativo</label>");
        sb.Append("<label><input type=\"checkbox\" id=\"auto\"> Resolvidos automaticamente</label></div>");

        if (result.GlobalItems.Count > 0)
        {
            sb.Append("<details class=\"project\" open><summary>Solução <span class=\"sub\">itens gerais</span></summary>");
            RenderItems(sb, result.GlobalItems);
            sb.Append("</details>");
        }

        foreach (var p in result.Projects)
        {
            sb.Append($"<details class=\"project\" id=\"p-{Anchor(p.Project.Name)}\" {(p.Breaking.Any() ? "open" : "")}><summary>{E(p.Project.Name)}")
              .Append($"<span class=\"sub\">{E(ReportWriter.KindLabel(p.Project.Kind))} · {E(p.Project.TargetFramework)} → net10.0 · {E(p.RelativeDir.Length == 0 ? "." : p.RelativeDir)}</span>")
              .Append($"<span class=\"badge breaking\">{p.Breaking.Count()} bloqueantes</span><span class=\"badge warning\">{p.Warnings.Count()} atenção</span><span class=\"badge auto\">{p.Automatic.Count()} automáticos</span></summary>");
            RenderItems(sb, p.Inventory);
            sb.Append("</details>");
        }

        sb.Append("</main><footer>Gerado pelo Migrator (.NET Framework → .NET 10). Itens “Resolvidos automaticamente” ficam ocultos por padrão; marque o filtro para vê-los.</footer>");
        sb.Append($"<script>{Script}</script></body></html>");
        return sb.ToString();
    }

    private static void RenderItems(StringBuilder sb, IEnumerable<InventoryItem> source)
    {
        var items = ReportWriter.Ordered(source).ToList();
        if (items.Count == 0)
        {
            sb.Append("<div class=\"empty\">Nenhum item.</div>");
            return;
        }

        var nonBuild = items.Where(i => i.Category != InventoryCategory.Build).ToList();
        foreach (var item in nonBuild) RenderItem(sb, item);

        var build = items.Where(i => i.Category == InventoryCategory.Build).ToList();
        if (build.Count == 0) return;
        sb.Append("<h3 class=\"section-title\">Build de verificação</h3>");
        sb.Append($"<div class=\"empty\">{E(BuildNote)}</div>");
        foreach (var group in build.GroupBy(i => (i.RuleId, i.Severity)).OrderBy(g => g.Key.Severity).ThenByDescending(g => g.Count()))
        {
            var first = group.First();
            var sev = SeverityClass(first);
            var text = E(string.Join(" ", group.Select(i => $"{i.RuleId} {i.Description} {i.FilePath}")).ToLowerInvariant());
            if (text.Length > 20000) text = text[..20000];
            sb.Append($"<details class=\"group\" data-sev=\"{sev}\" data-auto=\"0\" data-text=\"{text}\"><summary>")
              .Append($"<span class=\"badge {sev}\">{E(first.Severity.Display())}</span> <code class=\"rule\">{E(group.Key.RuleId)}</code> ")
              .Append($"<strong>{group.Count()} ocorrência(s)</strong> — {E(first.Suggestion)}</summary>")
              .Append("<table><thead><tr><th>Arquivo</th><th>Mensagem</th></tr></thead><tbody>");
            foreach (var i in group.Take(MaxBuildRowsPerGroup))
                sb.Append($"<tr><td class=\"file\">{E(ReportWriter.Location(i))}</td><td>{E(i.Description)}</td></tr>");
            if (group.Count() > MaxBuildRowsPerGroup)
                sb.Append($"<tr><td colspan=\"2\">... e mais {group.Count() - MaxBuildRowsPerGroup} (veja inventory.xlsx / build-verification.log)</td></tr>");
            sb.Append("</tbody></table></details>");
        }
    }

    private static void RenderItem(StringBuilder sb, InventoryItem item)
    {
        var sev = SeverityClass(item);
        var label = item.AutoMigrated ? "Automático" : item.Severity.Display();
        var text = E($"{item.Title} {item.Description} {item.Suggestion} {item.FilePath} {item.RuleId}".ToLowerInvariant());
        sb.Append($"<div class=\"item {(item.AutoMigrated ? "auto" : sev)}\" data-sev=\"{sev}\" data-auto=\"{(item.AutoMigrated ? 1 : 0)}\" data-text=\"{text}\">")
          .Append("<div class=\"head\">")
          .Append($"<span class=\"badge {(item.AutoMigrated ? "auto" : sev)}\">{E(label)}</span>")
          .Append($"<span class=\"badge cat\">{E(item.Category.Display())}</span>")
          .Append($"<code class=\"rule\">{E(item.RuleId)}</code>")
          .Append($"<span class=\"title\">{E(item.Title)}</span>");
        if (item.Occurrences > 1) sb.Append($"<span class=\"badge cat\">×{item.Occurrences}</span>");
        sb.Append("</div>");
        if (item.FilePath != null) sb.Append($"<div class=\"loc\">{E(ReportWriter.Location(item))}</div>");
        if (item.Description.Length > 0) sb.Append($"<p class=\"desc\">{E(item.Description)}</p>");
        if (item.Suggestion.Length > 0 && item.Suggestion != "Nenhuma ação necessária.")
            sb.Append($"<p class=\"sugg\"><b>Sugestão:</b> {E(item.Suggestion)}</p>");
        sb.Append("</div>");
    }

    private static string SeverityClass(InventoryItem item) => item.Severity switch
    {
        InventorySeverity.Breaking => "breaking",
        InventorySeverity.Warning => "warning",
        _ => "info"
    };

    private static void Card(StringBuilder sb, string css, string number, string label) =>
        sb.Append($"<div class=\"card {css}\"><div class=\"n\">{number}</div><div class=\"l\">{E(label)}</div></div>");

    private static string Anchor(string name) => new(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray());

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");
}
