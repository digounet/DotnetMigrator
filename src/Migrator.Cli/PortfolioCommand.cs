using Migrator.Core.Portfolio;
using Spectre.Console;

namespace Migrator.Cli;

public static class PortfolioCommand
{
    public static async Task<int> ExecuteAsync(PortfolioOptions options, CancellationToken cancellationToken)
    {
        PortfolioResult result;
        try
        {
            result = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).StartAsync("Descobrindo soluções...", async ctx =>
            {
                var progress = new Progress<string>(message => ctx.Status(Markup.Escape(message)));
                return await new PortfolioRunner().RunAsync(options, progress, cancellationToken);
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or DirectoryNotFoundException or NotSupportedException or UnauthorizedAccessException or IOException)
        {
            AnsiConsole.MarkupLine($"[red]Erro:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Operação cancelada.[/]");
            return 130;
        }

        var table = new Table().RoundedBorder().Title($"[bold]Portfólio[/] — {result.Apps.Count} aplicação(ões) em {Markup.Escape(result.RootDir)}");
        table.AddColumn("Aplicação");
        table.AddColumn(new TableColumn("Proj.").RightAligned());
        table.AddColumn(new TableColumn("[red]Bloq.[/]").RightAligned());
        table.AddColumn(new TableColumn("[yellow]Atenção[/]").RightAligned());
        table.AddColumn(new TableColumn("[purple]Mod. alta[/]").RightAligned());
        table.AddColumn("Hospedagem");
        table.AddColumn("Esforço");
        table.AddColumn(new TableColumn("Onda").RightAligned());
        foreach (var a in result.Apps)
        {
            if (a.Error != null)
            {
                table.AddRow(Markup.Escape(a.Name), "—", "—", "—", "—", $"[red]{Markup.Escape(a.Error)}[/]", "—", "—");
                continue;
            }
            var band = a.Effort switch { EffortBand.High => "[red]Alto[/]", EffortBand.Medium => "[yellow]Médio[/]", _ => "[green]Baixo[/]" };
            table.AddRow(Markup.Escape(a.Name), a.Projects.ToString(), a.Breaking.ToString(), a.Warnings.ToString(), a.HighImpact.ToString(),
                Markup.Escape(string.Join(", ", a.Hosting.Select(h => h.Value > 1 ? $"{h.Value}× {h.Key}" : h.Key))) + (a.RequiresWindows ? " [yellow](Windows)[/]" : ""),
                $"{band} ({a.EffortScore})", a.Wave.ToString());
        }
        AnsiConsole.Write(table);

        foreach (var w in result.Waves)
            AnsiConsole.MarkupLine($"Onda {w.Number}: [cyan]{Markup.Escape(string.Join(", ", w.Apps))}[/] (esforço {w.EffortTotal})");
        if (result.Shared.Count > 0)
            AnsiConsole.MarkupLine($"Compartilhado entre aplicações: {Markup.Escape(string.Join("; ", result.Shared.Take(6).Select(s => $"{s.Kind} {s.Name} ({s.Apps.Count})")))}");
        var topGaps = result.ModernizationGaps.Where(g => g.Severity == "Alto").Take(5).Select(g => $"{g.RuleId} ({g.Apps})");
        if (topGaps.Any()) AnsiConsole.MarkupLine($"Gaps de impacto alto mais comuns: [purple]{Markup.Escape(string.Join(", ", topGaps))}[/]");
        if (result.Baseline != null)
        {
            var improved = result.Baseline.Count(d => d.Status == "melhorou");
            var worse = result.Baseline.Count(d => d.Status == "piorou");
            AnsiConsole.MarkupLine($"Baseline: [green]{improved} melhoraram[/], [red]{worse} pioraram[/], {result.Baseline.Count - improved - worse} iguais/novas/removidas.");
        }
        AnsiConsole.MarkupLine($"Relatórios: [cyan]{Markup.Escape(Path.Combine(result.ReportDir!, PortfolioReports.HtmlFile))}[/] (+ .md, .xlsx, .json; individuais em apps/)");
        return result.Apps.Any(a => a.Error != null) ? 2 : 0;
    }
}
