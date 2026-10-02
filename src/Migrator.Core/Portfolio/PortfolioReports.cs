using System.Net;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using Migrator.Core.Reporting;

namespace Migrator.Core.Portfolio;

public static class PortfolioReports
{
    public const string HtmlFile = "portfolio-report.html";
    public const string MarkdownFile = "portfolio-report.md";
    public const string ExcelFile = "portfolio.xlsx";
    public const string JsonFile = "portfolio.json";

    public static async Task WriteAllAsync(PortfolioResult result, string directory, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var utf8Bom = new UTF8Encoding(true);
        await File.WriteAllTextAsync(Path.Combine(directory, JsonFile), Json(result), utf8Bom, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, MarkdownFile), Markdown(result), utf8Bom, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, HtmlFile), Html(result), utf8Bom, cancellationToken);
        Excel(result, Path.Combine(directory, ExcelFile));
    }

    public static string Json(PortfolioResult result) => JsonSerializer.Serialize(result, JsonReport.Options);

    // ------------------------------------------------------------------ Markdown

    public static string Markdown(PortfolioResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Portfólio de migração — {r.Apps.Count} aplicação(ões)");
        sb.AppendLine();
        sb.AppendLine($"- **Pasta:** `{r.RootDir}`  ");
        sb.AppendLine($"- **Gerado em:** {r.GeneratedAt:dd/MM/yyyy HH:mm}  ");
        sb.AppendLine($"- **Totais:** {r.TotalBreaking} bloqueantes, {r.TotalWarnings} pontos de atenção, {r.TotalHighImpact} modernizações de impacto alto  ");
        if (r.HostingTotals.Count > 0) sb.AppendLine($"- **Hospedagem:** {string.Join(", ", r.HostingTotals.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}× {kv.Key}"))}  ");
        var byBand = r.Apps.Where(a => a.Error == null).GroupBy(a => a.Effort).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.Display().ToLowerInvariant()}");
        sb.AppendLine($"- **Esforço:** {string.Join(", ", byBand)} (pontuação de referência: bloqueantes ×3, atenção ×1, modernização alta ×2, +10 Windows, +5 por arquivo Web Forms, +8 por projeto VB, +3 por web)");
        sb.AppendLine();

        sb.AppendLine("## Aplicações (maior esforço primeiro)");
        sb.AppendLine();
        sb.AppendLine("| # | Aplicação | Projetos | Bloq. | Atenção | Autom. | Modern. (alto) | Hospedagem | Windows | Esforço | Onda |");
        sb.AppendLine("|--:|---|---|--:|--:|--:|--:|---|---|---|--:|");
        var i = 0;
        foreach (var a in r.Apps)
        {
            i++;
            if (a.Error != null) { sb.AppendLine($"| {i} | **{Cell(a.Name)}** | — | — | — | — | — | — | — | erro: {Cell(a.Error)} | — |"); continue; }
            sb.AppendLine($"| {i} | **{Cell(a.Name)}** | {a.Projects} ({Cell(string.Join(", ", a.Kinds.Select(k => $"{k.Value} {k.Key}")))}) | {a.Breaking} | {a.Warnings} | {a.AutomationPercent}% | {a.Modernizations} ({a.HighImpact}) | {Cell(string.Join(", ", a.Hosting.Select(h => h.Value > 1 ? $"{h.Value}× {h.Key}" : h.Key)))} | {(a.RequiresWindows ? "sim" : "não")} | {a.Effort.Display()} ({a.EffortScore}) | {a.Wave} |");
        }
        sb.AppendLine();

        if (r.Waves.Count > 0)
        {
            sb.AppendLine("## Ondas sugeridas");
            sb.AppendLine();
            foreach (var w in r.Waves)
            {
                sb.AppendLine($"### Onda {w.Number} — {w.Apps.Count} aplicação(ões), esforço {w.EffortTotal}");
                sb.AppendLine();
                sb.AppendLine(w.Rationale);
                sb.AppendLine();
                foreach (var name in w.Apps)
                {
                    var app = r.Apps.First(a => a.Name == name);
                    sb.AppendLine($"- **{Cell(name)}** ({app.Effort.Display()}, {app.EffortScore}){(app.WaveReason != null ? $" — {Cell(app.WaveReason)}" : "")}");
                }
                sb.AppendLine();
            }
        }

        if (r.Shared.Count > 0)
        {
            sb.AppendLine("## Infraestrutura compartilhada entre aplicações");
            sb.AppendLine();
            sb.AppendLine("Bancos e hosts internos usados por mais de uma aplicação: o cutover precisa ser coordenado (mesma onda).");
            sb.AppendLine();
            sb.AppendLine("| Tipo | Recurso | Aplicações |");
            sb.AppendLine("|---|---|---|");
            foreach (var s in r.Shared) sb.AppendLine($"| {s.Kind} | `{Cell(s.Name)}` | {Cell(string.Join(", ", s.Apps))} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Gaps mais frequentes (inventário)");
        sb.AppendLine();
        sb.AppendLine("| Regra | Item | Severidade | Aplicações | Ocorrências |");
        sb.AppendLine("|---|---|---|--:|--:|");
        foreach (var g in r.InventoryGaps.Take(40)) sb.AppendLine($"| `{g.RuleId}` | {Cell(g.Title)} | {g.Severity} | {g.Apps} | {g.Occurrences} |");
        sb.AppendLine();

        sb.AppendLine("## Gaps mais frequentes (modernização)");
        sb.AppendLine();
        sb.AppendLine("| Regra | Item | Tipo | Impacto | Aplicações | Serviço AWS |");
        sb.AppendLine("|---|---|---|---|--:|---|");
        foreach (var g in r.ModernizationGaps.Take(40)) sb.AppendLine($"| `{g.RuleId}` | {Cell(g.Title)} | {g.Source} | {g.Severity} | {g.Apps} | {Cell(g.AwsService ?? "")} |");
        sb.AppendLine();

        if (r.AwsServices.Count > 0)
        {
            sb.AppendLine("## Serviços AWS obrigatórios (quantas aplicações precisam)");
            sb.AppendLine();
            foreach (var kv in r.AwsServices.OrderByDescending(kv => kv.Value)) sb.AppendLine($"- {Cell(kv.Key)}: {kv.Value}");
            sb.AppendLine();
        }

        if (r.Baseline != null)
        {
            sb.AppendLine($"## Comparação com a baseline (`{r.BaselinePath}`)");
            sb.AppendLine();
            sb.AppendLine("| Aplicação | Bloqueantes | Atenção | Modern. alta | Esforço | Situação |");
            sb.AppendLine("|---|---|---|---|---|---|");
            foreach (var d in r.Baseline) sb.AppendLine($"| {Cell(d.App)} | {d.BreakingBefore} → {d.BreakingAfter} | {d.WarningsBefore} → {d.WarningsAfter} | {d.HighImpactBefore} → {d.HighImpactAfter} | {d.EffortBefore} → {d.EffortAfter} | {d.Status} |");
            sb.AppendLine();
        }

        sb.AppendLine("Relatórios individuais em `apps/<aplicação>/migration-report.html`.");
        return sb.ToString();
    }

    private static string Cell(string text) => text.Replace("\r", "").Replace("\n", " ").Replace("|", "\\|");

    // ------------------------------------------------------------------ HTML

    private const string Css = """
        :root { --red:#c5221f; --red-bg:#fce8e6; --amber:#b06000; --amber-bg:#fef7e0; --green:#137333; --green-bg:#e6f4ea; --blue:#1967d2; --purple:#6a1b9a; --border:#dadce0; --muted:#5f6368; }
        * { box-sizing: border-box; } body { margin: 0; font-family: "Segoe UI", system-ui, sans-serif; background: #f8f9fa; color: #202124; font-size: 14px; }
        header { background: #1a3a6b; color: #fff; padding: 24px 32px; } header h1 { margin: 0 0 8px; font-size: 22px; } header .meta { display: flex; flex-wrap: wrap; gap: 6px 24px; font-size: 13px; opacity: .9; }
        main { max-width: 1320px; margin: 0 auto; padding: 24px 32px 64px; }
        .cards { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 12px; margin-bottom: 24px; }
        .card { background: #fff; border: 1px solid var(--border); border-radius: 8px; padding: 16px; } .card .n { font-size: 28px; font-weight: 600; } .card .l { color: var(--muted); font-size: 12px; margin-top: 4px; }
        .card.red .n { color: var(--red); } .card.amber .n { color: var(--amber); } .card.purple .n { color: var(--purple); } .card.blue .n { color: var(--blue); }
        .panel { background: #fff; border: 1px solid var(--border); border-radius: 8px; margin-bottom: 20px; overflow: hidden; } .panel > h2 { margin: 0; padding: 14px 16px; font-size: 16px; border-bottom: 1px solid var(--border); }
        .panel .body { padding: 14px 16px; line-height: 1.5; } .panel p { margin: 0 0 10px; }
        table { width: 100%; border-collapse: collapse; } th, td { text-align: left; padding: 8px 10px; border-bottom: 1px solid var(--border); vertical-align: top; }
        th { background: #f1f3f4; font-size: 12px; text-transform: uppercase; letter-spacing: .03em; color: var(--muted); cursor: pointer; } td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
        .badge { display: inline-block; padding: 2px 8px; border-radius: 10px; font-size: 11px; font-weight: 600; white-space: nowrap; }
        .badge.high { background: var(--red-bg); color: var(--red); } .badge.medium { background: var(--amber-bg); color: var(--amber); } .badge.low { background: var(--green-bg); color: var(--green); } .badge.cat { background: #f1f3f4; color: #3c4043; }
        .wave { border-left: 4px solid var(--blue); padding: 8px 12px; margin: 8px 0; background: #f8f9fa; } .muted { color: var(--muted); font-size: 12px; }
        a { color: var(--blue); }
        """;

    private const string Script = """
        document.querySelectorAll('table.sortable th').forEach((th, idx) => th.addEventListener('click', () => {
          const table = th.closest('table'); const tbody = table.tBodies[0]; const rows = Array.from(tbody.rows);
          const numeric = th.classList.contains('num'); const asc = !(th.dataset.asc === '1'); th.dataset.asc = asc ? '1' : '0';
          rows.sort((a, b) => { const x = a.cells[idx].dataset.v ?? a.cells[idx].textContent.trim(); const y = b.cells[idx].dataset.v ?? b.cells[idx].textContent.trim();
            const r = numeric ? (parseFloat(x) || 0) - (parseFloat(y) || 0) : x.localeCompare(y, 'pt-BR'); return asc ? r : -r; });
          rows.forEach(r => tbody.appendChild(r));
        }));
        """;

    public static string Html(PortfolioResult r)
    {
        var sb = new StringBuilder();
        sb.Append($"<!DOCTYPE html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Portfólio de migração</title><style>{Css}</style></head><body>");
        sb.Append($"<header><h1>Portfólio de migração — {r.Apps.Count} aplicação(ões)</h1><div class=\"meta\"><span>Pasta: {E(r.RootDir)}</span><span>Gerado em {r.GeneratedAt:dd/MM/yyyy HH:mm}</span>");
        if (r.HostingTotals.Count > 0) sb.Append($"<span>Hospedagem: {E(string.Join(", ", r.HostingTotals.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}× {kv.Key}")))}</span>");
        sb.Append("</div></header><main>");

        var analyzed = r.Apps.Where(a => a.Error == null).ToList();
        sb.Append("<div class=\"cards\">");
        Card(sb, "", r.Apps.Count.ToString(), "Aplicações");
        Card(sb, "red", r.TotalBreaking.ToString(), "Itens bloqueantes");
        Card(sb, "amber", r.TotalWarnings.ToString(), "Pontos de atenção");
        Card(sb, "purple", r.TotalHighImpact.ToString(), "Modernizações de impacto alto");
        Card(sb, "blue", analyzed.Count(a => a.RequiresWindows).ToString(), "Exigem Windows");
        Card(sb, "", analyzed.Count(a => a.Effort == EffortBand.Low).ToString(), "Esforço baixo (quick wins)");
        sb.Append("</div>");

        sb.Append("<div class=\"panel\"><h2>Aplicações</h2><div class=\"body\"><p class=\"muted\">Clique nos cabeçalhos para ordenar. Pontuação de esforço: bloqueantes ×3, atenção ×1, modernização alta ×2, +10 Windows, +5 por arquivo Web Forms, +8 por projeto VB, +3 por projeto web.</p></div>");
        sb.Append("<table class=\"sortable\"><thead><tr><th>Aplicação</th><th>Projetos</th><th class=\"num\">Bloq.</th><th class=\"num\">Atenção</th><th class=\"num\">Autom.</th><th class=\"num\">Modern. (alto)</th><th>Hospedagem</th><th>Windows</th><th class=\"num\">Esforço</th><th class=\"num\">Onda</th><th>Riscos principais</th></tr></thead><tbody>");
        foreach (var a in r.Apps)
        {
            if (a.Error != null) { sb.Append($"<tr><td><strong>{E(a.Name)}</strong><div class=\"muted\">{E(a.InputPath)}</div></td><td colspan=\"10\" style=\"color:var(--red)\">Falha na análise: {E(a.Error)}</td></tr>"); continue; }
            var report = a.ReportDir != null ? Path.Combine(Path.GetRelativePath(r.ReportDir ?? r.RootDir, a.ReportDir), ReportWriter.HtmlFile).Replace('\\', '/') : null;
            var band = a.Effort.ToString().ToLowerInvariant();
            sb.Append($"<tr><td><strong>{(report != null ? $"<a href=\"{E(report)}\">{E(a.Name)}</a>" : E(a.Name))}</strong><div class=\"muted\">{E(Path.GetRelativePath(r.RootDir, a.InputPath))}</div></td>")
              .Append($"<td>{a.Projects}<div class=\"muted\">{E(string.Join(", ", a.Kinds.Select(k => $"{k.Value} {k.Key}")))}</div></td>")
              .Append($"<td class=\"num\">{a.Breaking}</td><td class=\"num\">{a.Warnings}</td><td class=\"num\">{a.AutomationPercent}%</td><td class=\"num\">{a.Modernizations} ({a.HighImpact})</td>")
              .Append($"<td>{E(string.Join(", ", a.Hosting.Select(h => h.Value > 1 ? $"{h.Value}× {h.Key}" : h.Key)))}</td><td>{(a.RequiresWindows ? "<span class=\"badge high\">sim</span>" : "não")}</td>")
              .Append($"<td class=\"num\" data-v=\"{a.EffortScore}\"><span class=\"badge {band}\">{E(a.Effort.Display())}</span> {a.EffortScore}</td><td class=\"num\">{a.Wave}</td>")
              .Append($"<td class=\"muted\">{E(string.Join(" · ", a.TopRisks.Select(x => x.Length > 110 ? x[..110] + "..." : x)))}</td></tr>");
        }
        sb.Append("</tbody></table></div>");

        if (r.Waves.Count > 0)
        {
            sb.Append("<div class=\"panel\"><h2>Ondas sugeridas</h2><div class=\"body\">");
            foreach (var w in r.Waves)
                sb.Append($"<div class=\"wave\"><strong>Onda {w.Number}</strong> — {w.Apps.Count} aplicação(ões), esforço {w.EffortTotal}<br><span class=\"muted\">{E(w.Rationale)}</span><br>{E(string.Join(", ", w.Apps))}</div>");
            sb.Append("</div></div>");
        }

        if (r.Shared.Count > 0)
        {
            sb.Append("<div class=\"panel\"><h2>Infraestrutura compartilhada</h2><table><thead><tr><th>Tipo</th><th>Recurso</th><th>Aplicações</th></tr></thead><tbody>");
            foreach (var s in r.Shared) sb.Append($"<tr><td>{E(s.Kind)}</td><td><code>{E(s.Name)}</code></td><td>{E(string.Join(", ", s.Apps))}</td></tr>");
            sb.Append("</tbody></table></div>");
        }

        sb.Append("<div class=\"panel\"><h2>Gaps mais frequentes — inventário</h2><table class=\"sortable\"><thead><tr><th>Regra</th><th>Item</th><th>Severidade</th><th class=\"num\">Aplicações</th><th class=\"num\">Ocorrências</th><th>Onde</th></tr></thead><tbody>");
        foreach (var g in r.InventoryGaps.Take(60))
            sb.Append($"<tr><td><code>{E(g.RuleId)}</code></td><td>{E(g.Title)}</td><td><span class=\"badge {(g.Severity == "Bloqueante" ? "high" : "medium")}\">{E(g.Severity)}</span></td><td class=\"num\">{g.Apps}</td><td class=\"num\">{g.Occurrences}</td><td class=\"muted\">{E(string.Join(", ", g.AppNames.Take(8)))}{(g.AppNames.Count > 8 ? "..." : "")}</td></tr>");
        sb.Append("</tbody></table></div>");

        sb.Append("<div class=\"panel\"><h2>Gaps mais frequentes — modernização</h2><table class=\"sortable\"><thead><tr><th>Regra</th><th>Item</th><th>Tipo</th><th>Impacto</th><th class=\"num\">Aplicações</th><th>Serviço AWS</th></tr></thead><tbody>");
        foreach (var g in r.ModernizationGaps.Take(60))
            sb.Append($"<tr><td><code>{E(g.RuleId)}</code></td><td>{E(g.Title)}</td><td><span class=\"badge cat\">{E(g.Source)}</span></td><td><span class=\"badge {(g.Severity == "Alto" ? "high" : g.Severity == "Médio" ? "medium" : "low")}\">{E(g.Severity)}</span></td><td class=\"num\">{g.Apps}</td><td>{E(g.AwsService ?? "")}</td></tr>");
        sb.Append("</tbody></table></div>");

        if (r.AwsServices.Count > 0)
        {
            sb.Append("<div class=\"panel\"><h2>Serviços AWS obrigatórios</h2><table><thead><tr><th>Serviço</th><th class=\"num\">Aplicações</th></tr></thead><tbody>");
            foreach (var kv in r.AwsServices.OrderByDescending(kv => kv.Value)) sb.Append($"<tr><td>{E(kv.Key)}</td><td class=\"num\">{kv.Value}</td></tr>");
            sb.Append("</tbody></table></div>");
        }

        if (r.Baseline != null)
        {
            sb.Append($"<div class=\"panel\"><h2>Comparação com a baseline</h2><div class=\"body\"><p class=\"muted\">{E(r.BaselinePath ?? "")}</p></div><table><thead><tr><th>Aplicação</th><th>Bloqueantes</th><th>Atenção</th><th>Modern. alta</th><th>Esforço</th><th>Situação</th></tr></thead><tbody>");
            foreach (var d in r.Baseline)
                sb.Append($"<tr><td>{E(d.App)}</td><td>{d.BreakingBefore} → {d.BreakingAfter}</td><td>{d.WarningsBefore} → {d.WarningsAfter}</td><td>{d.HighImpactBefore} → {d.HighImpactAfter}</td><td>{d.EffortBefore} → {d.EffortAfter}</td><td><span class=\"badge {(d.Status == "melhorou" ? "low" : d.Status == "piorou" ? "high" : "cat")}\">{E(d.Status)}</span></td></tr>");
            sb.Append("</tbody></table></div>");
        }

        sb.Append($"</main><script>{Script}</script></body></html>");
        return sb.ToString();
    }

    private static void Card(StringBuilder sb, string css, string n, string label) => sb.Append($"<div class=\"card {css}\"><div class=\"n\">{n}</div><div class=\"l\">{E(label)}</div></div>");

    private static string E(string? text) => WebUtility.HtmlEncode(text ?? "");

    // ------------------------------------------------------------------ Excel

    public static void Excel(PortfolioResult r, string path)
    {
        using var wb = new XLWorkbook();
        var apps = wb.Worksheets.Add("Aplicações");
        string[] headers = ["Aplicação", "Caminho", "Projetos", "Tipos", "Bloqueantes", "Atenção", "Automático", "% automatizado", "Modernizações", "Impacto alto", "Licenças", "Web Forms (arquivos)", "Projetos VB", "Exige Windows", "Hospedagem", "Esforço (pontos)", "Esforço", "Onda", "Bancos", "Hosts internos", "Erro"];
        for (var c = 0; c < headers.Length; c++) apps.Cell(1, c + 1).Value = headers[c];
        var row = 1;
        foreach (var a in r.Apps)
        {
            row++;
            var values = new object[] { a.Name, a.InputPath, a.Projects, string.Join(", ", a.Kinds.Select(k => $"{k.Value} {k.Key}")), a.Breaking, a.Warnings, a.Automatic, a.AutomationPercent / 100.0, a.Modernizations, a.HighImpact, a.LicenseIssues, a.WebFormsFiles, a.VbProjects, a.RequiresWindows ? "Sim" : "Não", string.Join(", ", a.Hosting.Select(h => $"{h.Value}× {h.Key}")), a.EffortScore, a.Effort.Display(), a.Wave, string.Join("; ", a.Databases), string.Join("; ", a.InternalHosts), a.Error ?? "" };
            for (var c = 0; c < values.Length; c++) apps.Cell(row, c + 1).Value = values[c] switch { int n => n, double d => d, string s => s, _ => values[c].ToString() };
            apps.Cell(row, 8).Style.NumberFormat.Format = "0%";
            apps.Cell(row, 17).Style.Fill.BackgroundColor = XLColor.FromHtml(a.Effort switch { EffortBand.High => "#FCE8E6", EffortBand.Medium => "#FEF7E0", _ => "#E6F4EA" });
        }
        apps.Range(1, 1, Math.Max(row, 2), headers.Length).CreateTable("Aplicacoes");
        apps.Columns().AdjustToContents(1, 60);

        void Gaps(string name, List<PortfolioGap> gaps)
        {
            var sheet = wb.Worksheets.Add(name);
            string[] h = ["Regra", "Item", "Origem/Tipo", "Severidade/Impacto", "Aplicações", "Ocorrências", "Serviço AWS", "Quais aplicações"];
            for (var c = 0; c < h.Length; c++) sheet.Cell(1, c + 1).Value = h[c];
            var rr = 1;
            foreach (var g in gaps)
            {
                rr++;
                sheet.Cell(rr, 1).Value = g.RuleId; sheet.Cell(rr, 2).Value = g.Title; sheet.Cell(rr, 3).Value = g.Source; sheet.Cell(rr, 4).Value = g.Severity;
                sheet.Cell(rr, 5).Value = g.Apps; sheet.Cell(rr, 6).Value = g.Occurrences; sheet.Cell(rr, 7).Value = g.AwsService ?? ""; sheet.Cell(rr, 8).Value = string.Join(", ", g.AppNames);
            }
            sheet.Range(1, 1, Math.Max(rr, 2), h.Length).CreateTable(name.Replace(" ", ""));
            sheet.Columns().AdjustToContents(1, 70);
        }
        Gaps("Gaps inventário", r.InventoryGaps);
        Gaps("Gaps modernização", r.ModernizationGaps);

        var shared = wb.Worksheets.Add("Compartilhado");
        shared.Cell(1, 1).Value = "Tipo"; shared.Cell(1, 2).Value = "Recurso"; shared.Cell(1, 3).Value = "Aplicações";
        var sr = 1;
        foreach (var s in r.Shared) { sr++; shared.Cell(sr, 1).Value = s.Kind; shared.Cell(sr, 2).Value = s.Name; shared.Cell(sr, 3).Value = string.Join(", ", s.Apps); }
        shared.Range(1, 1, Math.Max(sr, 2), 3).CreateTable("Compartilhado");
        shared.Columns().AdjustToContents(1, 80);

        var waves = wb.Worksheets.Add("Ondas");
        waves.Cell(1, 1).Value = "Onda"; waves.Cell(1, 2).Value = "Aplicação"; waves.Cell(1, 3).Value = "Esforço"; waves.Cell(1, 4).Value = "Motivo"; waves.Cell(1, 5).Value = "Racional da onda";
        var wr = 1;
        foreach (var w in r.Waves)
            foreach (var name in w.Apps)
            {
                var app = r.Apps.First(a => a.Name == name);
                wr++;
                waves.Cell(wr, 1).Value = w.Number; waves.Cell(wr, 2).Value = name; waves.Cell(wr, 3).Value = app.EffortScore; waves.Cell(wr, 4).Value = app.WaveReason ?? ""; waves.Cell(wr, 5).Value = w.Rationale;
            }
        if (wr > 1) waves.Range(1, 1, wr, 5).CreateTable("Ondas");
        waves.Columns().AdjustToContents(1, 80);

        if (r.Baseline != null)
        {
            var b = wb.Worksheets.Add("Baseline");
            string[] h = ["Aplicação", "Bloqueantes antes", "Bloqueantes depois", "Atenção antes", "Atenção depois", "Impacto alto antes", "Impacto alto depois", "Esforço antes", "Esforço depois", "Situação"];
            for (var c = 0; c < h.Length; c++) b.Cell(1, c + 1).Value = h[c];
            var br = 1;
            foreach (var d in r.Baseline)
            {
                br++;
                var v = new object[] { d.App, d.BreakingBefore, d.BreakingAfter, d.WarningsBefore, d.WarningsAfter, d.HighImpactBefore, d.HighImpactAfter, d.EffortBefore, d.EffortAfter, d.Status };
                for (var c = 0; c < v.Length; c++) b.Cell(br, c + 1).Value = v[c] switch { int n => n, string s => s, _ => v[c].ToString() };
            }
            b.Range(1, 1, Math.Max(br, 2), h.Length).CreateTable("Baseline");
            b.Columns().AdjustToContents();
        }
        wb.SaveAs(path);
    }
}
