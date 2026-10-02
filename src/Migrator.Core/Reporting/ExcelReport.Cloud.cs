using ClosedXML.Excel;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class ExcelReport
{
    private static void WriteModernization(IXLWorksheet sheet, SolutionResult result)
    {
        string[] headers = ["Projeto", "Tipo", "Impacto", "Esforço", "Regra", "Item", "Por quê", "Proposta", "Evidência", "Ocorrências", "Serviço AWS"];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        var row = 1;
        foreach (var item in ReportWriter.OrderedModernizations(result.AllModernizations))
        {
            row++;
            sheet.Cell(row, 1).Value = item.Project;
            sheet.Cell(row, 2).Value = item.Kind.Display();
            sheet.Cell(row, 3).Value = item.Impact.Display();
            sheet.Cell(row, 4).Value = item.Effort.Display();
            sheet.Cell(row, 5).Value = item.RuleId;
            sheet.Cell(row, 6).Value = Truncate(item.Title);
            sheet.Cell(row, 7).Value = Truncate(item.Why);
            sheet.Cell(row, 8).Value = Truncate(item.Proposal);
            sheet.Cell(row, 9).Value = Truncate(item.Evidence ?? "");
            sheet.Cell(row, 10).Value = item.Occurrences;
            sheet.Cell(row, 11).Value = item.AwsService ?? "";
            sheet.Cell(row, 3).Style.Fill.BackgroundColor = XLColor.FromHtml(item.Impact switch { Impact.High => "#FCE8E6", Impact.Medium => "#FEF7E0", _ => "#E6F4EA" });
        }
        sheet.Range(1, 1, Math.Max(row, 2), headers.Length).CreateTable("Modernizacao");
        sheet.SheetView.FreezeRows(1);
        int[] widths = [24, 14, 10, 10, 24, 50, 70, 80, 45, 11, 28];
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];
        foreach (var c in new[] { 6, 7, 8, 9 }) sheet.Column(c).Style.Alignment.WrapText = true;
        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }

    private static void WriteArchitecture(IXLWorksheet sheet, SolutionResult result)
    {
        var a = result.Architecture!;
        sheet.Cell(1, 1).Value = "Arquitetura alvo (AWS)";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(2, 1).Value = Truncate(a.Summary);
        sheet.Range(2, 1, 2, 6).Merge().Style.Alignment.WrapText = true;
        sheet.Row(2).Height = 60;

        var row = 4;
        string[] hostingHeaders = ["Projeto", "Tipo", "Hospedagem", "Por quê", "Pré-requisitos", "Alternativas"];
        for (var c = 0; c < hostingHeaders.Length; c++) sheet.Cell(row, c + 1).Value = hostingHeaders[c];
        var start = row;
        foreach (var h in a.Hosting)
        {
            row++;
            sheet.Cell(row, 1).Value = h.Project;
            sheet.Cell(row, 2).Value = ReportWriter.KindLabel(h.Kind);
            sheet.Cell(row, 3).Value = h.Primary.Display() + (h.RequiresWindows ? " (exige Windows)" : "");
            sheet.Cell(row, 4).Value = Truncate(string.Join("\n", h.Rationale));
            sheet.Cell(row, 5).Value = Truncate(string.Join("\n", h.Prerequisites));
            sheet.Cell(row, 6).Value = Truncate(string.Join("\n", h.Alternatives));
        }
        if (row > start) sheet.Range(start, 1, row, hostingHeaders.Length).CreateTable("Hospedagem");

        row += 2;
        string[] componentHeaders = ["Serviço AWS", "Papel", "Substitui", "Por quê", "Usado por", "Necessidade"];
        for (var c = 0; c < componentHeaders.Length; c++) sheet.Cell(row, c + 1).Value = componentHeaders[c];
        start = row;
        foreach (var c in a.Components)
        {
            row++;
            sheet.Cell(row, 1).Value = c.Service;
            sheet.Cell(row, 2).Value = c.Role;
            sheet.Cell(row, 3).Value = c.Replaces;
            sheet.Cell(row, 4).Value = Truncate(c.Why + (c.Notes != null ? "\n" + c.Notes : ""));
            sheet.Cell(row, 5).Value = string.Join(", ", c.UsedBy);
            sheet.Cell(row, 6).Value = c.Required ? "obrigatório" : "recomendado";
        }
        if (row > start) sheet.Range(start, 1, row, componentHeaders.Length).CreateTable("Servicos");

        row += 2;
        void Section(string title, IEnumerable<string> lines)
        {
            sheet.Cell(row, 1).Value = title;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            foreach (var line in lines)
            {
                row++;
                sheet.Cell(row, 1).Value = Truncate(line);
                sheet.Range(row, 1, row, 6).Merge().Style.Alignment.WrapText = true;
            }
            row += 2;
        }
        Section("Plano de migração", a.Phases);
        Section("Riscos", a.Risks);
        Section("Custo", a.CostNotes);

        int[] widths = [34, 28, 40, 70, 60, 60];
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];
        sheet.Columns(1, 6).Style.Alignment.WrapText = true;
        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }
}
