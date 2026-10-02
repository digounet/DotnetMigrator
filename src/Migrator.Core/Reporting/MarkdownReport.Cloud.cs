using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class MarkdownReport
{
    private static void RenderArchitecture(StringBuilder sb, SolutionResult result)
    {
        var a = result.Architecture!;
        sb.AppendLine("## Arquitetura alvo (AWS)");
        sb.AppendLine();
        sb.AppendLine(Inline(a.Summary));
        sb.AppendLine();
        if (a.ExecutiveSummary != null)
        {
            sb.AppendLine($"### Leitura do arquiteto (LLM: {a.ExecutiveSummaryModel})");
            sb.AppendLine();
            sb.AppendLine(a.ExecutiveSummary.Replace("\r", ""));
            sb.AppendLine();
        }

        sb.AppendLine("### Hospedagem recomendada");
        sb.AppendLine();
        sb.AppendLine("| Projeto | Tipo | Hospedagem | Por quê | Pré-requisitos | Alternativas |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var h in a.Hosting)
            sb.AppendLine($"| **{Cell(h.Project)}**{(h.DockerfileGenerated ? " (Dockerfile gerado)" : "")} | {ReportWriter.KindLabel(h.Kind)} | **{Cell(h.Primary.Display())}**{(h.RequiresWindows ? " ⚠ exige Windows" : "")} | {Cells(h.Rationale)} | {Cells(h.Prerequisites)} | {Cells(h.Alternatives)} |");
        sb.AppendLine();

        sb.AppendLine("### Serviços");
        sb.AppendLine();
        sb.AppendLine("| Serviço AWS | Papel | Substitui | Por quê | Usado por | Necessidade |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var c in a.Components)
            sb.AppendLine($"| **{Cell(c.Service)}** | {Cell(c.Role)} | {Cell(c.Replaces)} | {Cell(c.Why)}{(c.Notes != null ? " _" + Cell(c.Notes) + "_" : "")} | {Cell(string.Join(", ", c.UsedBy))} | {(c.Required ? "obrigatório" : "recomendado")} |");
        sb.AppendLine();

        if (a.Diagram.Length > 0)
        {
            sb.AppendLine("### Diagrama");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine(a.Diagram);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        sb.AppendLine("### Plano de migração");
        sb.AppendLine();
        foreach (var phase in a.Phases) sb.AppendLine($"{Inline(phase)}");
        sb.AppendLine();
        if (a.Risks.Count > 0)
        {
            sb.AppendLine("### Riscos");
            sb.AppendLine();
            foreach (var r in a.Risks) sb.AppendLine($"- {Inline(r)}");
            sb.AppendLine();
        }
        if (a.CostNotes.Count > 0)
        {
            sb.AppendLine("### Custo");
            sb.AppendLine();
            foreach (var c in a.CostNotes) sb.AppendLine($"- {Inline(c)}");
            sb.AppendLine();
        }
    }

    private static void RenderModernization(StringBuilder sb, SolutionResult result)
    {
        var items = result.AllModernizations.ToList();
        if (items.Count == 0) return;
        sb.AppendLine($"## Modernização ({items.Count})");
        sb.AppendLine();
        sb.AppendLine("Sugestões que não bloqueiam a compilação: bibliotecas que passaram a ser pagas ou foram descontinuadas, código C# que compila mas muda de comportamento no .NET 10/Linux, e adaptações para a AWS. Detalhes completos em `modernization.csv` e na aba Modernização do `inventory.xlsx`.");
        sb.AppendLine();
        sb.AppendLine("| Projeto | Tipo | Impacto | Esforço | Item | Proposta | Serviço AWS |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var item in ReportWriter.OrderedModernizations(items))
            sb.AppendLine($"| {Cell(item.Project)} | {item.Kind.Display()} | {item.Impact.Display()} | {item.Effort.Display()} | **[{item.RuleId}] {Cell(item.Title)}**{(item.Occurrences > 1 ? $" (×{item.Occurrences})" : "")}<br>_{Cell(item.Why)}_{(item.Evidence != null ? $"<br>`{Cell(item.Evidence)}`" : "")} | {Cell(item.Proposal)} | {Cell(item.AwsService ?? "")} |");
        sb.AppendLine();
    }

    private static string Cells(IReadOnlyList<string> items) => items.Count == 0 ? "—" : string.Join("<br>", items.Select(i => "• " + Cell(i)));
}
