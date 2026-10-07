using System.Text;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// Lift-and-shift variant used by <c>--target framework</c>: the code stays on .NET Framework 4.8.1, therefore every deployable
/// runs on Windows (EC2 in Auto Scaling groups). The recommendation also computes what the project would get after a
/// .NET 10 migration, so the report shows both answers side by side instead of two contradicting ones.
/// </summary>
public static partial class AwsArchitect
{
    public static HostingRecommendation Recommend(ProjectInfo project, ApplicationProfile profile, MigrationTarget target)
    {
        var modern = Recommend(project, profile);
        if (target == MigrationTarget.Net10) return modern;
        if (modern.Primary is AwsHosting.NotDeployable or AwsHosting.Desktop) return modern;

        var rec = new HostingRecommendation { Project = project.Name, Kind = project.Kind, Primary = AwsHosting.Ec2Windows };
        rec.HardWindowsDependencies.AddRange(modern.HardWindowsDependencies);
        rec.SoftWindowsDependencies.AddRange(modern.SoftWindowsDependencies);
        var isWeb = project.Kind == ProjectKind.Web;

        rec.Rationale.Add(isWeb
            ? "O código permanece em .NET Framework 4.8.1 (--target framework): só roda em Windows com IIS. EC2 Windows Server 2022 em Auto Scaling group atrás de um Application Load Balancer, AMI padronizada (EC2 Image Builder) e deploy pelo CodeDeploy."
            : project.Kind == ProjectKind.WindowsService
                ? "O código permanece em .NET Framework 4.8.1 (--target framework): Windows Service instalado em uma instância EC2 Windows (Auto Scaling group de tamanho 1 para auto-recuperação), deploy pelo CodeDeploy."
                : "O código permanece em .NET Framework 4.8.1 (--target framework): console agendado pelo Agendador de Tarefas do Windows em uma instância EC2 Windows (Auto Scaling group de tamanho 1), deploy pelo CodeDeploy.");
        if (profile.Has(Signal.WebForms) && isWeb) rec.Rationale.Add("Web Forms roda no IIS sem alteração; a reescrita só é necessária na migração futura para .NET 10.");
        if (rec.HardWindowsDependencies.Count > 0) rec.Rationale.Add("Dependências Windows (" + string.Join("; ", rec.HardWindowsDependencies) + ") continuam atendidas na instância.");
        rec.Rationale.Add($"Após migrar o código para .NET 10 a recomendação passa a ser {modern.Primary.Display()}: {modern.Rationale.FirstOrDefault() ?? ""}");

        rec.Alternatives.Add(isWeb
            ? "ECS com containers Windows (imagem mcr.microsoft.com/dotnet/framework/aspnet:4.8.1): deploy por imagem e o mesmo cluster das demais aplicações, mas imagens de 5-10 GB e sem Fargate Spot."
            : "ECS com containers Windows (imagem mcr.microsoft.com/dotnet/framework/runtime:4.8.1) com EventBridge Scheduler: deploy por imagem; exige adaptar o serviço para rodar como processo de console.");
        rec.Alternatives.Add($"Migrar o código para .NET 10 (rode o Migrator sem --target framework): {modern.Primary.Display()}" + (modern.Alternatives.Count > 0 ? "; alternativas nessa trilha: " + string.Join(" / ", modern.Alternatives.Take(2)) : ""));

        if (isWeb)
        {
            rec.Prerequisites.Add("Endpoint de health check para o ALB (página/action que responda 200 sem autenticação, ex.: /health.aspx ou /health); informe o caminho no parâmetro HealthCheckPath da stack.");
            if (profile.HasAny(Signal.FormsAuth, Signal.MachineKey) || profile.ViewCount > 0 || profile.Has(Signal.WebForms))
                rec.Prerequisites.Add("machineKey explícita no web.config, igual em todas as instâncias (Forms Authentication/ViewState com mais de uma instância atrás do ALB).");
            if (profile.Has(Signal.InProcSession))
                rec.Prerequisites.Add("Sessão InProc não é compartilhada entre instâncias: habilite stickiness no target group (menos resiliente) ou mova a sessão para SQL Server (aspnet_regsql) / Redis (provider).");
        }
        if (profile.Has(Signal.UncPaths))
            rec.Prerequisites.Add("Compartilhamentos \\\\servidor\\pasta → Amazon FSx for Windows File Server (SMB nativo, exige Active Directory) ou AWS Storage Gateway (File Gateway: SMB sobre S3), sem mudar o código; copie os dados com robocopy/DataSync.");
        if (profile.HasAny(Signal.WindowsPaths, Signal.AppDataFolder, Signal.FileSystemWrites, Signal.FileUploads) && !profile.Has(Signal.UncPaths))
            rec.Prerequisites.Add("Pastas locais (C:\\..., App_Data) ficam no disco EBS da instância e se perdem na troca por Auto Scaling: aponte para FSx/File Gateway ou aceite instância única.");
        if (profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode) || profile.Databases.Any(d => !d.IntegratedSecurity))
            rec.Prerequisites.Add("Senhas fora do repositório: o script after-install.ps1 do CodeDeploy lê o segredo <app>/<projeto>/config no Secrets Manager e grava no web.config/app.config da instância; rotacione as credenciais que estavam em texto claro.");
        if (profile.Databases.Any(d => d.IntegratedSecurity && !(d.Server ?? "").Contains("localdb", StringComparison.OrdinalIgnoreCase)))
            rec.Prerequisites.Add("Integrated Security exige instâncias ingressadas no domínio (AWS Managed Microsoft AD ou AD Connector + VPN) e RDS com autenticação Windows; alternativa simples: autenticação SQL + Secrets Manager.");
        if (profile.HasAny(Signal.WindowsAuth, Signal.ActiveDirectory))
            rec.Prerequisites.Add("Autenticação Windows/AD: ingressar as instâncias no domínio (AWS Managed Microsoft AD com trust, ou AD Connector) e manter a VPN com os controladores on-premises.");
        if (profile.Has(Signal.MailboxReading) && profile.Get(Signal.MailboxReading)!.Details.Any(d => d.Contains("Exchange", StringComparison.OrdinalIgnoreCase)))
            rec.Prerequisites.Add("EWS está sendo desligado no Exchange Online: a leitura da caixa postal deve migrar para Microsoft Graph (o SDK roda no .NET Framework 4.8.1); se o Exchange for on-premises, liberar o acesso pela VPN.");
        if (profile.Has(Signal.Msmq)) rec.Prerequisites.Add("MSMQ: instalar o recurso na AMI (user data gerado); filas privadas ficam no disco da instância, então não escale horizontalmente sem mover para SQS.");
        if (profile.Has(Signal.Smtp)) rec.Prerequisites.Add("SMTP interno → Amazon SES (endpoint SMTP na porta 587, credenciais SMTP no Secrets Manager) ou relay pela VPN.");
        if (profile.HasAny(Signal.Scheduler, Signal.TimerLoop) && project.Kind == ProjectKind.Console)
            rec.Prerequisites.Add("Agendamento: o after-install.ps1 registra a tarefa no Agendador de Tarefas (ajuste o intervalo no script) ou dispare via EventBridge → SSM Run Command.");
        if (profile.HasAny(Signal.FileLogging, Signal.EventLog)) rec.Prerequisites.Add("Logs: CloudWatch agent (instalado no user data) coleta Event Log e arquivos de log para o CloudWatch Logs.");
        if (profile.Has(Signal.DateTimeNow) || profile.Culture != null) rec.Prerequisites.Add("Fuso horário da instância definido no user data (tzutil) para manter DateTime.Now como on-premises.");
        if (profile.Has(Signal.WcfHost) || profile.Has(Signal.Asmx)) rec.Prerequisites.Add("Serviços WCF/ASMX hospedados continuam no IIS; exponha-os pelo mesmo ALB (regra por caminho) e mantenha os bindings HTTP.");
        return rec;
    }

    private static ArchitectureProposal ProposeLiftAndShift(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> projects)
    {
        var proposal = new ArchitectureProposal();
        var components = new Dictionary<string, AwsComponent>(StringComparer.Ordinal);
        AwsComponent Component(string id, string service, string role, string replaces, string why, bool required = true, string? notes = null)
        {
            if (!components.TryGetValue(id, out var c))
                components[id] = c = new AwsComponent { Id = id, Service = service, Role = role, Replaces = replaces, Why = why, Required = required, Notes = notes };
            else if (!string.IsNullOrEmpty(replaces) && !c.Replaces.Contains(replaces, StringComparison.OrdinalIgnoreCase))
                c.Replaces = string.IsNullOrEmpty(c.Replaces) ? replaces : c.Replaces + "; " + replaces;
            return c;
        }

        var allProjects = projects;
        var deployables = projects.Where(p => p.Result.Hosting is { Primary: not (AwsHosting.NotDeployable or AwsHosting.Desktop) }).ToList();
        projects = deployables.Count > 0 ? deployables : projects;
        var webs = deployables.Where(p => p.Result.Project.Kind == ProjectKind.Web).ToList();
        var workers = deployables.Where(p => p.Result.Project.Kind != ProjectKind.Web).ToList();
        foreach (var (pr, _) in allProjects) proposal.Hosting.Add(pr.Hosting!);

        if (deployables.Count > 0)
        {
            var ec2 = Component("ec2", "Amazon EC2 (Windows Server 2022) + Auto Scaling", "Servidores para IIS, Windows Services e consoles agendados (.NET Framework 4.8.1)", "servidores Windows on-premises",
                "O código em .NET Framework roda sem alteração; o Auto Scaling group recria a instância em caso de falha e, para a web, escala por CPU. AMI padronizada com IIS/.NET 4.8.1/agentes (EC2 Image Builder) ou user data gerado.",
                notes: "Instâncias em subnets privadas, acesso por Session Manager (sem RDP exposto), patching pelo Patch Manager. Licença Windows inclusa no preço da instância.");
            foreach (var (pr, _) in deployables) ec2.UsedBy.Add(pr.Project.Name);
            var cd = Component("cicd", "AWS CodeDeploy + GitHub Actions (runner Windows com MSBuild)", "Build com MSBuild, pacote no S3 e deploy in-place nas instâncias (para IIS site, serviço ou tarefa agendada)", "publicação manual / MSDeploy / copiar e colar",
                "Pipeline padronizado para todas as aplicações .NET Framework do portfólio; rollback por deployment anterior; os scripts PowerShell gerados (infra/codedeploy/) configuram IIS, serviço e segredos na instância.");
            foreach (var (pr, _) in deployables) cd.UsedBy.Add(pr.Project.Name);
            var ssm = Component("ssm", "AWS Systems Manager (Session Manager, Patch Manager, Parameter Store)", "Acesso administrativo sem RDP, patching automático e parâmetros por ambiente", "RDP / WSUS / configurações por servidor",
                "Sem portas abertas para administração; janelas de patch gerenciadas; o CloudWatch agent é instalado e configurado pelo SSM.");
            foreach (var (pr, _) in deployables) ssm.UsedBy.Add(pr.Project.Name);
            var cw = Component("cloudwatch", "Amazon CloudWatch (agent, Logs, Alarms)", "Event Log, arquivos de log, métricas e alarmes", "Event Viewer / arquivos de log no servidor",
                "O agent envia Event Log de aplicação e arquivos de log para o CloudWatch Logs; alarmes de CPU, 5xx do ALB e instâncias sem saúde notificam por SNS.", notes: "Defina retenção dos log groups.");
            foreach (var (pr, _) in deployables) cw.UsedBy.Add(pr.Project.Name);
            Component("backup", "AWS Backup", "Backups de EBS, FSx e RDS por política", "backup dos servidores", "Política única de retenção para discos, compartilhamentos e banco; restauração pontual.", required: false);
        }
        if (webs.Count > 0)
        {
            var alb = Component("alb", "Application Load Balancer + AWS Certificate Manager", "Entrada HTTP/HTTPS, TLS, health checks e roteamento por host", "IIS bindings / certificados no servidor",
                "Termina TLS com certificados do ACM, distribui entre instâncias e tira do rodízio as que falham no health check. Um ALB serve várias aplicações por host header.",
                notes: "Habilite access logs no S3 e, para aplicações públicas, AWS WAF. Sticky sessions só se a sessão InProc não for movida.");
            foreach (var (pr, _) in webs) alb.UsedBy.Add(pr.Project.Name);
            Component("route53", "Amazon Route 53", "DNS", "DNS interno / registros apontando para servidores", "Registros alias para o ALB; roteamento ponderado permite cutover gradual e rollback.", required: false);
        }

        AddDatabaseComponents(Component, projects, result);

        var storageProjects = projects.Where(p => p.Profile.HasAny(Signal.FileSystemWrites, Signal.AppDataFolder, Signal.UncPaths, Signal.WindowsPaths, Signal.FileUploads, Signal.Ftp)).ToList();
        if (storageProjects.Count > 0)
        {
            var shares = storageProjects.SelectMany(p => p.Profile.Get(Signal.UncPaths)?.Details ?? []).Distinct().Take(4).ToList();
            var unc = storageProjects.Any(p => p.Profile.Has(Signal.UncPaths));
            var fsx = Component("fsx", "Amazon FSx for Windows File Server", "Compartilhamentos SMB (pastas de rede) montados nas instâncias", shares.Count > 0 ? "pastas de rede: " + string.Join(", ", shares) : "pastas locais/rede do servidor",
                "Mesmo caminho UNC, sem mudar o código; Multi-AZ, backups e cotas; integrado ao Active Directory. Dados copiados com robocopy ou AWS DataSync.",
                required: unc, notes: "Exige um Active Directory (AWS Managed Microsoft AD ou o AD corporativo via VPN); informe ActiveDirectoryId na stack de storage.");
            foreach (var (pr, _) in storageProjects) fsx.UsedBy.Add(pr.Project.Name);
            var s3 = Component("s3", "Amazon S3 (+ AWS Storage Gateway File Gateway)", "Arquivos no S3 sem alterar o código: o File Gateway expõe o bucket como compartilhamento SMB", "pastas de rede / locais",
                "Os arquivos ficam duráveis e baratos no S3, prontos para a etapa .NET 10 (eventos S3 → Lambda); o gateway roda como instância EC2 ou appliance on-premises.", required: false,
                notes: "Bucket privado, versionado e criptografado (criado pela stack de storage). Para quem grava direto via SDK não é preciso gateway.");
            foreach (var (pr, _) in storageProjects) s3.UsedBy.Add(pr.Project.Name);
            if (storageProjects.Any(p => p.Profile.Has(Signal.Ftp)))
                Component("transfer", "AWS Transfer Family", "SFTP/FTPS gerenciado sobre o S3", "servidor FTP", "Troca de arquivos com parceiros sem EC2 dedicado.", required: false);
        }

        var adProjects = projects.Where(p => p.Profile.HasAny(Signal.WindowsAuth, Signal.ActiveDirectory) || p.Profile.Databases.Any(d => d.IntegratedSecurity && !(d.Server ?? "").Contains("localdb", StringComparison.OrdinalIgnoreCase))).ToList();
        if (adProjects.Count > 0 || components.ContainsKey("fsx"))
        {
            var ad = Component("ad", "AWS Managed Microsoft AD (ou AD Connector)", "Domínio para as instâncias: autenticação Windows, Integrated Security e FSx", "controladores de domínio on-premises",
                adProjects.Count > 0 ? "A aplicação depende de identidade Windows (autenticação/Integrated Security): as instâncias precisam ingressar num domínio com trust para o AD corporativo." : "Pré-requisito do FSx for Windows; pode ser o AD corporativo acessado pela VPN (AD Connector).",
                required: adProjects.Count > 0);
            foreach (var (pr, _) in adProjects) ad.UsedBy.Add(pr.Project.Name);
        }

        var mailboxProjects = projects.Where(p => p.Profile.Has(Signal.MailboxReading)).ToList();
        if (mailboxProjects.Count > 0)
        {
            var libs = string.Join(", ", mailboxProjects.SelectMany(p => p.Profile.Get(Signal.MailboxReading)!.Details).Distinct().Take(3));
            var graph = Component("graph", "Microsoft Graph (fora da AWS) / Exchange via VPN", "Leitura da caixa postal pela automação", libs.Length > 0 ? $"leitura de caixa postal ({libs})" : "leitura de caixa postal",
                "Sem mudar a arquitetura a instância continua consultando a caixa; se o Exchange for on-premises, pela VPN. EWS está sendo desligado no Exchange Online: planeje a troca por Microsoft Graph (há SDK para .NET Framework).", required: false,
                notes: "Na trilha .NET 10 a leitura vira SES recebimento ou Lambda agendada com Graph.");
            foreach (var (pr, _) in mailboxProjects) graph.UsedBy.Add(pr.Project.Name);
        }
        var smtpProjects = projects.Where(p => p.Profile.Has(Signal.Smtp)).ToList();
        if (smtpProjects.Count > 0)
        {
            var hosts = string.Join(", ", smtpProjects.SelectMany(p => p.Profile.Get(Signal.Smtp)!.Details).Where(d => d.Contains('.')).Distinct().Take(3));
            var ses = Component("ses", "Amazon SES", "Envio de e-mail", hosts.Length > 0 ? $"SMTP {hosts}" : "servidor SMTP interno", "Endpoint SMTP compatível (porta 587, credenciais SMTP) sem mudar o código; métricas de bounce; DKIM gerenciado.", notes: "Verifique domínio e saia do sandbox antes do go-live.");
            foreach (var (pr, _) in smtpProjects) ses.UsedBy.Add(pr.Project.Name);
        }
        if (projects.Any(p => p.Profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode) || p.Profile.Databases.Count > 0))
        {
            var sm = Component("secrets", "AWS Secrets Manager", "Senhas de banco, caixa postal, SMTP e chaves de API", "senhas no web.config/app.config",
                "O script after-install.ps1 do CodeDeploy lê o segredo <app>/<projeto>/config (JSON chave→valor) e grava no config da instância: nada no repositório nem na AMI.", notes: "Instance profile restrito ao prefixo <app>/; rotação automática para a senha do RDS.");
            foreach (var (pr, _) in projects.Where(p => p.Profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode) || p.Profile.Databases.Count > 0)) sm.UsedBy.Add(pr.Project.Name);
        }
        var internalHosts = projects.SelectMany(p => p.Profile.ExternalEndpoints.Select(HostOf).Concat(p.Profile.Databases.Select(d => (d.Server ?? "").Split(',')[0].Split('\\')[0])))
            .Where(h => ModernizationAdvisor.IsInternalHost(h) && !h.Contains("localdb", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var nonDbInternal = projects.SelectMany(p => p.Profile.ExternalEndpoints.Select(HostOf)).Where(ModernizationAdvisor.IsInternalHost).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (internalHosts.Count > 0 || components.ContainsKey("ad"))
        {
            var vpn = Component("vpn", "AWS Site-to-Site VPN ou Direct Connect + Route 53 Resolver", "Conectividade com a rede on-premises", internalHosts.Count > 0 ? "acesso direto na LAN a " + string.Join(", ", internalHosts.Take(5)) : "rede corporativa (AD)",
                nonDbInternal.Count > 0 ? "Integrações internas (ERP, serviços, Exchange) continuam on-premises; o DNS interno precisa ser resolvível da VPC." : "Necessária para o domínio/AD e para a migração dos dados.",
                required: nonDbInternal.Count > 0 || adProjects.Count > 0, notes: "Direct Connect se o volume for alto; VPN para começar.");
            foreach (var (pr, p) in projects.Where(p => p.Profile.ExternalEndpoints.Select(HostOf).Any(ModernizationAdvisor.IsInternalHost) || p.Profile.Databases.Any(d => ModernizationAdvisor.IsInternalHost((d.Server ?? "").Split(',')[0].Split('\\')[0]))))
                vpn.UsedBy.Add(pr.Project.Name);
        }
        Component("vpc", "Amazon VPC (subnets privadas, NAT Gateway, VPC endpoints)", "Rede", "rede do datacenter", "Instâncias em subnets privadas; endpoints para S3, SSM, Secrets Manager e CloudWatch evitam NAT para serviços AWS.", notes: "NAT Gateway é custo fixo: use endpoints.");

        proposal.Components.AddRange(components.Values.OrderBy(c => c.Required ? 0 : 1).ThenBy(c => OrderLift(c.Id)));
        proposal.Summary = Summarize(result, allProjects, deployables) + " Destino escolhido: .NET Framework 4.8.1 sem alteração de código (lift-and-shift para EC2 Windows); a coluna 'Alternativas' mostra a hospedagem após a migração para .NET 10.";
        BuildLiftPhases(proposal, projects, components);
        BuildLiftRisks(proposal, projects, deployables);
        BuildLiftCostNotes(proposal, components);
        proposal.Diagram = MermaidLift(deployables, components);
        return proposal;
    }

    private static int OrderLift(string id) => id switch
    {
        "vpc" => 0, "alb" => 1, "route53" => 2, "ec2" => 3, "ad" => 4, _ when id.StartsWith("rds") => 5, "fsx" => 6, "s3" => 7, "transfer" => 8, "ses" => 9, "graph" => 10,
        "secrets" => 11, "ssm" => 12, "cloudwatch" => 13, "backup" => 14, "vpn" => 15, "cicd" => 16, _ => 50
    };

    private static void BuildLiftPhases(ArchitectureProposal proposal, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> projects, Dictionary<string, AwsComponent> components)
    {
        var p = proposal.Phases;
        var n = 1;
        p.Add($"{n++}. Fundação: VPC com subnets privadas e endpoints (stack 00-network), Secrets Manager com os segredos de cada projeto, bucket de artefatos do CodeDeploy" + (components.ContainsKey("ad") ? ", diretório (Managed AD/AD Connector)" : "") + (components.ContainsKey("vpn") ? ", VPN/Direct Connect e Route 53 Resolver para o DNS interno" : "") + ".");
        if (components.Keys.Any(k => k.StartsWith("rds")))
            p.Add($"{n++}. Dados: RDS (stack 10-data), restore nativo do .bak via S3 ou AWS DMS com replicação contínua; usuário da aplicação com autenticação SQL e connection string no Secrets Manager; Encrypt/TLS e collation validados.");
        if (components.ContainsKey("fsx"))
            p.Add($"{n++}. Arquivos: FSx for Windows (stack 20-storage) com os mesmos nomes de compartilhamento; cópia inicial com robocopy/DataSync e cópia final no cutover; ou File Gateway sobre o S3.");
        p.Add($"{n++}. Aplicação: compilar a saída do Migrator (4.8.1) com MSBuild, publicar pelo workflow gerado (CodeDeploy) na stack 30-compute; machineKey/segredos/fuso definidos pelos scripts; health check no ALB.");
        p.Add($"{n++}. Cutover: homologação com dados reais, teste de carga para dimensionar instâncias, Route 53 com roteamento ponderado (10% → 100%), alarmes e runbook de rollback (voltar o peso do DNS).");
        p.Add($"{n}. Próxima etapa (modernização): rodar o Migrator sem --target framework para gerar a versão .NET 10 e mover cada aplicação para ECS Fargate/Lambda (coluna 'Alternativas'), eliminando a licença Windows e os servidores.");
    }

    private static void BuildLiftRisks(ArchitectureProposal proposal, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> projects, List<(ProjectResult Result, ApplicationProfile Profile)> deployables)
    {
        var r = proposal.Risks;
        r.Add("Lift-and-shift mantém o custo de Windows (licença no preço da instância) e servidores para administrar; o ganho é prazo e risco baixo, não custo. Planeje a migração para .NET 10 como etapa seguinte.");
        r.Add("Atualização 4.x → 4.8.1 é compatível em binário, mas há mudanças de comportamento acumuladas (TLS 1.2 por padrão, criptografia, Regex, WPF/WinForms DPI): teste os fluxos de integração antes do cutover.");
        if (projects.Any(p => p.Profile.Has(Signal.InProcSession)))
            r.Add("Sessão InProc com mais de uma instância atrás do ALB: usuários perdem a sessão a cada deploy/scale-in. Stickiness mitiga; sessão em SQL/Redis resolve.");
        if (projects.Any(p => p.Profile.HasAny(Signal.WindowsPaths, Signal.AppDataFolder)))
            r.Add("Caminhos locais (C:\\..., App_Data) no disco da instância: com Auto Scaling os arquivos somem na troca da instância. Mova para FSx/File Gateway ou fixe uma instância.");
        if (projects.Any(p => p.Profile.Databases.Any(d => d.IntegratedSecurity && !(d.Server ?? "").Contains("localdb", StringComparison.OrdinalIgnoreCase))))
            r.Add("Integrated Security: sem domínio (Managed AD) a conexão falha; decida entre ingressar as instâncias no domínio ou trocar para autenticação SQL antes do primeiro deploy.");
        if (projects.Any(p => p.Profile.Has(Signal.MailboxReading)))
            r.Add("EWS no Exchange Online está em desligamento: a automação de caixa postal pode parar independentemente da migração; priorize a troca por Microsoft Graph.");
        if (projects.Any(p => p.Profile.Has(Signal.Msmq)))
            r.Add("MSMQ com filas privadas na instância: mensagens se perdem se a instância for substituída; não escale horizontalmente.");
        if (projects.Any(p => p.Profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode)))
            r.Add("Senhas em texto claro nos configs copiados: bloqueie no pipeline (git-secrets/trufflehog), use os scripts do CodeDeploy para injetar do Secrets Manager e rotacione o que já vazou.");
        if (deployables.Any(d => d.Result.Project.Kind == ProjectKind.Console))
            r.Add("Consoles agendados em instância única: sem lock distribuído, não rode a mesma tarefa em duas instâncias; o Auto Scaling group de tamanho 1 apenas recria a instância.");
    }

    private static void BuildLiftCostNotes(ArchitectureProposal proposal, Dictionary<string, AwsComponent> components)
    {
        var c = proposal.CostNotes;
        c.Add("EC2 Windows: licença inclusa (~2x o Linux equivalente); comece com t3.medium/t3.large por aplicação web e 1 instância por serviço; Savings Plans após estabilizar.");
        if (components.Keys.Any(k => k.StartsWith("rds-sqlserver")))
            c.Add("RDS for SQL Server: a licença inclusa domina o custo (Standard ≈ 2-3x um RDS PostgreSQL); vários bancos cabem numa instância; Express é gratuito em licença até 10 GB por banco.");
        if (components.ContainsKey("fsx")) c.Add("FSx for Windows: mínimo de 32 GB SSD e throughput de 32 MB/s; Multi-AZ dobra o custo — use Single-AZ em homologação.");
        c.Add("CodeDeploy em EC2 não tem custo; GitHub Actions com runner Windows consome minutos a 2x o Linux.");
        c.Add("NAT Gateway tem custo fixo por hora + por GB; VPC endpoints para S3/SSM/Secrets Manager/CloudWatch evitam a maior parte do tráfego.");
        c.Add("O custo cai de verdade na etapa seguinte (.NET 10 em Fargate Linux/Lambda): mantenha a migração de código no roadmap.");
    }

    private static string MermaidLift(List<(ProjectResult Result, ApplicationProfile Profile)> deployables, Dictionary<string, AwsComponent> components)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        sb.AppendLine("    users([Usuários])");
        if (components.ContainsKey("alb")) sb.AppendLine("    alb[Application Load Balancer]");
        sb.AppendLine("    subgraph aws[AWS - VPC]");
        sb.AppendLine("        direction LR");
        sb.AppendLine("        subgraph ec2[EC2 Windows - Auto Scaling]");
        foreach (var (pr, _) in deployables)
        {
            var label = pr.Project.Kind switch { ProjectKind.Web => "IIS", ProjectKind.WindowsService => "Windows Service", _ => "tarefa agendada" };
            sb.AppendLine($"            {Id(pr.Project.Name)}[\"{pr.Project.Name}<br/>{label} (.NET Framework 4.8.1)\"]");
        }
        sb.AppendLine("        end");
        foreach (var id in new[] { "rds-sqlserver", "rds-oracle", "rds-mysql", "rds-postgres", "rds-other", "fsx", "ad" }.Where(components.ContainsKey))
            sb.AppendLine($"        {Id(id)}[({(id == "fsx" ? "FSx for Windows" : id == "ad" ? "Managed AD" : ShortName(components[id]))})]");
        sb.AppendLine("    end");
        foreach (var id in new[] { "s3", "ses", "secrets", "ssm", "cloudwatch", "cicd", "graph" }.Where(components.ContainsKey))
            sb.AppendLine($"    {Id(id)}[{(id == "s3" ? "S3 via File Gateway" : id == "cicd" ? "CodeDeploy" : id == "graph" ? "Exchange / Graph" : ShortName(components[id]))}]");
        if (components.ContainsKey("vpn"))
        {
            sb.AppendLine("    subgraph onprem[On-premises]");
            var hosts = deployables.SelectMany(d => d.Profile.ExternalEndpoints.Select(HostOf)).Where(ModernizationAdvisor.IsInternalHost).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            if (hosts.Count == 0) hosts.Add("sistemas internos");
            foreach (var h in hosts) sb.AppendLine($"        {Id("op_" + h)}[{h}]");
            sb.AppendLine("    end");
        }
        var webs = deployables.Where(d => d.Result.Project.Kind == ProjectKind.Web).ToList();
        if (components.ContainsKey("alb")) sb.AppendLine("    users --> alb");
        foreach (var (pr, _) in webs) sb.AppendLine($"    alb --> {Id(pr.Project.Name)}");
        foreach (var (pr, p) in deployables)
        {
            var node = Id(pr.Project.Name);
            foreach (var db in p.Databases.Select(d => d.Provider).Distinct())
            {
                var id = db switch { "SQL Server" => "rds-sqlserver", "Oracle" => "rds-oracle", "MySQL" => "rds-mysql", "PostgreSQL" => "rds-postgres", "SQLite" => null, _ => "rds-other" };
                if (id != null && components.ContainsKey(id)) sb.AppendLine($"    {node} --> {Id(id)}");
            }
            if (components.ContainsKey("fsx") && p.HasAny(Signal.UncPaths, Signal.FileSystemWrites, Signal.WindowsPaths, Signal.FileUploads)) sb.AppendLine($"    {node} -- SMB --> fsx");
            if (components.ContainsKey("s3") && p.HasAny(Signal.UncPaths, Signal.FileSystemWrites, Signal.WindowsPaths, Signal.FileUploads)) sb.AppendLine($"    {node} -.-> s3");
            if (components.ContainsKey("ses") && p.Has(Signal.Smtp)) sb.AppendLine($"    {node} --> ses");
            if (components.ContainsKey("graph") && p.Has(Signal.MailboxReading)) sb.AppendLine($"    {node} -- EWS/Graph --> graph");
            if (components.ContainsKey("ad") && (p.HasAny(Signal.WindowsAuth, Signal.ActiveDirectory) || p.Databases.Any(d => d.IntegratedSecurity))) sb.AppendLine($"    {node} -.-> ad");
            if (components.ContainsKey("vpn"))
                foreach (var h in p.ExternalEndpoints.Select(HostOf).Where(ModernizationAdvisor.IsInternalHost).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
                    sb.AppendLine($"    {node} -. VPN .-> {Id("op_" + h)}");
        }
        if (deployables.Count > 0)
        {
            var first = Id(deployables[0].Result.Project.Name);
            if (components.ContainsKey("cicd")) sb.AppendLine($"    cicd -- deploy --> {first}");
            if (components.ContainsKey("secrets")) sb.AppendLine($"    secrets -.-> {first}");
            if (components.ContainsKey("cloudwatch")) sb.AppendLine($"    {first} -. logs .-> cloudwatch");
        }
        return sb.ToString().TrimEnd();
    }
}
