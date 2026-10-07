using ClosedXML.Excel;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class ExcelReport
{
    /// <summary>One sheet, one block per section of the deployment guide, so the work can be split and tracked in a spreadsheet.</summary>
    private static void WriteDeployment(IXLWorksheet sheet, DeploymentGuide g)
    {
        var row = 1;
        var envs = g.Environments;
        void Title(string text)
        {
            if (row > 1) row++;
            sheet.Cell(row, 1).Value = text;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Font.FontSize = 12;
            row++;
        }
        void Header(params string[] columns)
        {
            for (var i = 0; i < columns.Length; i++) sheet.Cell(row, i + 1).Value = columns[i];
            sheet.Range(row, 1, row, columns.Length).Style.Font.Bold = true;
            sheet.Range(row, 1, row, columns.Length).Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F3F4");
            row++;
        }
        void Row(params string?[] values)
        {
            for (var i = 0; i < values.Length; i++) sheet.Cell(row, i + 1).Value = values[i] ?? "";
            row++;
        }
        string Env(Dictionary<string, string> values, string e) => values.TryGetValue(e, out var v) ? v : "";

        Title($"Guia de implantação — {g.Target}, {g.Iac}, feature {g.Feature}");
        Title("1. O que roda onde");
        Header("Projeto", "Micro", "Tipo", "Hospedagem", "Como roda", "Template", "Stack", "Parâmetros", "Endpoint", "Health check", "Agendamento", "Exige Windows", "Pré-requisitos", "Não gerado");
        foreach (var u in g.Units) Row(u.Project, u.Micro, u.Kind, u.HostingLabel, u.Runtime, u.TemplateFile, u.Stack, u.ParametersFile, u.Endpoint, u.HealthCheck, u.Schedule, u.RequiresWindows ? "sim" : "não", string.Join("\n", u.Prerequisites), u.NotGenerated);

        Title("2. Infraestrutura: arquivos");
        Header("Arquivo", "Para quê", "Stack", "Parâmetros", "Ordem");
        foreach (var f in g.Files) Row(f.Path, f.Purpose, f.Stack, f.ParametersFile, f.DeployOrder?.ToString());
        Title("2. Infraestrutura: parâmetros por ambiente");
        Header(new[] { "Grupo", "Parâmetro", "Preencher" }.Concat(envs).Concat(["Arquivos", "Descrição"]).ToArray());
        foreach (var p in g.Parameters) Row(new[] { p.Group, p.Name, p.Placeholder ? "SIM" : "" }.Concat(envs.Select(e => Env(p.Values, e))).Concat([string.Join(", ", p.Files), p.Description]).ToArray());

        Title("3. Banco de dados");
        Header(new[] { "Banco", "Tecnologia", "Origem", "Integrated Security", "Connection strings", "Usado por", "Tabelas", "Procedures", "Engine RDS" }.Concat(envs.Select(e => "Instância " + e)).Concat(["Endpoint", "Connection string na AWS", "Notas"]).ToArray());
        foreach (var db in g.Databases) Row(new[] { db.Name, db.Provider, db.SourceServer, db.IntegratedSecurity ? "sim" : "não", string.Join(", ", db.ConnectionNames), string.Join(", ", db.UsedBy), db.Tables.ToString(), db.Procedures.ToString(), db.RdsEngine }.Concat(envs.Select(e => Env(db.InstanceClass, e))).Concat([db.Endpoint, string.Join("\n", db.ConnectionSecrets), string.Join("\n", db.Notes)]).ToArray());

        Title("4. Segredos (Secrets Manager)");
        Header("Segredo", "Conteúdo", "Usado por", "Como chega na aplicação", "Como preencher", "Criado por");
        foreach (var s in g.Secrets) Row(s.Name, s.Holds, string.Join(", ", s.UsedBy), s.DeliveredAs, s.HowToFill, s.CreatedBy);

        Title("5. Variáveis de ambiente e parâmetros da aplicação");
        Header(new[] { "Chave", "Tipo", "Variável de ambiente", "Parameter Store", "Parâmetro" }.Concat(envs).Concat(["Origem", "Usado por"]).ToArray());
        foreach (var s in g.Settings) Row(new[] { s.Key, s.Kind, s.EnvironmentVariable, s.ParameterStorePath, s.Parameter }.Concat(envs.Select(e => Env(s.Values, e))).Concat([s.Source, string.Join(", ", s.UsedBy)]).ToArray());

        Title("6. Armazenamento e filas");
        Header("Serviço", "Recurso", "Para quê", "Substitui", "Usado por", "Como a aplicação encontra", "Notas");
        foreach (var r in g.Storage.Concat(g.Queues)) Row(r.Service, r.Name, r.Purpose, r.Replaces, string.Join(", ", r.UsedBy), r.DeliveredAs, string.Join("\n", r.Notes));

        Title("7. Rede e integrações");
        Header("Tipo", "Alvo", "O que configurar", "Usado por");
        foreach (var i in g.Integrations) Row(i.Kind, i.Target, i.Action, string.Join(", ", i.UsedBy));

        Title("8. Esteira");
        Header("Arquivo", "Chave", "Valor gerado", "O que fazer");
        foreach (var p in g.Pipeline) Row(p.File, p.Key, p.Value, p.Action);

        Title("9. Checklist de implantação");
        Header("Fase", "Passo", "Detalhe", "Feito");
        foreach (var c in g.Checklist) Row(c.Phase, c.Step, c.Detail, "");

        sheet.Columns().AdjustToContents(1, row, 10, 70);
        sheet.Style.Alignment.WrapText = true;
        sheet.Style.Alignment.Vertical = XLAlignmentVerticalValues.Top;
    }
}
