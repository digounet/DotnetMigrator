using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class MarkdownReport
{
    /// <summary>
    /// The deployment guide as Markdown. <paramref name="level"/> is the heading level of the section title (2 inside the
    /// report, 1 in the repository README); <paramref name="number"/> prefixes the subsections ("2" → "2.1", null → "1.").
    /// </summary>
    public static string RenderDeploymentGuide(SolutionResult result, int level, string? number = null)
    {
        var sb = new StringBuilder();
        RenderDeployment(sb, result, level, number);
        return sb.ToString();
    }

    private static void RenderDeployment(StringBuilder sb, SolutionResult result, int level, string? number)
    {
        var g = result.Deployment;
        if (g == null) return;
        var h = new string('#', level);
        var hh = new string('#', level + 1);
        var n = 0;
        string Sub(string title) => $"{hh} {(number != null ? $"{number}.{++n}" : $"{++n}.")} {title}";
        var envs = g.Environments;
        string EnvHeader() => string.Join(" | ", envs);
        string EnvCells(Dictionary<string, string> values) => string.Join(" | ", envs.Select(e => values.TryGetValue(e, out var v) && v.Length > 0 ? Cell(v) : "—"));

        sb.AppendLine($"{h} {(number != null ? number + ". " : "")}Guia de implantação na AWS");
        sb.AppendLine();
        sb.AppendLine($"Tudo o que precisa ser configurado para **{Cell(result.SolutionName)}** rodar na AWS: destino **{g.Target}**, infraestrutura em **{g.Iac}** (feature `{g.Feature}`, ambientes {string.Join(", ", envs)}). " +
                      (g.InfrastructureWritten ? "Os arquivos citados foram gerados na saída (`infra/`, `.iupipes.yml`, `tests/`, `_secrets/`)." : "Os arquivos citados são gerados pelo `migrate`; esta execução só os descreve.") +
                      " Valores marcados com ⚠ são exemplos e precisam ser substituídos antes do primeiro deploy.");
        sb.AppendLine();

        // ---- 1. units
        sb.AppendLine(Sub("O que roda onde"));
        sb.AppendLine();
        sb.AppendLine("| Projeto | Tipo | Hospedagem | Como roda | Template → stack | Endpoint / health / agendamento |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var u in g.Units)
        {
            var endpoint = string.Join("<br>", new[] { u.Endpoint, u.HealthCheck, u.Schedule }.Where(x => x != null).Select(x => Cell(x!)));
            var template = u.TemplateFile != null ? $"`{u.TemplateFile}` → `{u.Stack}` (`{u.ParametersFile}`)" : u.NotGenerated != null ? $"não gerado: {Cell(u.NotGenerated)}" : "—";
            sb.AppendLine($"| **{Cell(u.Project)}** (`{u.Micro}`) | {Cell(u.Kind)} | {Cell(u.HostingLabel)}{(u.RequiresWindows ? " ⚠ exige Windows" : "")} | {Cell(u.Runtime)} | {template} | {(endpoint.Length > 0 ? endpoint : "—")} |");
        }
        sb.AppendLine();
        if (g.Units.Any(u => u.Prerequisites.Count > 0))
        {
            sb.AppendLine("**Antes do primeiro deploy**");
            sb.AppendLine();
            foreach (var u in g.Units.Where(u => u.Prerequisites.Count > 0))
            {
                sb.AppendLine($"- **{Cell(u.Project)}**");
                foreach (var pre in u.Prerequisites) sb.AppendLine($"  - {Inline(pre)}");
            }
            sb.AppendLine();
        }

        // ---- 2. infra files and parameters
        sb.AppendLine(Sub("Infraestrutura: arquivos, ordem de deploy e parâmetros"));
        sb.AppendLine();
        sb.AppendLine("| Arquivo | Para quê | Stack | Parâmetros | Ordem |");
        sb.AppendLine("|---|---|---|---|---:|");
        foreach (var f in g.Files)
            sb.AppendLine($"| `{f.Path}` | {Cell(f.Purpose)} | {(f.Stack != null ? $"`{f.Stack}`" : "—")} | {(f.ParametersFile != null ? $"`{f.ParametersFile}`" : "—")} | {(f.DeployOrder?.ToString() ?? "—")} |");
        sb.AppendLine();
        if (g.Parameters.Count > 0)
        {
            sb.AppendLine($"**Parâmetros por ambiente** ({g.Parameters.Count(p => p.Placeholder)} a preencher, marcados com ⚠):");
            sb.AppendLine();
            sb.AppendLine($"| Grupo | Parâmetro | {EnvHeader()} | Arquivos | Descrição |");
            sb.AppendLine($"|---|---|{string.Concat(envs.Select(_ => "---|"))}---|---|");
            foreach (var p in g.Parameters)
                sb.AppendLine($"| {Cell(p.Group)} | {(p.Placeholder ? "⚠ " : "")}`{p.Name}` | {EnvCells(p.Values)} | {Cell(ReportWriter.FilesLabel(g, p))} | {Cell(p.Description)} |");
            sb.AppendLine();
        }

        // ---- 3. databases
        sb.AppendLine(Sub("Banco de dados"));
        sb.AppendLine();
        if (g.Databases.Count == 0) sb.AppendLine("Nenhuma connection string encontrada: a aplicação não acessa banco de dados (ou acessa por caminhos que a análise não reconhece).");
        foreach (var db in g.Databases)
        {
            sb.AppendLine($"**{Cell(db.Name)}** ({Cell(db.Provider)}) — usado por {Cell(string.Join(", ", db.UsedBy))}; {db.Tables} tabela(s) e {db.Procedures} procedure(s) acessadas (detalhes na seção Dados acessados).");
            sb.AppendLine();
            sb.AppendLine("| Item | Valor |");
            sb.AppendLine("|---|---|");
            sb.AppendLine($"| Connection string(s) | {Cell(string.Join(", ", db.ConnectionNames))} |");
            sb.AppendLine($"| Origem | {Cell(db.SourceServer ?? "—")}{(db.IntegratedSecurity ? " (Integrated Security)" : "")} |");
            if (db.RdsEngine != null) sb.AppendLine($"| RDS | engine `{db.RdsEngine}`; instância {string.Join(" / ", envs.Select(e => $"{e}: `{(db.InstanceClass.GetValueOrDefault(e) ?? "")}`"))} |");
            if (db.Endpoint != null) sb.AppendLine($"| Endpoint | {Cell(db.Endpoint)} |");
            sb.AppendLine($"| Connection string na AWS | {(db.ConnectionSecrets.Count > 0 ? string.Join("<br>", db.ConnectionSecrets.Select(Cell)) : "—")} |");
            foreach (var note in db.Notes) sb.AppendLine($"| Nota | {Cell(note)} |");
            sb.AppendLine();
        }

        // ---- 4. secrets
        sb.AppendLine(Sub("Segredos (Secrets Manager)"));
        sb.AppendLine();
        if (g.Secrets.Count == 0) sb.AppendLine("Nenhuma credencial encontrada no código ou nos configs.");
        else
        {
            var createdBy = g.Secrets.Select(s => s.CreatedBy).FirstOrDefault(c => c != null);
            sb.AppendLine($"Nenhum valor passa por template, parâmetro ou repositório: os nomes são criados por `{createdBy ?? "_secrets/"}` e os valores entram pelos scripts de `_secrets/` ou manualmente.");
            sb.AppendLine();
            sb.AppendLine("| Segredo | Conteúdo | Usado por | Como chega na aplicação | Como preencher |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var s in g.Secrets)
                sb.AppendLine($"| `{s.Name}` | {Cell(s.Holds)} | {Cell(string.Join(", ", s.UsedBy))} | {Cell(s.DeliveredAs ?? "—")} | {Cell(s.HowToFill)} |");
        }
        sb.AppendLine();

        // ---- 5. settings
        sb.AppendLine(Sub("Variáveis de ambiente e parâmetros da aplicação"));
        sb.AppendLine();
        if (g.Settings.Count == 0) sb.AppendLine("Nenhuma URL/e-mail fixo foi externalizado e nenhuma variável de ambiente é injetada além das do runtime.");
        else
        {
            var framework = g.Settings.Any(s => s.ParameterStorePath != null);
            sb.AppendLine(framework
                ? "Os valores chegam às instâncias pelo SSM Parameter Store (`/<feature>/<env>/...`), gravados no `appSettings` do config pelo `after-install.ps1` do CodeDeploy; a aplicação continua lendo `ConfigurationManager.AppSettings[...]`."
                : "Os valores chegam como variáveis de ambiente da task definition/função (`Secao__Chave`), que o `IConfiguration` lê sem código extra.");
            sb.AppendLine();
            sb.AppendLine($"| Chave → entrega → parâmetro | Tipo | {EnvHeader()} | Origem | Usado por |");
            sb.AppendLine($"|---|---|{string.Concat(envs.Select(_ => "---|"))}---|---|");
            foreach (var s in g.Settings)
                sb.AppendLine($"| `{s.Key}`<br>`{s.EnvironmentVariable ?? s.ParameterStorePath}`{(s.Parameter != s.Key && s.Parameter != s.EnvironmentVariable ? $"<br>parâmetro `{s.Parameter}`" : "")} | {Cell(s.Kind)} | {EnvCells(s.Values)} | {Cell(s.Source)} | {Cell(string.Join(", ", s.UsedBy))} |");
        }
        sb.AppendLine();

        // ---- 6. storage and queues
        sb.AppendLine(Sub("Armazenamento e filas"));
        sb.AppendLine();
        if (g.Storage.Count == 0 && g.Queues.Count == 0) sb.AppendLine("A aplicação não usa arquivos nem filas que precisem de recursos próprios.");
        else
        {
            sb.AppendLine("| Serviço | Recurso | Para quê | Substitui | Usado por | Como a aplicação encontra | Notas |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in g.Storage.Concat(g.Queues))
                sb.AppendLine($"| {Cell(r.Service)} | `{r.Name}` | {Cell(r.Purpose)} | {Cell(r.Replaces ?? "—")} | {Cell(string.Join(", ", r.UsedBy))} | {Cell(r.DeliveredAs ?? "—")} | {Cells(r.Notes)} |");
        }
        sb.AppendLine();

        // ---- 7. network and integrations
        sb.AppendLine(Sub("Rede e integrações"));
        sb.AppendLine();
        if (g.Integrations.Count == 0) sb.AppendLine("Nenhum host on-premises nem serviço externo além dos já cobertos acima.");
        else
        {
            sb.AppendLine("| Tipo | Alvo | O que configurar | Usado por |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var i in g.Integrations) sb.AppendLine($"| {Cell(i.Kind)} | {Cell(i.Target)} | {Cell(i.Action)} | {Cell(string.Join(", ", i.UsedBy))} |");
        }
        sb.AppendLine();

        // ---- 8. pipeline
        sb.AppendLine(Sub("Esteira"));
        sb.AppendLine();
        sb.AppendLine("| Arquivo | Chave | Valor gerado | O que fazer |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var p in g.Pipeline) sb.AppendLine($"| `{p.File}` | `{p.Key}` | `{Cell(p.Value)}` | {Cell(p.Action)} |");
        sb.AppendLine();

        // ---- 9. checklist
        sb.AppendLine(Sub("Checklist de implantação"));
        sb.AppendLine();
        foreach (var phase in g.Checklist.GroupBy(c => c.Phase))
        {
            sb.AppendLine($"**{Cell(phase.Key)}**");
            sb.AppendLine();
            foreach (var step in phase) sb.AppendLine($"- [ ] {Cell(step.Step)}{(step.Detail != null ? $" — {Cell(step.Detail)}" : "")}");
            sb.AppendLine();
        }
    }
}
