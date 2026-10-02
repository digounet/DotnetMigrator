using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static class CsvReport
{
    public static string Render(SolutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Projeto,Severidade,Categoria,Regra,Automatico,Titulo,Arquivo,Linha,Ocorrencias,Descricao,Sugestao");
        foreach (var item in ReportWriter.Ordered(result.AllItems))
        {
            sb.AppendLine(string.Join(",",
                Field(item.Project),
                Field(item.Severity.Display()),
                Field(item.Category.Display()),
                Field(item.RuleId),
                Field(item.AutoMigrated ? "Sim" : "Não"),
                Field(item.Title),
                Field(item.FilePath ?? ""),
                Field(item.Line?.ToString() ?? ""),
                Field(item.Occurrences.ToString()),
                Field(item.Description),
                Field(item.Suggestion)));
        }
        return sb.ToString();
    }

    public static string RenderModernization(SolutionResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Projeto,Tipo,Impacto,Esforco,Regra,Item,PorQue,Proposta,Evidencia,Ocorrencias,ServicoAWS");
        foreach (var item in ReportWriter.OrderedModernizations(result.AllModernizations))
            sb.AppendLine(string.Join(",",
                Field(item.Project), Field(item.Kind.Display()), Field(item.Impact.Display()), Field(item.Effort.Display()), Field(item.RuleId),
                Field(item.Title), Field(item.Why), Field(item.Proposal), Field(item.Evidence ?? ""), Field(item.Occurrences.ToString()), Field(item.AwsService ?? "")));
        return sb.ToString();
    }

    private static string Field(string value)
    {
        var sanitized = value.Length > 0 && "=+-@".Contains(value[0]) ? "'" + value : value;
        return $"\"{sanitized.Replace("\"", "\"\"")}\"";
    }
}
