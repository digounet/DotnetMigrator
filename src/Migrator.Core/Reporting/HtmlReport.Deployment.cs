using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Reporting;

public static partial class HtmlReport
{
    private static void RenderDeployment(StringBuilder sb, SolutionResult result)
    {
        var g = result.Deployment!;
        var envs = g.Environments;
        string EnvHeaders() => string.Concat(envs.Select(e => $"<th>{E(e)}</th>"));
        string EnvCells(Dictionary<string, string> values) => string.Concat(envs.Select(e => $"<td class=\"env\">{(values.TryGetValue(e, out var v) && v.Length > 0 ? $"<code>{E(v)}</code>" : "<span class=\"opt\">—</span>")}</td>"));
        string Code(string? text) => text == null ? "<span class=\"opt\">—</span>" : $"<code>{E(text)}</code>";

        sb.Append("<div class=\"panel\" id=\"implantacao\"><h2>2. Guia de implantação na AWS</h2><div class=\"body\">");
        sb.Append($"<p class=\"lead\">Tudo o que precisa ser configurado para <strong>{E(result.SolutionName)}</strong> rodar na AWS: destino <strong>{E(g.Target)}</strong>, infraestrutura em <strong>{E(g.Iac)}</strong> (feature <code>{E(g.Feature)}</code>, ambientes {E(string.Join(", ", envs))}). ");
        sb.Append(g.InfrastructureWritten ? "Os arquivos citados foram gerados na saída (<code>infra/</code>, <code>.iupipes.yml</code>, <code>tests/</code>, <code>_secrets/</code>)." : "Os arquivos citados são gerados pelo <code>migrate</code>; esta execução só os descreve.");
        sb.Append(" Valores marcados com <span class=\"badge warning\">preencher</span> são exemplos e precisam ser substituídos antes do primeiro deploy.</p>");
        sb.Append("<p class=\"toc\">");
        foreach (var (anchor, title) in new[] { ("roda", "2.1 O que roda onde"), ("infra", "2.2 Infraestrutura"), ("banco", "2.3 Banco de dados"), ("segredos", "2.4 Segredos"), ("variaveis", "2.5 Variáveis e parâmetros"), ("storage", "2.6 Armazenamento e filas"), ("rede", "2.7 Rede e integrações"), ("esteira", "2.8 Esteira"), ("checklist", "2.9 Checklist") })
            sb.Append($"<a href=\"#d-{anchor}\">{E(title)}</a>");
        sb.Append("</p>");

        // 2.1
        sb.Append("<h3 id=\"d-roda\">2.1 O que roda onde</h3>");
        sb.Append("<table><thead><tr><th>Projeto</th><th>Tipo</th><th>Hospedagem</th><th>Como roda</th><th>Template → stack</th><th>Endpoint / health / agendamento</th></tr></thead><tbody>");
        foreach (var u in g.Units)
        {
            var endpoint = string.Concat(new[] { u.Endpoint, u.HealthCheck, u.Schedule }.Where(x => x != null).Select(x => $"<div>{E(x)}</div>"));
            var template = u.TemplateFile != null ? $"<code>{E(u.TemplateFile)}</code> → <code>{E(u.Stack)}</code><br><span class=\"opt\">{E(u.ParametersFile)}</span>" : u.NotGenerated != null ? $"<span class=\"badge warning\">não gerado</span> {E(u.NotGenerated)}" : "<span class=\"opt\">—</span>";
            sb.Append($"<tr><td><strong>{E(u.Project)}</strong><br><code>{E(u.Micro)}</code></td><td>{E(u.Kind)}</td><td><strong>{E(u.HostingLabel)}</strong>{(u.RequiresWindows ? "<br><span class=\"badge warning\">exige Windows</span>" : "")}</td>")
              .Append($"<td class=\"wrap\">{E(u.Runtime)}</td><td class=\"wrap\">{template}</td><td class=\"wrap\">{(endpoint.Length > 0 ? endpoint : "<span class=\"opt\">—</span>")}</td></tr>");
        }
        sb.Append("</tbody></table>");
        if (g.Units.Any(u => u.Prerequisites.Count > 0))
        {
            sb.Append("<h4>Antes do primeiro deploy</h4>");
            foreach (var u in g.Units.Where(u => u.Prerequisites.Count > 0))
                sb.Append($"<details class=\"prereq\"><summary><strong>{E(u.Project)}</strong> <span class=\"opt\">{u.Prerequisites.Count} item(ns)</span></summary>{List(u.Prerequisites)}</details>");
        }

        // 2.2
        sb.Append("<h3 id=\"d-infra\">2.2 Infraestrutura: arquivos, ordem de deploy e parâmetros</h3>");
        sb.Append("<table><thead><tr><th>Arquivo</th><th>Para quê</th><th>Stack</th><th>Parâmetros</th><th class=\"num\">Ordem</th></tr></thead><tbody>");
        foreach (var f in g.Files)
            sb.Append($"<tr><td class=\"name\"><code>{E(f.Path)}</code></td><td class=\"wrap\">{E(f.Purpose)}</td><td>{Code(f.Stack)}</td><td>{Code(f.ParametersFile)}</td><td class=\"num\">{(f.DeployOrder?.ToString() ?? "—")}</td></tr>");
        sb.Append("</tbody></table>");
        if (g.Parameters.Count > 0)
        {
            sb.Append($"<h4>Parâmetros por ambiente <span class=\"opt\">({g.Parameters.Count(p => p.Placeholder)} a preencher)</span></h4>");
            sb.Append($"<table><thead><tr><th>Grupo</th><th>Parâmetro</th>{EnvHeaders()}<th>Arquivos</th><th>Descrição</th></tr></thead><tbody>");
            foreach (var p in g.Parameters)
                sb.Append($"<tr><td>{E(p.Group)}</td><td class=\"name\">{(p.Placeholder ? "<span class=\"badge warning\">preencher</span> " : "")}<code>{E(p.Name)}</code></td>{EnvCells(p.Values)}<td class=\"files\">{E(ReportWriter.FilesLabel(g, p))}</td><td class=\"wrap\">{E(p.Description)}</td></tr>");
            sb.Append("</tbody></table>");
        }

        // 2.3
        sb.Append("<h3 id=\"d-banco\">2.3 Banco de dados</h3>");
        if (g.Databases.Count == 0) sb.Append("<p class=\"lead\">Nenhuma connection string encontrada: a aplicação não acessa banco de dados (ou acessa por caminhos que a análise não reconhece).</p>");
        foreach (var db in g.Databases)
        {
            sb.Append($"<h4>{E(db.Name)} <span class=\"badge cloud\">{E(db.Provider)}</span> <span class=\"opt\">usado por {E(string.Join(", ", db.UsedBy))} · {db.Tables} tabela(s), {db.Procedures} procedure(s) (seção 4)</span></h4>");
            sb.Append("<table><tbody>");
            sb.Append($"<tr><th>Connection string(s)</th><td>{E(string.Join(", ", db.ConnectionNames))}</td></tr>");
            sb.Append($"<tr><th>Origem</th><td>{E(db.SourceServer ?? "—")}{(db.IntegratedSecurity ? " <span class=\"badge warning\">Integrated Security</span>" : "")}</td></tr>");
            if (db.RdsEngine != null) sb.Append($"<tr><th>RDS</th><td>engine <code>{E(db.RdsEngine)}</code>; instância {string.Join(" · ", envs.Select(e => $"{E(e)}: <code>{E(db.InstanceClass.GetValueOrDefault(e) ?? "")}</code>"))}</td></tr>");
            if (db.Endpoint != null) sb.Append($"<tr><th>Endpoint</th><td>{E(db.Endpoint)}</td></tr>");
            sb.Append($"<tr><th>Connection string na AWS</th><td>{List(db.ConnectionSecrets)}</td></tr>");
            sb.Append($"<tr><th>Notas</th><td>{List(db.Notes)}</td></tr>");
            sb.Append("</tbody></table>");
        }

        // 2.4
        sb.Append("<h3 id=\"d-segredos\">2.4 Segredos (Secrets Manager)</h3>");
        if (g.Secrets.Count == 0) sb.Append("<p class=\"lead\">Nenhuma credencial encontrada no código ou nos configs.</p>");
        else
        {
            var createdBy = g.Secrets.Select(s => s.CreatedBy).FirstOrDefault(c => c != null);
            sb.Append($"<p class=\"lead\">Nenhum valor passa por template, parâmetro ou repositório: os nomes são criados por <code>{E(createdBy ?? "_secrets/")}</code> e os valores entram pelos scripts de <code>_secrets/</code> ou manualmente.</p>");
            sb.Append("<table><thead><tr><th>Segredo</th><th>Conteúdo</th><th>Usado por</th><th>Como chega na aplicação</th><th>Como preencher</th></tr></thead><tbody>");
            foreach (var s in g.Secrets)
                sb.Append($"<tr><td class=\"res\"><code>{E(s.Name)}</code></td><td class=\"wrap\">{E(s.Holds)}</td><td>{E(string.Join(", ", s.UsedBy))}</td><td class=\"wrap\">{E(s.DeliveredAs ?? "—")}</td><td class=\"wrap\">{E(s.HowToFill)}</td></tr>");
            sb.Append("</tbody></table>");
        }

        // 2.5
        sb.Append("<h3 id=\"d-variaveis\">2.5 Variáveis de ambiente e parâmetros da aplicação</h3>");
        if (g.Settings.Count == 0) sb.Append("<p class=\"lead\">Nenhuma URL/e-mail fixo foi externalizado e nenhuma variável de ambiente é injetada além das do runtime.</p>");
        else
        {
            var framework = g.Settings.Any(s => s.ParameterStorePath != null);
            sb.Append(framework
                ? "<p class=\"lead\">Os valores chegam às instâncias pelo SSM Parameter Store (<code>/&lt;feature&gt;/&lt;env&gt;/...</code>), gravados no <code>appSettings</code> do config pelo <code>after-install.ps1</code> do CodeDeploy; a aplicação continua lendo <code>ConfigurationManager.AppSettings[...]</code>.</p>"
                : "<p class=\"lead\">Os valores chegam como variáveis de ambiente da task definition/função (<code>Secao__Chave</code>), que o <code>IConfiguration</code> lê sem código extra.</p>");
            sb.Append($"<table><thead><tr><th>Chave → entrega → parâmetro</th><th>Tipo</th>{EnvHeaders()}<th>Origem</th><th>Usado por</th></tr></thead><tbody>");
            foreach (var s in g.Settings)
                sb.Append($"<tr><td class=\"res\"><code>{E(s.Key)}</code><span class=\"sub\">{E(s.EnvironmentVariable ?? s.ParameterStorePath ?? "")}</span>{(s.Parameter != s.Key && s.Parameter != s.EnvironmentVariable ? $"<span class=\"sub\">parâmetro {E(s.Parameter)}</span>" : "")}</td><td>{E(s.Kind)}</td>{EnvCells(s.Values)}<td class=\"wrap\">{E(s.Source)}</td><td>{E(string.Join(", ", s.UsedBy))}</td></tr>");
            sb.Append("</tbody></table>");
        }

        // 2.6
        sb.Append("<h3 id=\"d-storage\">2.6 Armazenamento e filas</h3>");
        if (g.Storage.Count == 0 && g.Queues.Count == 0) sb.Append("<p class=\"lead\">A aplicação não usa arquivos nem filas que precisem de recursos próprios.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Serviço</th><th>Recurso</th><th>Para quê</th><th>Substitui</th><th>Usado por</th><th>Como a aplicação encontra</th><th>Notas</th></tr></thead><tbody>");
            foreach (var r in g.Storage.Concat(g.Queues))
                sb.Append($"<tr><td>{E(r.Service)}</td><td class=\"res\"><code>{E(r.Name)}</code></td><td class=\"wrap\">{E(r.Purpose)}</td><td class=\"wrap\">{E(r.Replaces ?? "—")}</td><td>{E(string.Join(", ", r.UsedBy))}</td><td class=\"wrap\">{E(r.DeliveredAs ?? "—")}</td><td class=\"wrap\">{List(r.Notes)}</td></tr>");
            sb.Append("</tbody></table>");
        }

        // 2.7
        sb.Append("<h3 id=\"d-rede\">2.7 Rede e integrações</h3>");
        if (g.Integrations.Count == 0) sb.Append("<p class=\"lead\">Nenhum host on-premises nem serviço externo além dos já cobertos acima.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Tipo</th><th>Alvo</th><th>O que configurar</th><th>Usado por</th></tr></thead><tbody>");
            foreach (var i in g.Integrations) sb.Append($"<tr><td>{E(i.Kind)}</td><td class=\"wrap\">{E(i.Target)}</td><td class=\"wrap\">{E(i.Action)}</td><td>{E(string.Join(", ", i.UsedBy))}</td></tr>");
            sb.Append("</tbody></table>");
        }

        // 2.8
        sb.Append("<h3 id=\"d-esteira\">2.8 Esteira</h3>");
        sb.Append("<table><thead><tr><th>Arquivo</th><th>Chave</th><th>Valor gerado</th><th>O que fazer</th></tr></thead><tbody>");
        foreach (var p in g.Pipeline) sb.Append($"<tr><td class=\"name\"><code>{E(p.File)}</code></td><td><code>{E(p.Key)}</code></td><td><code>{E(p.Value)}</code></td><td class=\"wrap\">{E(p.Action)}</td></tr>");
        sb.Append("</tbody></table>");

        // 2.9
        sb.Append("<h3 id=\"d-checklist\">2.9 Checklist de implantação</h3>");
        foreach (var phase in g.Checklist.GroupBy(c => c.Phase))
        {
            sb.Append($"<h4>{E(phase.Key)}</h4><ul class=\"checklist\">");
            foreach (var step in phase) sb.Append($"<li><label><input type=\"checkbox\"> {E(step.Step)}</label>{(step.Detail != null ? $"<span class=\"opt\"> — {E(step.Detail)}</span>" : "")}</li>");
            sb.Append("</ul>");
        }
        sb.Append("</div></div>");
    }
}
