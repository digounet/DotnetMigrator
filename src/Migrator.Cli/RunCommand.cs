using Migrator.Core.Migration;
using Migrator.Core.Models;
using Migrator.Core.Reporting;
using Spectre.Console;

namespace Migrator.Cli;

public static class RunCommand
{
    public static async Task<int> ExecuteAsync(MigrationOptions options, CancellationToken cancellationToken)
    {
        SolutionResult result;
        try
        {
            result = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Preparando...", async ctx =>
                {
                    var progress = new Progress<string>(message => ctx.Status(Markup.Escape(message)));
                    return await new MigrationEngine().RunAsync(options, progress, cancellationToken);
                });
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or NotSupportedException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            AnsiConsole.MarkupLine($"[red]Erro:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("[yellow]Operação cancelada.[/]");
            return 130;
        }

        PrintSummary(result);
        var hasBlocking = result.AllItems.Any(i => i.RequiresAction && i.Severity == InventorySeverity.Breaking);
        return hasBlocking || result.BuildSucceeded == false ? 2 : 0;
    }

    private static void PrintSummary(SolutionResult result)
    {
        AnsiConsole.WriteLine();
        var table = new Table().RoundedBorder().Title($"[bold]{Markup.Escape(result.SolutionName)}[/] — {Markup.Escape(ReportWriter.ModeLabel(result))}");
        table.AddColumn("Projeto");
        table.AddColumn("Tipo");
        table.AddColumn("Origem");
        table.AddColumn(new TableColumn("[red]Bloqueantes[/]").RightAligned());
        table.AddColumn(new TableColumn("[yellow]Atenção[/]").RightAligned());
        table.AddColumn(new TableColumn("[green]Automático[/]").RightAligned());
        table.AddColumn("Build");

        foreach (var p in result.Projects)
        {
            var build = ReportWriter.BuildLabel(result, p);
            var buildMarkup = p.Build == null ? $"[grey]{Markup.Escape(build)}[/]" : p.Build.Succeeded ? "[green]OK[/]" : $"[red]{Markup.Escape(build)}[/]";
            table.AddRow(
                Markup.Escape(p.Project.Name),
                Markup.Escape(ReportWriter.KindLabel(p.Project.Kind)),
                Markup.Escape(p.Project.TargetFramework),
                p.Breaking.Count().ToString(),
                p.Warnings.Count().ToString(),
                p.Automatic.Count().ToString(),
                buildMarkup);
        }
        AnsiConsole.Write(table);

        var global = result.GlobalItems.Where(i => i.RequiresAction).ToList();
        foreach (var item in global.Take(10))
            AnsiConsole.MarkupLine($"[{(item.Severity == InventorySeverity.Breaking ? "red" : "yellow")}]•[/] {Markup.Escape(item.Title)}");

        if (!result.NuGetChecked && !result.Options.Offline)
            AnsiConsole.MarkupLine("[yellow]nuget.org inacessível: a compatibilidade dos pacotes não foi verificada.[/]");

        if (result.OutputDir != null)
            AnsiConsole.MarkupLine($"Aplicação migrada: [cyan]{Markup.Escape(result.OutputDir)}[/]");
        if (result.BuildSucceeded is { } ok)
            AnsiConsole.MarkupLine(ok ? "Build de verificação: [green]sucesso[/]" : "Build de verificação: [red]com erros[/] (detalhes no relatório)");
        AnsiConsole.MarkupLine($"Relatórios: [cyan]{Markup.Escape(Path.Combine(result.ReportDir!, ReportWriter.HtmlFile))}[/] (+ .md, .csv, .xlsx)");
    }
}
