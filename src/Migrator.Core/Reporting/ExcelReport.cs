using ClosedXML.Excel;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static class ExcelReport
{
    private const int MaxCellLength = 32000;

    public static void Write(SolutionResult result, string path)
    {
        using var workbook = new XLWorkbook();
        WriteSummary(workbook.Worksheets.Add("Resumo"), result);
        WriteInventory(workbook.Worksheets.Add("Inventário"), result);
        workbook.SaveAs(path);
    }

    private static void WriteSummary(IXLWorksheet sheet, SolutionResult result)
    {
        sheet.Cell(1, 1).Value = $"Migração .NET Framework → .NET 10 — {result.SolutionName}";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;

        var meta = new (string, string)[]
        {
            ("Origem", result.RootDir),
            ("Modo", ReportWriter.ModeLabel(result)),
            ("Saída", result.OutputDir ?? "—"),
            ("Data", result.FinishedAt.ToString("dd/MM/yyyy HH:mm")),
            ("Compatibilidade NuGet verificada", result.NuGetChecked ? "Sim (nuget.org)" : "Não"),
            ("Build de verificação", result.BuildSucceeded switch { true => "Sucesso", false => "Falhou", null => "Não executado" })
        };
        var row = 3;
        foreach (var (label, value) in meta)
        {
            sheet.Cell(row, 1).Value = label;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 2).Value = value;
            row++;
        }

        row++;
        string[] headers = ["Projeto", "Tipo", "Framework de origem", "Bloqueantes", "Atenção", "Automático", "Erros de build", "% automatizado"];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(row, c + 1).Value = headers[c];
        var headerRow = row;
        foreach (var project in result.Projects)
        {
            row++;
            sheet.Cell(row, 1).Value = project.Project.Name;
            sheet.Cell(row, 2).Value = ReportWriter.KindLabel(project.Project.Kind);
            sheet.Cell(row, 3).Value = project.Project.TargetFramework;
            sheet.Cell(row, 4).Value = project.Breaking.Count();
            sheet.Cell(row, 5).Value = project.Warnings.Count();
            sheet.Cell(row, 6).Value = project.Automatic.Count();
            sheet.Cell(row, 7).Value = project.Build?.Errors.ToString() ?? "—";
            sheet.Cell(row, 8).Value = project.AutomationPercent / 100.0;
            sheet.Cell(row, 8).Style.NumberFormat.Format = "0%";
        }
        if (row > headerRow) sheet.Range(headerRow, 1, row, headers.Length).CreateTable("Projetos");
        sheet.Column(1).Width = 34;
        sheet.Column(2).Width = 60;
        for (var c = 3; c <= headers.Length; c++) sheet.Column(c).Width = 16;
    }

    private static void WriteInventory(IXLWorksheet sheet, SolutionResult result)
    {
        string[] headers = ["Projeto", "Severidade", "Categoria", "Regra", "Automático", "Título", "Arquivo", "Linha", "Ocorrências", "Descrição", "Sugestão"];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];

        var row = 1;
        foreach (var item in ReportWriter.Ordered(result.AllItems))
        {
            row++;
            sheet.Cell(row, 1).Value = item.Project;
            sheet.Cell(row, 2).Value = item.Severity.Display();
            sheet.Cell(row, 3).Value = item.Category.Display();
            sheet.Cell(row, 4).Value = item.RuleId;
            sheet.Cell(row, 5).Value = item.AutoMigrated ? "Sim" : "Não";
            sheet.Cell(row, 6).Value = Truncate(item.Title);
            sheet.Cell(row, 7).Value = item.FilePath ?? "";
            if (item.Line is { } line) sheet.Cell(row, 8).Value = line;
            sheet.Cell(row, 9).Value = item.Occurrences;
            sheet.Cell(row, 10).Value = Truncate(item.Description);
            sheet.Cell(row, 11).Value = Truncate(item.Suggestion);

            var color = item.AutoMigrated ? "#E6F4EA" : item.Severity switch
            {
                InventorySeverity.Breaking => "#FCE8E6",
                InventorySeverity.Warning => "#FEF7E0",
                _ => "#E8F0FE"
            };
            sheet.Cell(row, 2).Style.Fill.BackgroundColor = XLColor.FromHtml(color);
        }

        sheet.Range(1, 1, Math.Max(row, 2), headers.Length).CreateTable("Inventario");
        sheet.SheetView.FreezeRows(1);
        int[] widths = [24, 12, 14, 18, 11, 60, 45, 8, 11, 80, 80];
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];
        foreach (var c in new[] { 6, 10, 11 }) sheet.Column(c).Style.Alignment.WrapText = true;
        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }

    private static string Truncate(string value) => value.Length <= MaxCellLength ? value : value[..MaxCellLength] + "...";
}
