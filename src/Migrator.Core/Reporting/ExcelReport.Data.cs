using ClosedXML.Excel;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class ExcelReport
{
    private static void WriteDataAccess(IXLWorksheet sheet, SolutionResult result)
    {
        string[] headers = ["Banco", "Tecnologia", "Tipo", "Schema", "Tabela / procedure", "Campos acessados", "Operações", "Acesso", "Projetos", "Onde", "Banco identificado"];
        for (var c = 0; c < headers.Length; c++) sheet.Cell(1, c + 1).Value = headers[c];
        var row = 1;
        foreach (var (database, technology, tables) in ReportWriter.DataAccessByDatabase(result))
            foreach (var t in tables)
            {
                row++;
                var unresolved = database.StartsWith("não identificado", StringComparison.Ordinal);
                sheet.Cell(row, 1).Value = database;
                sheet.Cell(row, 2).Value = technology;
                sheet.Cell(row, 3).Value = t.Kind.Display();
                sheet.Cell(row, 4).Value = t.Schema ?? "";
                sheet.Cell(row, 5).Value = t.Name;
                sheet.Cell(row, 6).Value = Truncate(string.Join(", ", t.Columns));
                sheet.Cell(row, 7).Value = string.Join(", ", t.Operations);
                sheet.Cell(row, 8).Value = string.Join(", ", t.Access);
                sheet.Cell(row, 9).Value = t.Project;
                sheet.Cell(row, 10).Value = Truncate(string.Join("\n", t.Locations));
                sheet.Cell(row, 11).Value = unresolved ? "Não" : "Sim";
                if (unresolved) sheet.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#FEF7E0");
            }
        sheet.Range(1, 1, Math.Max(row, 2), headers.Length).CreateTable("DadosAcessados");
        sheet.SheetView.FreezeRows(1);
        int[] widths = [26, 14, 12, 10, 30, 60, 22, 24, 30, 50, 10];
        for (var c = 0; c < widths.Length; c++) sheet.Column(c + 1).Width = widths[c];
        foreach (var c in new[] { 6, 10 }) sheet.Column(c).Style.Alignment.WrapText = true;
        sheet.Rows().Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }
}
