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
        if (result.Architecture != null)
        {
            table.AddColumn(new TableColumn("[purple]Modern.[/]").RightAligned());
            table.AddColumn("AWS");
        }

        foreach (var p in result.Projects)
        {
            var build = ReportWriter.BuildLabel(result, p);
            var runtimeOk = p.Tests is not { Succeeded: false } && p.Smoke is not { Succeeded: false } && p.DockerBuildSucceeded != false;
            var buildMarkup = p.Build == null ? $"[grey]{Markup.Escape(build)}[/]" : p.Build.Succeeded && runtimeOk ? $"[green]{Markup.Escape(build)}[/]" : $"[red]{Markup.Escape(build)}[/]";
            var cells = new List<string>
            {
                Markup.Escape(p.Project.Name),
                Markup.Escape(ReportWriter.KindLabel(p.Project)),
                Markup.Escape(p.Project.TargetFramework),
                p.Breaking.Count().ToString(),
                p.Warnings.Count().ToString(),
                p.Automatic.Count().ToString(),
                buildMarkup
            };
            if (result.Architecture != null)
            {
                cells.Add(p.Modernizations.Count.ToString());
                cells.Add(Markup.Escape(p.Hosting?.Primary.Short() ?? "—"));
            }
            table.AddRow(cells.ToArray());
        }
        AnsiConsole.Write(table);

        if (result.Architecture is { } arch)
        {
            var required = arch.Components.Where(c => c.Required).Select(c => c.Service.Split(" (")[0].Split(" + ")[0]).ToList();
            AnsiConsole.MarkupLine($"Arquitetura AWS: [cyan]{Markup.Escape(string.Join(", ", required))}[/]" +
                (arch.Components.Count > required.Count ? $" [grey](+{arch.Components.Count - required.Count} recomendados)[/]" : ""));
            var mods = result.AllModernizations.ToList();
            if (mods.Count > 0)
            {
                var byKind = mods.GroupBy(m => m.Kind).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.Display().ToLowerInvariant()}");
                var high = mods.Count(m => m.Impact == Impact.High);
                AnsiConsole.MarkupLine($"Modernização: [purple]{mods.Count} sugestões[/] ({Markup.Escape(string.Join(", ", byKind))}); [red]{high}[/] de impacto alto.");
            }
        }

        var global = result.GlobalItems.Where(i => i.RequiresAction).ToList();
        foreach (var item in global.Take(10))
            AnsiConsole.MarkupLine($"[{(item.Severity == InventorySeverity.Breaking ? "red" : "yellow")}]•[/] {Markup.Escape(item.Title)}");

        if (result.LlmModel != null)
        {
            var summary = result.GlobalItems.FirstOrDefault(i => i.RuleId == "LLM-SUMMARY");
            var unavailable = result.GlobalItems.FirstOrDefault(i => i.RuleId == "LLM-UNAVAILABLE");
            var drafts = result.AllItems.Count(i => i.RuleId == "LLM-DRAFT");
            AnsiConsole.MarkupLine(unavailable != null
                ? $"LLM: [yellow]{Markup.Escape(result.LlmModel)} indisponível[/] — {Markup.Escape(unavailable.Description)}"
                : $"LLM: [cyan]{Markup.Escape(result.LlmModel)}[/], {result.LlmCalls} chamada(s)" +
                  (summary != null ? $" — {Markup.Escape(summary.Title.Replace("Correção assistida por LLM: ", ""))}" : "") +
                  (drafts > 0 ? $", {drafts} rascunho(s) de conversão" : "") +
                  (result.LlmTriagedItems > 0 ? $", {result.LlmTriagedItems} item(ns) triados" : "") +
                  (result.Architecture?.ExecutiveSummary != null ? ", resumo executivo no relatório" : ""));
        }
        if (!result.NuGetChecked && !result.Options.Offline)
            AnsiConsole.MarkupLine("[yellow]nuget.org inacessível: a compatibilidade dos pacotes não foi verificada.[/]");

        if (result.OutputDir != null)
            AnsiConsole.MarkupLine($"Aplicação migrada: [cyan]{Markup.Escape(result.OutputDir)}[/]");
        if (result.BuildSucceeded is { } ok)
            AnsiConsole.MarkupLine(ok ? "Build de verificação: [green]sucesso[/]" : "Build de verificação: [red]com erros[/] (detalhes no relatório)");
        AnsiConsole.MarkupLine($"Relatórios: [cyan]{Markup.Escape(Path.Combine(result.ReportDir!, ReportWriter.HtmlFile))}[/] (+ .md, .csv, .xlsx)");
    }
}
