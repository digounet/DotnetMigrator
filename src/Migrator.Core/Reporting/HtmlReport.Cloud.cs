using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class HtmlReport
{
    private const string MermaidScript = """
        <script type="module">
          try {
            const m = await import("https://cdn.jsdelivr.net/npm/mermaid@11/dist/mermaid.esm.min.mjs");
            m.default.initialize({ startOnLoad: false, theme: "neutral" });
            await m.default.run({ querySelector: ".mermaid" });
          } catch (e) { document.querySelectorAll(".mermaid-note").forEach(n => n.textContent = "Sem acesso à internet o diagrama fica como texto (Mermaid); cole em https://mermaid.live ou veja o migration-report.md."); }
        </script>
        """;

    private static void RenderArchitecture(StringBuilder sb, SolutionResult result)
    {
        var a = result.Architecture!;
        sb.Append("<div class=\"panel\" id=\"aws\"><h2>3. Arquitetura alvo (AWS)</h2><div class=\"body\">");
        sb.Append($"<p class=\"lead\">{E(a.Summary)}</p>");
        if (a.ExecutiveSummary != null)
        {
            sb.Append("<h3>Leitura do arquiteto (LLM)</h3>");
            sb.Append($"<div class=\"narrative\">{SimpleMarkdown(a.ExecutiveSummary)}</div>");
            sb.Append($"<p class=\"mermaid-note\">Texto gerado por {E(a.ExecutiveSummaryModel)} a partir dos sinais detectados; confira contra as tabelas abaixo.</p>");
        }

        sb.Append("<h3>Hospedagem recomendada por projeto</h3>");
        sb.Append("<table><thead><tr><th>Projeto</th><th>Tipo</th><th>Hospedagem</th><th>Por quê</th><th>Pré-requisitos</th><th>Alternativas</th></tr></thead><tbody>");
        foreach (var h in a.Hosting)
        {
            sb.Append($"<tr><td><strong>{E(h.Project)}</strong>{(h.DockerfileGenerated ? "<br><span class=\"badge auto\">Dockerfile</span>" : "")}</td><td>{E(ReportWriter.KindLabel(h.Kind))}</td>")
              .Append($"<td><strong>{E(h.Primary.Display())}</strong>{(h.RequiresWindows ? "<br><span class=\"badge warning\">exige Windows</span>" : "")}</td>")
              .Append($"<td class=\"wrap\">{List(h.Rationale)}</td><td class=\"wrap\">{List(h.Prerequisites)}</td><td class=\"wrap\">{List(h.Alternatives)}</td></tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<h3>Serviços da arquitetura</h3>");
        sb.Append("<table><thead><tr><th>Serviço AWS</th><th>Papel</th><th>Substitui</th><th>Por quê</th><th>Usado por</th><th>Necessidade</th></tr></thead><tbody>");
        foreach (var c in a.Components)
            sb.Append($"<tr><td><strong>{E(c.Service)}</strong></td><td class=\"wrap\">{E(c.Role)}</td><td class=\"wrap\">{E(c.Replaces)}</td>")
              .Append($"<td class=\"wrap\">{E(c.Why)}{(c.Notes != null ? $"<br><span class=\"opt\">{E(c.Notes)}</span>" : "")}</td>")
              .Append($"<td>{E(string.Join(", ", c.UsedBy))}</td><td>{(c.Required ? "<span class=\"req\">obrigatório</span>" : "<span class=\"opt\">recomendado</span>")}</td></tr>");
        sb.Append("</tbody></table>");

        if (a.Diagram.Length > 0)
        {
            sb.Append("<h3>Diagrama</h3>");
            sb.Append($"<pre class=\"mermaid\">{E(a.Diagram)}</pre>");
            sb.Append("<p class=\"mermaid-note\">Diagrama renderizado com Mermaid (requer internet); o código-fonte também está no migration-report.md.</p>");
        }

        sb.Append("<h3>Plano de migração</h3><ol>");
        foreach (var phase in a.Phases) sb.Append($"<li>{E(StripNumber(phase))}</li>");
        sb.Append("</ol>");

        if (a.Risks.Count > 0)
        {
            sb.Append("<h3>Riscos e pontos de atenção</h3><ul>");
            foreach (var risk in a.Risks) sb.Append($"<li>{E(risk)}</li>");
            sb.Append("</ul>");
        }
        if (a.CostNotes.Count > 0)
        {
            sb.Append("<h3>Custo</h3><ul>");
            foreach (var note in a.CostNotes) sb.Append($"<li>{E(note)}</li>");
            sb.Append("</ul>");
        }
        sb.Append("</div></div>");
        sb.Append(MermaidScript);
    }

    private static void RenderModernization(StringBuilder sb, SolutionResult result, List<ModernizationItem> items)
    {
        sb.Append("<div class=\"panel\" id=\"modernizacao\"><h2>5. Modernização</h2>");
        sb.Append("<div class=\"body\"><p class=\"lead\">Sugestões que não bloqueiam a compilação: bibliotecas que passaram a ser pagas ou foram descontinuadas, código C# que compila mas muda de comportamento no .NET 10/Linux, e adaptações para rodar bem na AWS. Ordenadas por impacto.</p>");

        var byKind = items.GroupBy(i => i.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.Display()}");
        sb.Append($"<p class=\"lead\"><span class=\"opt\">{E(string.Join(" · ", byKind))}</span></p></div>");

        foreach (var group in items.GroupBy(i => i.Project).OrderBy(g => result.Projects.FindIndex(p => p.Project.Name == g.Key)))
        {
            sb.Append($"<h3 class=\"section-title\">{E(group.Key)}</h3>");
            foreach (var item in ReportWriter.OrderedModernizations(group))
            {
                var kind = item.Kind.ToString().ToLowerInvariant();
                var impact = item.Impact.ToString().ToLowerInvariant();
                var text = E($"{item.Title} {item.Why} {item.Proposal} {item.Evidence} {item.RuleId} {item.AwsService}".ToLowerInvariant());
                sb.Append($"<div class=\"item mod\" data-mod=\"1\" data-text=\"{text}\"><div class=\"head\">")
                  .Append($"<span class=\"badge {kind}\">{E(item.Kind.Display())}</span>")
                  .Append($"<span class=\"badge {impact}\">impacto {E(item.Impact.Display().ToLowerInvariant())}</span>")
                  .Append($"<span class=\"badge cat\">esforço {E(item.Effort.Display().ToLowerInvariant())}</span>")
                  .Append($"<code class=\"rule\">{E(item.RuleId)}</code><span class=\"title\">{E(item.Title)}</span>");
                if (item.Occurrences > 1) sb.Append($"<span class=\"badge cat\">×{item.Occurrences}</span>");
                if (item.AwsService != null) sb.Append($"<span class=\"badge cloud\">{E(item.AwsService)}</span>");
                sb.Append("</div>");
                if (item.Evidence != null) sb.Append($"<div class=\"loc\">{E(item.Evidence)}</div>");
                sb.Append($"<p class=\"why\">{E(item.Why)}</p>");
                sb.Append($"<p class=\"sugg\"><b>Proposta:</b> {E(item.Proposal)}</p>");
                sb.Append("</div>");
            }
        }
        sb.Append("</div>");
    }

    /// <summary>Paragraphs, bullet lists and **bold** are enough for the model's narrative; everything is HTML-encoded first.</summary>
    internal static string SimpleMarkdown(string markdown)
    {
        var sb = new StringBuilder();
        var inList = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) { if (inList) { sb.Append("</ul>"); inList = false; } continue; }
            var isBullet = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+[.)]\s");
            var text = isBullet ? System.Text.RegularExpressions.Regex.Replace(line, @"^(- |\* |\d+[.)]\s)", "") : line.TrimStart('#', ' ');
            var html = System.Text.RegularExpressions.Regex.Replace(E(text), @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            html = System.Text.RegularExpressions.Regex.Replace(html, @"`([^`]+)`", "<code>$1</code>");
            if (isBullet)
            {
                if (!inList) { sb.Append("<ul>"); inList = true; }
                sb.Append($"<li>{html}</li>");
            }
            else
            {
                if (inList) { sb.Append("</ul>"); inList = false; }
                sb.Append(line.StartsWith('#') ? $"<h4>{html}</h4>" : $"<p>{html}</p>");
            }
        }
        if (inList) sb.Append("</ul>");
        return sb.ToString();
    }

    private static string List(IReadOnlyList<string> items) =>
        items.Count == 0 ? "<span class=\"opt\">—</span>" : "<ul>" + string.Concat(items.Select(i => $"<li>{E(i)}</li>")) + "</ul>";

    private static string StripNumber(string phase)
    {
        var dot = phase.IndexOf(". ", StringComparison.Ordinal);
        return dot > 0 && dot < 4 && phase[..dot].All(char.IsDigit) ? phase[(dot + 2)..] : phase;
    }
}
