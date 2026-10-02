using System.Text;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>Turns application profiles into a hosting recommendation per project and a target architecture for the solution.</summary>
public static class AwsArchitect
{
    /// <summary>Culture assumed for TZ/LANG in generated Dockerfiles when the application does not declare one.</summary>
    public const string DefaultCulture = "pt-BR";

    private static readonly Signal[] HardWindows =
        [Signal.Com, Signal.ComPlus, Signal.Registry, Signal.PInvoke, Signal.OfficeInterop, Signal.CrystalReports, Signal.ReportViewer, Signal.Wmi, Signal.IisAdministration, Signal.WinForms, Signal.Wpf, Signal.OfficeOleDb];

    /// <summary>Soft dependencies that do not stop a worker from becoming a Lambda (the conversion is required in any case).</summary>
    private static readonly Signal[] LambdaTolerated = [Signal.WindowsServiceHost, Signal.EventLog, Signal.UncPaths];

    /// <summary>Source files above which a run-to-completion automation is considered too big for a Lambda handler rewrite.</summary>
    public const int LambdaMaxSourceFiles = 25;

    private static readonly Signal[] SoftWindows =
        [Signal.SystemDrawing, Signal.EventLog, Signal.PerformanceCounter, Signal.WindowsServiceHost, Signal.Msmq, Signal.WindowsAuth, Signal.ActiveDirectory, Signal.UncPaths];

    private static readonly Dictionary<Signal, string> SignalLabels = new()
    {
        [Signal.Com] = "componentes COM", [Signal.ComPlus] = "COM+ (System.EnterpriseServices)", [Signal.Registry] = "Registro do Windows", [Signal.PInvoke] = "P/Invoke em DLLs do Windows",
        [Signal.OfficeInterop] = "Office Interop", [Signal.CrystalReports] = "Crystal Reports", [Signal.ReportViewer] = "ReportViewer/RDLC", [Signal.Wmi] = "WMI (System.Management)",
        [Signal.IisAdministration] = "Microsoft.Web.Administration (IIS)", [Signal.WinForms] = "Windows Forms", [Signal.Wpf] = "WPF",
        [Signal.SystemDrawing] = "System.Drawing (GDI+)", [Signal.EventLog] = "Event Log", [Signal.PerformanceCounter] = "PerformanceCounter", [Signal.WindowsServiceHost] = "ServiceBase (Windows Service)",
        [Signal.Msmq] = "MSMQ", [Signal.WindowsAuth] = "Autenticação Windows (Negotiate)", [Signal.ActiveDirectory] = "Active Directory (System.DirectoryServices)", [Signal.UncPaths] = "compartilhamentos SMB (UNC)",
        [Signal.OfficeOleDb] = "provider OLE DB ACE/Jet (Excel/Access, só Windows 32/64 bits com o Access Database Engine)"
    };

    public static HostingRecommendation Recommend(ProjectInfo project, ApplicationProfile profile)
    {
        var rec = new HostingRecommendation { Project = project.Name, Kind = project.Kind };
        foreach (var s in HardWindows.Where(profile.Has)) rec.HardWindowsDependencies.Add(Describe(profile, s));
        foreach (var s in SoftWindows.Where(profile.Has)) rec.SoftWindowsDependencies.Add(Describe(profile, s));
        // Desktop UI frameworks referenced by a desktop project are the project itself, not a dependency.
        if (project.Kind == ProjectKind.Desktop) rec.HardWindowsDependencies.Clear();

        switch (project.Kind)
        {
            case ProjectKind.Test:
                rec.Primary = AwsHosting.NotDeployable;
                rec.Rationale.Add("Projeto de testes: roda no pipeline de CI (AWS CodeBuild ou GitHub Actions), não é publicado.");
                return rec;
            case ProjectKind.ClassLibrary:
                rec.Primary = AwsHosting.NotDeployable;
                rec.Rationale.Add("Biblioteca: é empacotada dentro dos projetos que a referenciam; as dependências dela foram consideradas na recomendação deles.");
                if (profile.MergedFrom.Count == 0 && (rec.HardWindowsDependencies.Count > 0 || rec.SoftWindowsDependencies.Count > 0))
                    rec.Rationale.Add("Atenção: contém dependências Windows que se propagam para quem a consome: " + string.Join("; ", rec.HardWindowsDependencies.Concat(rec.SoftWindowsDependencies)) + ".");
                return rec;
            case ProjectKind.Desktop:
                rec.Primary = AwsHosting.Desktop;
                rec.Rationale.Add("Aplicação desktop (WinForms/WPF): roda na estação do usuário. Se precisar centralizar, Amazon AppStream 2.0 ou WorkSpaces entregam o executável Windows via streaming.");
                rec.Alternatives.Add("Reescrever a interface como web (Blazor/MVC) e hospedar no ECS Fargate — maior esforço, elimina a distribuição de executáveis.");
                return rec;
        }

        var requiresWindows = rec.RequiresWindows;
        if (project.IsVisualBasic)
            rec.Prerequisites.Add("Projeto VB.NET: converter para SDK-style/net10.0 (Upgrade Assistant) ou para C# (CodeConverter) antes de qualquer deploy; o Migrator não gerou a cópia migrada.");
        var webForms = profile.Get(Signal.WebForms);
        if (project.Kind == ProjectKind.Web && webForms != null && profile.MvcControllerCount == 0 && profile.ApiControllerCount == 0)
        {
            rec.Primary = AwsHosting.Ec2Windows;
            rec.Rationale.Add($"Aplicação Web Forms ({webForms.Count} arquivo(s) .aspx/.ascx/.master) sem MVC/Web API: não roda no .NET 10 até ser reescrita. Enquanto isso, IIS em EC2 Windows (lift-and-shift) ou permanece on-premises.");
            rec.Alternatives.Add("Reescrever em Razor Pages/Blazor e então ECS Fargate (Linux) + ALB como as demais aplicações web.");
            rec.Prerequisites.Insert(0, "Reescrever as páginas Web Forms (item WEB-WEBFORMS do inventário traz a estimativa).");
            return rec;
        }
        if (project.Kind == ProjectKind.Web)
        {
            if (webForms != null)
                rec.Prerequisites.Add($"Reescrever {webForms.Count} arquivo(s) Web Forms (.aspx/.ascx/.master) que não compilam no .NET 10; o restante (MVC/Web API) migra normalmente.");
            if (requiresWindows)
            {
                rec.Primary = profile.Has(Signal.IisAdministration) ? AwsHosting.Ec2Windows : AwsHosting.EcsWindows;
                rec.Rationale.Add("Depende de componentes exclusivos do Windows: " + string.Join("; ", rec.HardWindowsDependencies) + ".");
                rec.Rationale.Add(rec.Primary == AwsHosting.EcsWindows
                    ? "Containers Windows no ECS (Fargate Windows ou EC2 launch type) mantêm o modelo de deploy por imagem; custam ~2x o Linux e não têm Fargate Spot."
                    : "A dependência do IIS impede containers: EC2 Windows com Auto Scaling group atrás do ALB, imagens via EC2 Image Builder.");
                rec.Alternatives.Add("ECS Fargate (Linux) depois de remover as dependências Windows listadas — meta recomendada para o portfólio.");
            }
            else
            {
                rec.Primary = AwsHosting.EcsFargate;
                rec.Rationale.Add("Aplicação ASP.NET Core sem dependências Windows: container Linux no ECS Fargate atrás de um Application Load Balancer é o padrão de menor operação para o portfólio (mesmo pipeline, mesma observabilidade para todas as aplicações).");
                if (rec.SoftWindowsDependencies.Count > 0)
                    rec.Rationale.Add("Pré-requisito: substituir " + string.Join("; ", rec.SoftWindowsDependencies) + " (itens MOD-WIN-* da modernização).");
                var apiOnly = profile.MvcControllerCount == 0 && profile.ViewCount == 0 && !profile.HasAny(Signal.SignalR, Signal.InProcSession, Signal.ExternalSession, Signal.LongRequests, Signal.LargeUploads);
                if (apiOnly && profile.ApiControllerCount > 0)
                    rec.Alternatives.Add("AWS Lambda (Amazon.Lambda.AspNetCoreServer.Hosting ou Lambda Web Adapter) + API Gateway/ALB — API sem estado e sem views; custo zero em ociosidade, cold start de ~1 s e limite de 15 min por requisição.");
                if (!profile.HasAny(Signal.SignalR, Signal.WindowsAuth, Signal.ActiveDirectory))
                    rec.Alternatives.Add("AWS App Runner — mesma imagem Docker com menos configuração (sem cluster/ALB próprios); menos controle de rede e sem Spot. Bom para aplicações internas pequenas.");
                rec.Alternatives.Add("AWS Elastic Beanstalk (.NET on Linux) — sem Docker, deploy do publish; plataforma mais antiga, menos padronizável que ECS.");
            }
        }
        else // WindowsService / Console
        {
            var queueDriven = profile.HasAny(Signal.Msmq, Signal.RabbitMq, Signal.MessageBusFramework, Signal.Kafka, Signal.AzureServiceBus);
            var scheduled = profile.HasAny(Signal.Scheduler, Signal.TimerLoop);
            var fileDriven = profile.HasAny(Signal.FileWatcher, Signal.Ftp) || (profile.HasAny(Signal.FileSystemWrites, Signal.UncPaths, Signal.WindowsPaths) && profile.Has(Signal.SpreadsheetFiles));
            var mailboxDriven = profile.Has(Signal.MailboxReading);
            var eventDriven = queueDriven || fileDriven || mailboxDriven;
            var blocksLambda = rec.SoftWindowsDependencies.Count > 0 && SoftWindows.Where(profile.Has).Any(s => !LambdaTolerated.Contains(s));
            var small = project.SourceFiles.Count() <= LambdaMaxSourceFiles;
            if (requiresWindows)
            {
                rec.Primary = AwsHosting.EcsWindows;
                rec.Rationale.Add("Depende de componentes exclusivos do Windows: " + string.Join("; ", rec.HardWindowsDependencies) + ". Container Windows no ECS (ou EC2 Windows se precisar de sessão interativa).");
                rec.Alternatives.Add("ECS Fargate (Linux) após remover as dependências Windows.");
            }
            else if (eventDriven && !blocksLambda && small && !profile.Has(Signal.Scheduler))
            {
                // Typical back-office automation: reacts to files, e-mails or messages, runs to completion. Serverless is the cheapest and simplest fit.
                rec.Primary = AwsHosting.Lambda;
                var triggers = new List<string>();
                if (fileDriven) triggers.Add("arquivos: S3 Event Notifications (ObjectCreated) → SQS → Lambda; pastas de rede viram um bucket via AWS Storage Gateway (File Gateway) ou os parceiros enviam por AWS Transfer Family");
                if (mailboxDriven) triggers.Add("e-mail: Amazon SES recebimento (regra → S3 → SQS/Lambda) quando o domínio da caixa puder apontar para a AWS; senão, EventBridge Scheduler disparando a Lambda que consulta a caixa via Microsoft Graph/IMAP");
                if (queueDriven) triggers.Add("fila: Amazon SQS como gatilho da Lambda (batch, DLQ e retry nativos)");
                rec.Rationale.Add($"Automação orientada a evento ({(fileDriven ? "arquivos" : "")}{(fileDriven && (mailboxDriven || queueDriven) ? ", " : "")}{(mailboxDriven ? "caixa de e-mail" : "")}{(mailboxDriven && queueDriven ? ", " : "")}{(queueDriven ? "fila" : "")}), {project.SourceFiles.Count()} arquivo(s) de código e sem dependências Windows: AWS Lambda executa só quando há trabalho, sem container 24x7 nem agendamento cego.");
                rec.Rationale.Add("Gatilhos: " + string.Join("; ", triggers) + ".");
                rec.Rationale.Add("Limites do Lambda: 15 min por execução, 10 GB de memória, /tmp de até 10 GB; processamento maior que isso vai para a alternativa ECS.");
                rec.Alternatives.Add("Tarefa ECS Fargate agendada (EventBridge Scheduler) — mantém o Main() como está (sem reescrever para handler); escolha se a execução puder passar de 15 min ou se preferir uniformidade com as demais aplicações.");
                rec.Alternatives.Add("Worker ECS Fargate contínuo consumindo SQS — para volume alto e constante.");
                rec.Prerequisites.Add("Reescrever o ponto de entrada como handler Lambda (Amazon.Lambda.Core + Amazon.Lambda.SQSEvents/S3Events) ou usar Amazon.Lambda.Annotations; publicar com Amazon.Lambda.Tools ou imagem de container.");
                if (mailboxDriven && profile.Get(Signal.MailboxReading)!.Details.Any(d => d.Contains("Exchange", StringComparison.OrdinalIgnoreCase)))
                    rec.Prerequisites.Add("Trocar EWS por Microsoft Graph (EWS está sendo bloqueado no Exchange Online) ou mover a caixa para recebimento via SES.");
                if (profile.Has(Signal.FileWatcher)) rec.Prerequisites.Add("Substituir FileSystemWatcher/polling de pasta pelo evento do S3 (não existe 'pasta' no Lambda).");
            }
            else if (queueDriven)
            {
                rec.Primary = AwsHosting.EcsFargateWorker;
                rec.Rationale.Add("Processo orientado a fila: um serviço ECS Fargate (Linux) consumindo Amazon SQS, com auto scaling pela profundidade da fila (métrica ApproximateNumberOfMessagesVisible).");
                rec.Alternatives.Add("AWS Lambda com gatilho SQS — se cada mensagem for processada em menos de 15 min e sem dependências pesadas; elimina o container sempre ligado.");
            }
            else if (scheduled)
            {
                rec.Primary = AwsHosting.EcsScheduledTask;
                rec.Rationale.Add("Processo periódico (timer/agendador): Amazon EventBridge Scheduler dispara uma tarefa ECS Fargate (RunTask) no horário; a task termina ao concluir e não há custo entre execuções.");
                if (eventDriven)
                    rec.Rationale.Add("A automação reage a " + (fileDriven ? "arquivos" : mailboxDriven ? "e-mails" : "mensagens") + ": em vez de agendar, dispare pelo evento (S3 Event Notifications, SES recebimento ou SQS) — " +
                        (!small ? "o tamanho do projeto" : !blocksLambda ? "o agendador embutido (Quartz/Hangfire)" : "as dependências Windows substituíveis") + " foi o que impediu recomendar Lambda diretamente.");
                rec.Alternatives.Add("ECS Fargate como serviço contínuo (BackgroundService + PeriodicTimer) — mais simples de portar, paga 24x7 e precisa de lock distribuído se escalar.");
                rec.Alternatives.Add("AWS Lambda agendado pelo EventBridge — se a execução durar menos de 15 min e couber em 10 GB de memória.");
            }
            else
            {
                rec.Primary = project.Kind == ProjectKind.Console ? AwsHosting.EcsScheduledTask : AwsHosting.EcsFargateWorker;
                rec.Rationale.Add(project.Kind == ProjectKind.Console
                    ? "Aplicação console (lote): tarefa ECS Fargate disparada pelo EventBridge Scheduler ou manualmente (RunTask)."
                    : "Serviço de longa duração sem fila nem agendamento detectados: serviço ECS Fargate (Linux) com 1 task; revise se poderia ser agendado.");
                rec.Alternatives.Add("AWS Lambda — para execuções curtas (< 15 min).");
            }
            if (rec.SoftWindowsDependencies.Count > 0 && !requiresWindows)
                rec.Rationale.Add("Pré-requisito: substituir " + string.Join("; ", rec.SoftWindowsDependencies) + " (itens MOD-WIN-* da modernização).");
        }

        // Prerequisites common to containers
        if (project.Kind == ProjectKind.Web)
        {
            rec.Prerequisites.Add("Endpoint /health para o target group do ALB (gerado no Program.cs) e ForwardedHeaders para IP/esquema reais (gerado).");
            if (profile.Has(Signal.InProcSession)) rec.Prerequisites.Add("Sessão em ElastiCache (AddStackExchangeRedisCache) em vez de memória.");
            if (profile.HasAny(Signal.FormsAuth, Signal.InProcSession, Signal.MachineKey) || profile.ViewCount > 0) rec.Prerequisites.Add("Anel de chaves do Data Protection persistido fora do container (SSM Parameter Store ou S3+KMS).");
        }
        if (profile.Has(Signal.StaticState)) rec.Prerequisites.Add("Remover estado em coleções estáticas (cada task teria uma cópia).");
        if (profile.HasAny(Signal.FileSystemWrites, Signal.AppDataFolder, Signal.FileUploads, Signal.UncPaths, Signal.WindowsPaths)) rec.Prerequisites.Add("Arquivos persistentes no S3 (ou volume EFS), não no disco do container.");
        if (profile.HasAny(Signal.FileLogging, Signal.EventLog)) rec.Prerequisites.Add("Logs em stdout (JSON) → CloudWatch Logs.");
        if (profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode) || profile.Databases.Any(d => !d.IntegratedSecurity))
            rec.Prerequisites.Add(profile.Has(Signal.SecretsInCode)
                ? "Segredos e connection strings no Secrets Manager, injetados na task definition; remover as credenciais embutidas no código e rotacioná-las."
                : "Segredos e connection strings no Secrets Manager, injetados na task definition.");
        if (profile.Databases.Any(d => d.IntegratedSecurity && !(d.Server ?? "").Contains("localdb", StringComparison.OrdinalIgnoreCase))) rec.Prerequisites.Add("Connection strings com autenticação SQL (Integrated Security não funciona no Fargate).");
        if (profile.Has(Signal.WindowsServiceHost) && !requiresWindows) rec.Prerequisites.Add("Converter ServiceBase em BackgroundService (Worker Service) para rodar em Linux e encerrar no SIGTERM.");
        if (profile.Has(Signal.Msmq)) rec.Prerequisites.Add("Substituir MSMQ por SQS.");
        if (profile.Has(Signal.OfficeOleDb)) rec.Prerequisites.Add("Ler Excel/Access sem o provider ACE/Jet (ExcelDataReader/ClosedXML; Access → exportar para RDS) para sair do Windows.");
        if (profile.Has(Signal.DateTimeNow) || profile.Culture != null) rec.Prerequisites.Add("Fuso horário e cultura definidos no container (TZ/LANG no Dockerfile gerado) ou código em UTC.");
        return rec;
    }

    private static string Describe(ApplicationProfile profile, Signal signal)
    {
        var label = SignalLabels.GetValueOrDefault(signal, signal.ToString());
        var evidence = profile.Get(signal);
        if (evidence == null) return label;
        var details = evidence.Details.Where(d => !label.Contains(d, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        var where = details.Count > 0 ? string.Join(", ", details) : string.Join(", ", evidence.Locations.Take(2));
        return where.Length > 0 ? $"{label} ({where})" : label;
    }

    public static ArchitectureProposal Propose(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> projects)
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
        // Libraries and tests ship inside the deployables (whose merged profiles already include them); only when nothing is
        // deployable (a solution of libraries) do we fall back to every project so the report still says something useful.
        projects = deployables.Count > 0 ? deployables : projects;
        var webs = deployables.Where(p => p.Result.Project.Kind == ProjectKind.Web).ToList();
        var workers = deployables.Where(p => p.Result.Project.Kind != ProjectKind.Web).ToList();
        var anyContainer = deployables.Any(p => p.Result.Hosting!.Primary is AwsHosting.EcsFargate or AwsHosting.EcsFargateWorker or AwsHosting.EcsScheduledTask or AwsHosting.EcsWindows);
        var anyWindows = deployables.Any(p => p.Result.Hosting!.Primary is AwsHosting.EcsWindows or AwsHosting.Ec2Windows);

        foreach (var (pr, _) in allProjects) proposal.Hosting.Add(pr.Hosting!);

        if (anyContainer)
        {
            var ecs = Component("ecs", "Amazon ECS on AWS Fargate", "Execução dos containers (serviços web, workers e tarefas agendadas)", "IIS / Windows Services em servidores", "Sem servidores para administrar; deploy por imagem; auto scaling por CPU/fila/requisições. Um cluster por ambiente, um serviço por aplicação.",
                notes: anyWindows ? "Projetos com dependências Windows usam containers Windows (Fargate Windows ou EC2 launch type), ~2x o custo do Linux." : "Comece com 0,5 vCPU / 1 GB por task e ajuste pelo Container Insights.");
            foreach (var (pr, _) in deployables) ecs.UsedBy.Add(pr.Project.Name);
            var ecr = Component("ecr", "Amazon ECR", "Registro das imagens Docker", "pastas de publish / MSDeploy", "Imagens versionadas por commit, scan de vulnerabilidades (Inspector) e lifecycle policy.");
            foreach (var (pr, _) in deployables) ecr.UsedBy.Add(pr.Project.Name);
            var cw = Component("cloudwatch", "Amazon CloudWatch (Logs, Metrics, Alarms, Container Insights)", "Logs, métricas e alarmes", "arquivos de log / Event Log / contadores de desempenho", "stdout dos containers vai direto ao CloudWatch Logs; Container Insights dá CPU/memória por serviço; alarmes acionam SNS/auto scaling.", notes: "Defina retenção (ex.: 30 dias) para controlar custo. Traces distribuídos: AWS X-Ray via ADOT.");
            foreach (var (pr, _) in deployables) cw.UsedBy.Add(pr.Project.Name);
        }
        var lambdas = deployables.Where(p => p.Result.Hosting!.Primary == AwsHosting.Lambda).ToList();
        if (lambdas.Count > 0)
        {
            var lambda = Component("lambda", "AWS Lambda (.NET)", "Automações orientadas a evento (arquivos, e-mails, filas)", "Windows Services / consoles agendados que ficam ociosos a maior parte do tempo",
                "Executa só quando há trabalho e cobra por milissegundo; gatilhos nativos de S3, SQS, SES e EventBridge; escala por evento sem configurar auto scaling.", notes: "Runtime gerenciado .NET (ou imagem de container quando o .NET 10 ainda não estiver no runtime gerenciado); empacote com Amazon.Lambda.Tools. Configure DLQ e timeout por função.");
            foreach (var (pr, _) in lambdas) lambda.UsedBy.Add(pr.Project.Name);
            if (!anyContainer)
            {
                var cw = Component("cloudwatch", "Amazon CloudWatch (Logs, Metrics, Alarms)", "Logs, métricas e alarmes", "arquivos de log / Event Log", "Logs das funções e alarmes de erro/duração.", notes: "Defina retenção.");
                foreach (var (pr, _) in lambdas) cw.UsedBy.Add(pr.Project.Name);
            }
        }
        if (deployables.Any(p => p.Result.Hosting!.Primary == AwsHosting.Ec2Windows))
        {
            var ec2 = Component("ec2", "Amazon EC2 (Windows) + Auto Scaling", "Servidores Windows para projetos que exigem IIS/componentes legados", "servidores Windows on-premises", "Lift-and-shift controlado enquanto as dependências Windows não são removidas.", notes: "Use EC2 Image Builder para AMIs e Systems Manager para patching.");
            foreach (var (pr, _) in deployables.Where(p => p.Result.Hosting!.Primary == AwsHosting.Ec2Windows)) ec2.UsedBy.Add(pr.Project.Name);
        }
        if (webs.Count > 0)
        {
            var alb = Component("alb", "Application Load Balancer + AWS Certificate Manager", "Entrada HTTP/HTTPS, TLS, health checks, roteamento por host/caminho", "IIS bindings / certificados no servidor", "Termina TLS com certificados gratuitos do ACM, distribui entre tasks e remove tasks sem saúde (/health). Um ALB pode servir várias aplicações por host header.",
                notes: "Habilite access logs no S3 e, para aplicações públicas, AWS WAF (regras gerenciadas OWASP).");
            foreach (var (pr, _) in webs) alb.UsedBy.Add(pr.Project.Name);
            Component("route53", "Amazon Route 53", "DNS", "DNS interno / registros apontando para servidores", "Registros alias para o ALB; roteamento ponderado permite cutover gradual e rollback.", required: false);
            if (webs.Any(w => w.Profile.StaticFileCount >= 25 || w.Profile.Has(Signal.FileUploads)))
            {
                var cf = Component("cloudfront", "Amazon CloudFront", "CDN para conteúdo estático e arquivos", "arquivos servidos pelo IIS", "Cache de wwwroot e de objetos do S3 na borda; reduz carga nas tasks.", required: false);
                foreach (var (pr, _) in webs.Where(w => w.Profile.StaticFileCount >= 25 || w.Profile.Has(Signal.FileUploads))) cf.UsedBy.Add(pr.Project.Name);
            }
        }

        // Data
        var allDatabases = projects.SelectMany(p => p.Profile.Databases).DistinctBy(d => $"{d.Provider}|{d.Server}|{d.Database}").ToList();
        foreach (var provider in allDatabases.Select(d => d.Provider).Distinct())
        {
            var dbs = allDatabases.Where(d => d.Provider == provider).ToList();
            var names = string.Join(", ", dbs.Select(d => d.Database ?? d.Name).Distinct().Take(6));
            var servers = string.Join(", ", dbs.Select(d => d.Server).Where(s => s != null).Distinct().Take(4));
            var (id, service, why, notes) = provider switch
            {
                "SQL Server" => ("rds-sqlserver", "Amazon RDS for SQL Server", "Mesmo engine, backups automáticos, Multi-AZ e patching gerenciado; restore nativo a partir de .bak no S3 para migrar os dados.",
                    "Licença inclusa (Standard/Enterprise/Web/Express). Integrated Security exige AWS Managed Microsoft AD; prefira autenticação SQL + Secrets Manager. Para reduzir licenciamento a longo prazo: Aurora PostgreSQL com Babelfish."),
                "Oracle" => ("rds-oracle", "Amazon RDS for Oracle", "Engine Oracle gerenciado (BYOL ou licença inclusa para SE2).", "Avalie Aurora PostgreSQL com AWS SCT/DMS para sair do licenciamento Oracle."),
                "MySQL" => ("rds-mysql", "Amazon Aurora MySQL / RDS for MySQL", "Compatível com o driver atual; Aurora traz réplicas e failover rápido.", null),
                "PostgreSQL" => ("rds-postgres", "Amazon Aurora PostgreSQL / RDS for PostgreSQL", "Compatível com Npgsql; Aurora Serverless v2 para cargas variáveis.", null),
                "SQLite" => ("sqlite", "Arquivo SQLite (EFS) ou Amazon RDS", "SQLite em disco efêmero se perde; use EFS para persistir ou migre para RDS.", null),
                _ => ("rds-other", "Amazon RDS", "Banco acessado via OLE DB/ODBC: identifique o engine real.", "Access/Excel via OLE DB não têm equivalente gerenciado: migre para RDS ou S3.")
            };
            var c = Component(id, service, $"Banco de dados ({names})", servers.Length > 0 ? $"{provider} em {servers}" : provider, why, notes: notes);
            foreach (var db in dbs) c.UsedBy.Add(db.Project);
        }
        if (projects.Any(p => p.Profile.Has(Signal.MongoDb))) Component("documentdb", "Amazon DocumentDB", "Banco de documentos", "MongoDB", "Compatível com o driver MongoDB.", required: false);
        if (projects.Any(p => p.Profile.Has(Signal.Elasticsearch))) Component("opensearch", "Amazon OpenSearch Service", "Busca/indexação", "Elasticsearch", "Fork gerenciado do Elasticsearch.", required: false);

        // Storage
        var storageProjects = projects.Where(p => p.Profile.HasAny(Signal.FileSystemWrites, Signal.AppDataFolder, Signal.UncPaths, Signal.WindowsPaths, Signal.FileUploads, Signal.AzureStorage, Signal.Ftp)).ToList();
        if (storageProjects.Count > 0)
        {
            var details = storageProjects.SelectMany(p => new[] { Signal.UncPaths, Signal.WindowsPaths }.Select(p.Profile.Get).Where(e => e != null).SelectMany(e => e!.Details)).Distinct().Take(4).ToList();
            var s3 = Component("s3", "Amazon S3", "Arquivos (uploads, exportações, App_Data, logs de acesso)", details.Count > 0 ? "pastas locais/rede: " + string.Join(", ", details) : "disco local do servidor",
                "Durável e compartilhado entre instâncias; URLs pré-assinadas para upload/download direto; eventos S3 disparam processamento (SQS/Lambda). Lifecycle para Glacier reduz custo de histórico.",
                notes: "Bloqueie acesso público, habilite versionamento e criptografia SSE-S3/KMS. Acesso via task role (sem chaves).");
            foreach (var (pr, _) in storageProjects) s3.UsedBy.Add(pr.Project.Name);
            if (storageProjects.Any(p => p.Profile.Has(Signal.UncPaths)))
            {
                var efs = Component("efs", "Amazon EFS (ou FSx for Windows File Server)", "Sistema de arquivos compartilhado montado nas tasks", "compartilhamentos SMB (UNC)", "Alternativa de lift-and-shift quando o código espera um caminho de pasta; FSx for Windows se outros sistemas Windows também acessam a pasta.", required: false);
                foreach (var (pr, _) in storageProjects.Where(p => p.Profile.Has(Signal.UncPaths))) efs.UsedBy.Add(pr.Project.Name);
            }
            if (storageProjects.Any(p => p.Profile.Has(Signal.Ftp)))
                Component("transfer", "AWS Transfer Family", "SFTP/FTPS gerenciado sobre o S3", "servidor FTP", "Troca de arquivos com parceiros sem EC2.", required: false);
            var fileAutomations = storageProjects.Where(p => p.Result.Project.Kind != ProjectKind.Web && (p.Profile.Has(Signal.FileWatcher) || p.Result.Hosting?.Primary == AwsHosting.Lambda || p.Profile.Has(Signal.SpreadsheetFiles))).ToList();
            if (fileAutomations.Count > 0)
            {
                var ev = Component("s3-events", "Amazon S3 Event Notifications → SQS", "Gatilho das automações de arquivo", "FileSystemWatcher / varredura periódica de pasta",
                    "Cada arquivo novo no bucket gera um evento; a fila garante retry e DLQ e dispara a Lambda ou escala o worker. Elimina o polling e a janela em que o arquivo ainda está sendo copiado.", notes: "Use prefixos por tipo de arquivo e um prefixo 'processados/' para mover após o sucesso.");
                foreach (var (pr, _) in fileAutomations) ev.UsedBy.Add(pr.Project.Name);
                if (fileAutomations.Any(p => p.Profile.HasAny(Signal.UncPaths, Signal.WindowsPaths)))
                {
                    var gw = Component("storage-gateway", "AWS Storage Gateway (File Gateway)", "Compartilhamento SMB/NFS on-premises com os arquivos gravados no S3", "pastas de rede onde outros sistemas depositam arquivos",
                        "Os sistemas que hoje gravam em \\\\servidor\\pasta continuam gravando numa pasta; o File Gateway envia ao S3 e o evento dispara a automação. Permite migrar a automação sem mudar quem produz os arquivos.", required: false,
                        notes: "Alternativa quando os produtores podem mudar: gravar direto no S3 com AWS CLI/SDK ou enviar por Transfer Family.");
                    foreach (var (pr, _) in fileAutomations.Where(p => p.Profile.HasAny(Signal.UncPaths, Signal.WindowsPaths))) gw.UsedBy.Add(pr.Project.Name);
                }
            }
        }

        var mailboxProjects = projects.Where(p => p.Profile.Has(Signal.MailboxReading)).ToList();
        if (mailboxProjects.Count > 0)
        {
            var libs = string.Join(", ", mailboxProjects.SelectMany(p => p.Profile.Get(Signal.MailboxReading)!.Details).Distinct().Take(4));
            var inbound = Component("ses-inbound", "Amazon SES (recebimento) → S3 → SQS/Lambda", "Entrada de e-mails para as automações", libs.Length > 0 ? $"leitura de caixa postal ({libs})" : "leitura de caixa postal",
                "Com um (sub)domínio apontado para o SES, cada e-mail recebido vira um objeto no S3 e um evento: sem polling, sem credenciais de caixa, sem EWS. Anexos ficam no S3 prontos para processar.", required: false,
                notes: "Se a caixa precisar continuar no Exchange Online/M365, use Microsoft Graph (EWS está sendo desligado) a partir de uma Lambda agendada pelo EventBridge, com o segredo do app no Secrets Manager; ou configure uma regra de encaminhamento da caixa para o endereço do SES.");
            foreach (var (pr, _) in mailboxProjects) inbound.UsedBy.Add(pr.Project.Name);
        }

        // Messaging
        var queueProjects = projects.Where(p => p.Profile.HasAny(Signal.Msmq, Signal.MessageBusFramework, Signal.AzureServiceBus)).ToList();
        if (queueProjects.Count > 0 || (webs.Count > 0 && workers.Count > 0))
        {
            var replaces = string.Join(", ", queueProjects.SelectMany(p => new[] { Signal.Msmq, Signal.MessageBusFramework, Signal.AzureServiceBus }.Where(p.Profile.Has).Select(s => SignalLabels.GetValueOrDefault(s, s.ToString()))).Distinct());
            var sqs = Component("sqs", "Amazon SQS (+ SNS para fan-out)", "Filas entre a aplicação web e os workers", replaces.Length > 0 ? replaces : "chamadas diretas / tabelas usadas como fila",
                queueProjects.Count > 0 ? "Fila gerenciada, sem broker; FIFO quando a ordem importa; DLQ para mensagens com falha; escala os workers pela profundidade da fila." : "Desacopla a web dos workers: a web enfileira, o worker processa; permite escalar e reprocessar com DLQ.",
                required: queueProjects.Count > 0, notes: "Biblioteca: AWS.Messaging (AWS Message Processing Framework for .NET).");
            foreach (var (pr, _) in queueProjects.Count > 0 ? queueProjects : workers.Concat(webs)) sqs.UsedBy.Add(pr.Project.Name);
        }
        if (projects.Any(p => p.Profile.Has(Signal.RabbitMq)))
        {
            var mq = Component("amazonmq", "Amazon MQ for RabbitMQ", "Broker RabbitMQ gerenciado", "RabbitMQ auto-hospedado", "Compatível com o cliente atual (AMQPS); troca só de endpoint. Evolução: SQS/SNS.", required: false);
            foreach (var (pr, _) in projects.Where(p => p.Profile.Has(Signal.RabbitMq))) mq.UsedBy.Add(pr.Project.Name);
        }
        if (projects.Any(p => p.Profile.Has(Signal.Kafka))) Component("msk", "Amazon MSK", "Kafka gerenciado", "Kafka auto-hospedado", "Compatível com Confluent.Kafka.", required: false);

        // Email
        var smtpProjects = projects.Where(p => p.Profile.Has(Signal.Smtp)).ToList();
        if (smtpProjects.Count > 0)
        {
            var hosts = string.Join(", ", smtpProjects.SelectMany(p => p.Profile.Get(Signal.Smtp)!.Details).Where(d => d.Contains('.')).Distinct().Take(3));
            var ses = Component("ses", "Amazon SES", "Envio de e-mail transacional", hosts.Length > 0 ? $"SMTP {hosts}" : "servidor SMTP interno", "Endpoint SMTP compatível (porta 587) ou API; métricas de bounce/complaint; DKIM gerenciado.", notes: "Verifique domínio e saia do sandbox antes do go-live.");
            foreach (var (pr, _) in smtpProjects) ses.UsedBy.Add(pr.Project.Name);
        }

        // State
        var stateProjects = projects.Where(p => p.Result.Project.Kind == ProjectKind.Web && p.Profile.HasAny(Signal.InProcSession, Signal.ExternalSession, Signal.LocalCache, Signal.SignalR, Signal.StaticState) || p.Profile.Has(Signal.Redis)).ToList();
        if (stateProjects.Count > 0)
        {
            var replaces = string.Join(", ", stateProjects.SelectMany(p => new[] { (Signal.InProcSession, "sessão InProc"), (Signal.ExternalSession, "sessão StateServer/SQL"), (Signal.LocalCache, "HttpRuntime.Cache/MemoryCache"), (Signal.SignalR, "SignalR sem backplane"), (Signal.StaticState, "coleções estáticas"), (Signal.Redis, "Redis auto-hospedado") }.Where(x => p.Profile.Has(x.Item1)).Select(x => x.Item2)).Distinct());
            var cache = Component("elasticache", "Amazon ElastiCache (Valkey / Redis OSS)", "Sessão distribuída, cache compartilhado e backplane do SignalR", replaces, "Permite mais de uma task por serviço sem perder sessão nem divergir cache; ElastiCache Serverless cobra por uso.",
                required: stateProjects.Any(p => p.Profile.HasAny(Signal.InProcSession, Signal.ExternalSession, Signal.SignalR, Signal.Redis)), notes: "Pacotes: Microsoft.Extensions.Caching.StackExchangeRedis, Microsoft.AspNetCore.SignalR.StackExchangeRedis.");
            foreach (var (pr, _) in stateProjects) cache.UsedBy.Add(pr.Project.Name);
        }
        if (webs.Count > 0)
        {
            var ssm = Component("ssm", "AWS Systems Manager Parameter Store", "Configuração por ambiente e anel de chaves do Data Protection", "machineKey / appSettings por servidor", "Configuração centralizada carregada no IConfiguration (Amazon.Extensions.Configuration.SystemsManager); PersistKeysToAWSSystemsManager mantém cookies válidos entre tasks e deploys.");
            foreach (var (pr, _) in webs) ssm.UsedBy.Add(pr.Project.Name);
        }

        // Secrets
        if (projects.Any(p => p.Profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode) || p.Profile.Databases.Count > 0))
        {
            var inCode = projects.Any(p => p.Profile.Has(Signal.SecretsInCode));
            var sm = Component("secrets", "AWS Secrets Manager", "Senhas de banco, chaves de API, credenciais SMTP",
                inCode ? "senhas no web.config/app.config e credenciais embutidas no código" : "senhas no web.config/app.config",
                "Injeção na task definition (valueFrom) sem passar pela imagem; rotação automática para RDS.", notes: "Combine com IAM task roles: nenhuma access key no código.");
            foreach (var (pr, p) in projects.Where(p => p.Profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode) || p.Profile.Databases.Count > 0)) sm.UsedBy.Add(pr.Project.Name);
        }

        // Scheduling
        var scheduledProjects = projects.Where(p => p.Result.Hosting?.Primary == AwsHosting.EcsScheduledTask || p.Profile.HasAny(Signal.Scheduler, Signal.TimerLoop)).ToList();
        if (scheduledProjects.Count > 0)
        {
            var eb = Component("eventbridge", "Amazon EventBridge Scheduler", "Agendamento de tarefas (cron) que disparam tarefas ECS ou Lambdas", "Timers em Windows Services / Quartz / Task Scheduler", "Cron gerenciado com retry, DLQ e histórico; a task roda só quando necessário.", notes: "Para jobs que precisam de lock, o Scheduler já garante uma execução por horário.");
            foreach (var (pr, _) in scheduledProjects) eb.UsedBy.Add(pr.Project.Name);
        }

        // Identity
        var adProjects = projects.Where(p => p.Profile.HasAny(Signal.WindowsAuth, Signal.ActiveDirectory)).ToList();
        var loginProjects = projects.Where(p => p.Result.Project.Kind == ProjectKind.Web && p.Profile.HasAny(Signal.FormsAuth, Signal.Membership, Signal.Identity2, Signal.OwinOAuth)).ToList();
        if (adProjects.Count > 0)
        {
            var ad = Component("cognito", "Amazon Cognito (federado ao AD/Entra ID) ou AWS Managed Microsoft AD", "Autenticação de usuários corporativos", "Autenticação Windows / LDAP", "Cognito com OIDC/SAML federado substitui Kerberos; Managed AD + gMSA mantém Windows Auth em containers Windows quando a reescrita não é viável.", required: true);
            foreach (var (pr, _) in adProjects) ad.UsedBy.Add(pr.Project.Name);
        }
        else if (loginProjects.Count > 0)
        {
            var cognito = Component("cognito", "Amazon Cognito", "Login, MFA, recuperação de senha, federação", "Forms Authentication / Membership / Identity 2", "Terceiriza o ciclo de vida de usuários; a aplicação só valida tokens/cookies OIDC. Opcional: ASP.NET Core Identity no RDS mantém tudo na aplicação.", required: false);
            foreach (var (pr, _) in loginProjects) cognito.UsedBy.Add(pr.Project.Name);
        }

        // Hybrid connectivity
        var internalHosts = projects.SelectMany(p => p.Profile.ExternalEndpoints.Select(HostOf).Concat(p.Profile.Databases.Select(d => (d.Server ?? "").Split(',')[0].Split('\\')[0])))
            .Where(h => ModernizationAdvisor.IsInternalHost(h) && !h.Contains("localdb", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var nonDbInternal = projects.SelectMany(p => p.Profile.ExternalEndpoints.Select(HostOf)).Where(ModernizationAdvisor.IsInternalHost).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (internalHosts.Count > 0)
        {
            var vpn = Component("vpn", "AWS Site-to-Site VPN ou Direct Connect + Route 53 Resolver", "Conectividade com a rede on-premises", "acesso direto na LAN a " + string.Join(", ", internalHosts.Take(5)),
                nonDbInternal.Count > 0 ? "Integrações internas (ERP, WCF, serviços) continuam on-premises durante e após a migração; o DNS interno precisa ser resolvível da VPC." : "Necessária durante a migração dos bancos (DMS) e para qualquer sistema que permaneça on-premises.",
                required: nonDbInternal.Count > 0, notes: "Direct Connect para latência previsível se o volume for alto; VPN para começar.");
            foreach (var (pr, p) in projects.Where(p => p.Profile.ExternalEndpoints.Select(HostOf).Any(ModernizationAdvisor.IsInternalHost) || p.Profile.Databases.Any(d => ModernizationAdvisor.IsInternalHost((d.Server ?? "").Split(',')[0].Split('\\')[0]))))
                vpn.UsedBy.Add(pr.Project.Name);
        }

        // CI/CD + IaC are always part of the proposal
        if (deployables.Count > 0)
        {
            var cicd = Component("cicd", "GitHub Actions / AWS CodePipeline + CodeBuild", "Build da imagem, testes, push ao ECR e deploy no ECS", "publicação manual / MSDeploy", "Pipeline padronizado para as aplicações do portfólio; deploy rolling ou blue/green (CodeDeploy) com rollback automático por alarme.", notes: "Infraestrutura como código: AWS CDK (C#) ou Terraform; um template reutilizado por aplicação.");
            foreach (var (pr, _) in deployables) cicd.UsedBy.Add(pr.Project.Name);
        }
        if (anyContainer)
            Component("vpc", "Amazon VPC (subnets privadas, NAT Gateway, VPC endpoints)", "Rede", "rede do datacenter", "Tasks em subnets privadas; VPC endpoints para S3, ECR, CloudWatch e Secrets Manager evitam custo de NAT e saída pela internet.", notes: "NAT Gateway é um custo fixo relevante: use endpoints para serviços AWS.");

        proposal.Components.AddRange(components.Values.OrderBy(c => c.Required ? 0 : 1).ThenBy(c => Order(c.Id)));
        proposal.Summary = Summarize(result, allProjects, deployables);
        BuildPhases(proposal, projects, deployables, components);
        BuildRisks(proposal, projects, deployables, allProjects);
        BuildCostNotes(proposal, deployables, components);
        proposal.Diagram = Mermaid(result, deployables, components);
        return proposal;
    }

    private static int Order(string id) => id switch
    {
        "vpc" => 0, "alb" => 1, "cloudfront" => 2, "route53" => 3, "ecs" => 4, "lambda" => 4, "ec2" => 5, "ecr" => 6, "eventbridge" => 7, "s3-events" => 7, "ses-inbound" => 7, "storage-gateway" => 16, "sqs" => 8, "amazonmq" => 9, "msk" => 10,
        _ when id.StartsWith("rds") => 11, "documentdb" => 12, "opensearch" => 13, "elasticache" => 14, "s3" => 15, "efs" => 16, "transfer" => 17, "ses" => 18,
        "cognito" => 19, "secrets" => 20, "ssm" => 21, "cloudwatch" => 22, "vpn" => 23, "cicd" => 24, _ => 50
    };

    private static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    private static string Summarize(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> projects, List<(ProjectResult Result, ApplicationProfile Profile)> deployables)
    {
        var sb = new StringBuilder();
        var kinds = projects.GroupBy(p => p.Result.Project.Kind).Select(g => $"{g.Count()} {KindPlural(g.Key, g.Count())}");
        sb.Append($"{result.SolutionName} tem {projects.Count} projeto(s): {string.Join(", ", kinds)}. ");
        foreach (var (pr, p) in deployables)
        {
            var traits = new List<string>();
            if (pr.Project.Kind == ProjectKind.Web)
            {
                if (p.MvcControllerCount > 0) traits.Add($"{p.MvcControllerCount} controller(s) MVC e {p.ViewCount} view(s)");
                if (p.ApiControllerCount > 0) traits.Add($"{p.ApiControllerCount} controller(s) de API");
                if (p.Has(Signal.FormsAuth)) traits.Add("login por Forms Authentication");
                if (p.Has(Signal.WindowsAuth)) traits.Add("autenticação Windows");
                if (p.Has(Signal.InProcSession)) traits.Add("sessão em memória");
                if (p.Has(Signal.FileUploads)) traits.Add("upload de arquivos");
                if (p.Has(Signal.SignalR)) traits.Add("SignalR");
                if (p.Has(Signal.WebForms)) traits.Add($"{p.Count(Signal.WebForms)} arquivo(s) Web Forms");
            }
            if (pr.Project.IsVisualBasic) traits.Add("VB.NET (não convertido)");
            else
            {
                if (p.Has(Signal.WindowsServiceHost)) traits.Add("Windows Service");
                if (p.Has(Signal.TimerLoop)) traits.Add("loop com timer");
                if (p.Has(Signal.Scheduler)) traits.Add("agendador (" + string.Join("/", p.Get(Signal.Scheduler)!.Details.Take(2)) + ")");
                if (p.Has(Signal.Msmq)) traits.Add("consome MSMQ");
                if (p.Has(Signal.RabbitMq)) traits.Add("consome RabbitMQ");
                if (p.Has(Signal.MailboxReading)) traits.Add("lê caixa de e-mail (" + string.Join("/", p.Get(Signal.MailboxReading)!.Details.Take(2).DefaultIfEmpty("IMAP/EWS")) + ")");
                if (p.Has(Signal.FileWatcher)) traits.Add("monitora pasta de entrada");
                if (p.Has(Signal.SpreadsheetFiles)) traits.Add("processa planilhas/CSV");
            }
            if (p.Databases.Count > 0) traits.Add("acessa " + string.Join(", ", p.Databases.Select(d => $"{d.Provider} ({d.Database ?? d.Name})").Distinct().Take(3)));
            if (p.Has(Signal.Smtp)) traits.Add("envia e-mail por SMTP");
            if (p.Has(Signal.WcfClient)) traits.Add("consome serviços WCF/SOAP");
            if (p.Has(Signal.FileSystemWrites) || p.Has(Signal.AppDataFolder)) traits.Add("grava arquivos locais");
            if (p.MergedFrom.Count > 0) traits.Add($"inclui {string.Join(", ", p.MergedFrom)}");
            sb.Append($"{pr.Project.Name} ({ReportKind(pr.Project.Kind)}): {(traits.Count > 0 ? string.Join("; ", traits) : "sem integrações externas detectadas")} → {pr.Hosting!.Primary.Display()}. ");
        }
        return sb.ToString().TrimEnd();
    }

    private static string KindPlural(ProjectKind kind, int n) => kind switch
    {
        ProjectKind.Web => n == 1 ? "aplicação web" : "aplicações web",
        ProjectKind.WindowsService => n == 1 ? "Windows Service" : "Windows Services",
        ProjectKind.Console => n == 1 ? "console" : "consoles",
        ProjectKind.Desktop => "desktop",
        ProjectKind.Test => n == 1 ? "projeto de testes" : "projetos de testes",
        _ => n == 1 ? "biblioteca" : "bibliotecas"
    };

    private static string ReportKind(ProjectKind kind) => kind switch
    {
        ProjectKind.Web => "web", ProjectKind.WindowsService => "serviço", ProjectKind.Console => "console", ProjectKind.Desktop => "desktop", ProjectKind.Test => "testes", _ => "biblioteca"
    };

    private static void BuildPhases(ArchitectureProposal proposal, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> projects, List<(ProjectResult Result, ApplicationProfile Profile)> deployables, Dictionary<string, AwsComponent> components)
    {
        var p = proposal.Phases;
        p.Add("1. Fundação (uma vez para o portfólio): landing zone/contas, VPC com subnets privadas e VPC endpoints, cluster ECS, ECR, CloudWatch, Secrets Manager/Parameter Store, pipeline de CI/CD e templates de IaC (CDK/Terraform)." + (components.ContainsKey("vpn") ? " Conectividade híbrida (VPN/Direct Connect) e Route 53 Resolver para o DNS interno." : ""));
        if (components.Keys.Any(k => k.StartsWith("rds")))
            p.Add("2. Dados: provisionar o RDS, migrar com restore nativo (.bak via S3) ou AWS DMS com replicação contínua; trocar Integrated Security por autenticação SQL com segredo no Secrets Manager; validar Encrypt/TLS e collation.");
        p.Add($"{(components.Keys.Any(k => k.StartsWith("rds")) ? 3 : 2)}. Aplicação: compilar a saída do Migrator, resolver os itens bloqueantes do inventário, construir a imagem com o Dockerfile gerado, externalizar estado (sessão → ElastiCache, Data Protection → SSM, arquivos → S3), logs em stdout, segredos via task definition. Rodar os testes no pipeline.");
        var integrations = new List<string>();
        if (projects.Any(x => x.Profile.Has(Signal.MailboxReading))) integrations.Add("leitura de caixa postal → SES recebimento ou Microsoft Graph (EWS em desligamento)");
        if (projects.Any(x => x.Profile.Has(Signal.FileWatcher))) integrations.Add("monitoramento de pasta → S3 Event Notifications (File Gateway para as pastas de rede)");
        if (projects.Any(x => x.Profile.Has(Signal.Msmq))) integrations.Add("MSMQ → SQS");
        if (projects.Any(x => x.Profile.Has(Signal.Smtp))) integrations.Add("SMTP → SES");
        if (projects.Any(x => x.Profile.HasAny(Signal.Scheduler, Signal.TimerLoop))) integrations.Add("timers → EventBridge Scheduler");
        if (projects.Any(x => x.Profile.HasAny(Signal.FileSystemWrites, Signal.AppDataFolder, Signal.UncPaths, Signal.FileUploads))) integrations.Add("arquivos → S3");
        if (projects.Any(x => x.Profile.HasAny(Signal.WindowsAuth, Signal.ActiveDirectory))) integrations.Add("Windows Auth → Cognito/OIDC");
        var n = components.Keys.Any(k => k.StartsWith("rds")) ? 4 : 3;
        if (integrations.Count > 0) p.Add($"{n++}. Integrações: {string.Join(", ", integrations)}; testes de contrato com os sistemas on-premises via VPN.");
        p.Add($"{n++}. Cutover: deploy em homologação na AWS com dados reais (cópia), testes de carga para dimensionar as tasks, Route 53 com roteamento ponderado (10% → 100%), alarmes e runbook de rollback (voltar o peso do DNS).");
        p.Add($"{n}. Modernização contínua (pós-migração): itens de Licença/Modernização desta lista (AutoMapper/MediatR, EF Core, System.Text.Json), redução de licença de SQL Server (Aurora PostgreSQL/Babelfish) e remoção das dependências Windows restantes para padronizar tudo em Fargate Linux.");
    }

    private static void BuildRisks(ArchitectureProposal proposal, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> projects, List<(ProjectResult Result, ApplicationProfile Profile)> deployables, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> allProjects)
    {
        var r = proposal.Risks;
        foreach (var (pr, _) in deployables.Where(d => d.Result.Hosting!.RequiresWindows))
            r.Add($"{pr.Project.Name} exige Windows ({string.Join("; ", pr.Hosting!.HardWindowsDependencies)}): custo ~2x e pipeline separado até remover a dependência.");
        if (projects.Any(p => p.Profile.Databases.Any(d => d.IntegratedSecurity && !(d.Server ?? "").Contains("localdb", StringComparison.OrdinalIgnoreCase))))
            r.Add("Connection strings com Integrated Security: o Fargate não participa do domínio; é preciso trocar para autenticação SQL (ou Managed AD) antes do primeiro deploy.");
        if (projects.Any(p => p.Profile.HasAny(Signal.DateTimeNow, Signal.FormsAuth) || p.Profile.Culture != null))
            r.Add("Fuso horário e cultura: containers rodam em UTC e, sem LANG, em cultura invariante; datas e formatação de números mudam. O Dockerfile gerado define TZ/LANG, mas o código deveria usar UTC e culturas explícitas.");
        if (projects.Any(p => p.Profile.Has(Signal.WcfClient) || p.Profile.ExternalEndpoints.Any(e => ModernizationAdvisor.IsInternalHost(HostOf(e)))))
            r.Add("Integrações on-premises via VPN adicionam latência (10-40 ms por chamada); chamadas em loop (N+1) que eram imperceptíveis na LAN podem ficar lentas. Meça com X-Ray e agrupe chamadas.");
        if (projects.Any(p => p.Profile.Has(Signal.InProcSession) || p.Profile.Has(Signal.StaticState)))
            r.Add("Estado em memória (sessão/coleções estáticas): qualquer teste com uma única task passa e falha em produção com duas. Teste com desired count ≥ 2 desde homologação.");
        if (projects.Any(p => p.Profile.Has(Signal.WcfHost) || p.Profile.Has(Signal.Asmx)))
            r.Add("Serviços WCF/ASMX hospedados: precisam de CoreWCF ou reescrita; clientes externos dependem do contrato SOAP atual.");
        if (projects.Any(p => p.Profile.Has(Signal.Edmx)))
            r.Add("Modelos EDMX: o EF Designer não funciona em projetos SDK-style; alterações de modelo exigem scaffold para EF Core.");
        if (projects.Any(p => p.Profile.HasAny(Signal.LargeUploads, Signal.LongRequests)))
            r.Add("Uploads grandes/requisições longas: ALB idle timeout de 60 s e memória da task; usar URLs pré-assinadas do S3 e processamento assíncrono.");
        if (projects.Any(p => p.Profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode)))
            r.Add("Segredos em texto claro" + (projects.Any(p => p.Profile.Has(Signal.SecretsInCode)) ? " (inclusive embutidos no código C#)" : "") + " iriam parar na imagem Docker e no repositório: bloqueie no pipeline (git-secrets/trufflehog), mova para o Secrets Manager antes do primeiro build e rotacione o que já vazou.");
        var licenses = allProjects.SelectMany(p => p.Result.Modernizations).Where(m => m.Kind == ModernizationKind.License).Select(m => m.Title).Distinct().ToList();
        if (licenses.Count > 0) r.Add("Bibliotecas que viraram pagas (" + licenses.Count + "): decidir entre pagar, congelar versão ou substituir antes de escalar para as demais aplicações do portfólio.");
    }

    private static void BuildCostNotes(ArchitectureProposal proposal, List<(ProjectResult Result, ApplicationProfile Profile)> deployables, Dictionary<string, AwsComponent> components)
    {
        var c = proposal.CostNotes;
        c.Add("Fargate Linux: comece com 0,5 vCPU / 1 GB por task e 2 tasks por serviço web (alta disponibilidade); Fargate Spot para workers tolerantes a interrupção reduz ~70%.");
        if (deployables.Any(d => d.Result.Hosting!.Primary is AwsHosting.EcsWindows or AwsHosting.Ec2Windows))
            c.Add("Containers/EC2 Windows: licença do Windows embutida (~2x o preço do Linux), sem Spot no Fargate Windows e imagens de 5-10 GB (deploys lentos). Remover as dependências Windows é a maior alavanca de custo.");
        if (components.ContainsKey("rds-sqlserver"))
            c.Add("RDS for SQL Server: a licença inclusa domina o custo (Standard ≈ 2-3x o preço de um RDS PostgreSQL equivalente). Vários bancos pequenos cabem numa instância; Express é gratuito em licença até 10 GB por banco; Babelfish for Aurora PostgreSQL elimina a licença mantendo T-SQL.");
        if (deployables.Any(d => d.Result.Hosting!.Primary == AwsHosting.EcsScheduledTask))
            c.Add("Tarefas agendadas pagam só o tempo de execução (em vez de um Windows Service 24x7).");
        if (deployables.Any(d => d.Result.Hosting!.Primary == AwsHosting.Lambda))
            c.Add("Lambda: automações que processam poucos milhares de eventos por mês costumam caber no nível gratuito (1 M de invocações e 400 mil GB-s); o custo relevante passa a ser o S3/SES e o RDS.");
        c.Add("NAT Gateway tem custo fixo por hora + por GB; VPC endpoints para S3/ECR/CloudWatch/Secrets Manager evitam a maior parte do tráfego.");
        c.Add("CloudWatch Logs: defina retenção e evite logs em nível Debug em produção; o custo de ingestão (por GB) surpreende em aplicações verbosas.");
        if (components.ContainsKey("elasticache")) c.Add("ElastiCache Serverless cobra por GB-hora e requisições — adequado para sessão/cache de aplicações pequenas; um único cluster pode servir várias aplicações (separe por prefixo/DB).");
    }

    private static string Mermaid(SolutionResult result, List<(ProjectResult Result, ApplicationProfile Profile)> deployables, Dictionary<string, AwsComponent> components)
    {
        var sb = new StringBuilder();
        sb.AppendLine("flowchart LR");
        sb.AppendLine("    users([Usuários])");
        if (components.ContainsKey("cloudfront")) sb.AppendLine("    cf[CloudFront]");
        if (components.ContainsKey("alb")) sb.AppendLine("    alb[Application Load Balancer]");

        sb.AppendLine("    subgraph aws[AWS - VPC]");
        sb.AppendLine("        direction LR");
        var containerized = deployables.Where(d => d.Result.Hosting!.Primary != AwsHosting.Lambda).ToList();
        foreach (var (pr, _) in deployables.Where(d => d.Result.Hosting!.Primary == AwsHosting.Lambda))
            sb.AppendLine($"        {Id(pr.Project.Name)}[\"{pr.Project.Name}<br/>Lambda\"]");
        if (containerized.Count > 0)
        {
            sb.AppendLine("        subgraph ecs[Amazon ECS]");
            foreach (var (pr, _) in containerized)
            {
                var label = pr.Hosting!.Primary switch
                {
                    AwsHosting.EcsFargate => $"{pr.Project.Name}<br/>Fargate service",
                    AwsHosting.EcsWindows => $"{pr.Project.Name}<br/>Windows container",
                    AwsHosting.EcsFargateWorker => $"{pr.Project.Name}<br/>Fargate worker",
                    AwsHosting.EcsScheduledTask => $"{pr.Project.Name}<br/>Fargate scheduled task",
                    AwsHosting.Ec2Windows => $"{pr.Project.Name}<br/>EC2 Windows",
                    AwsHosting.Lambda => $"{pr.Project.Name}<br/>Lambda",
                    _ => pr.Project.Name
                };
                sb.AppendLine($"            {Id(pr.Project.Name)}[\"{label}\"]");
            }
            sb.AppendLine("        end");
        }
        foreach (var id in new[] { "rds-sqlserver", "rds-oracle", "rds-mysql", "rds-postgres", "rds-other", "documentdb", "opensearch", "elasticache", "efs" }.Where(components.ContainsKey))
            sb.AppendLine($"        {Id(id)}[({ShortName(components[id])})]");
        sb.AppendLine("    end");
        foreach (var id in new[] { "s3", "s3-events", "storage-gateway", "sqs", "amazonmq", "msk", "ses", "ses-inbound", "eventbridge", "secrets", "ssm", "cloudwatch", "cognito", "transfer" }.Where(components.ContainsKey))
            sb.AppendLine($"    {Id(id)}[{ShortName(components[id])}]");
        if (components.ContainsKey("vpn"))
        {
            sb.AppendLine("    subgraph onprem[On-premises]");
            var hosts = deployables.SelectMany(d => d.Profile.ExternalEndpoints.Select(HostOf)).Where(ModernizationAdvisor.IsInternalHost).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            if (hosts.Count == 0) hosts.Add("sistemas internos");
            foreach (var h in hosts) sb.AppendLine($"        {Id("op_" + h)}[{h}]");
            sb.AppendLine("    end");
        }

        // Edges
        var webs = deployables.Where(d => d.Result.Project.Kind == ProjectKind.Web).ToList();
        if (components.ContainsKey("cloudfront")) { sb.AppendLine("    users --> cf --> alb"); }
        else if (components.ContainsKey("alb")) sb.AppendLine("    users --> alb");
        foreach (var (pr, _) in webs) sb.AppendLine($"    alb --> {Id(pr.Project.Name)}");
        foreach (var (pr, p) in deployables)
        {
            var node = Id(pr.Project.Name);
            foreach (var db in p.Databases.Select(d => d.Provider).Distinct())
            {
                var id = db switch { "SQL Server" => "rds-sqlserver", "Oracle" => "rds-oracle", "MySQL" => "rds-mysql", "PostgreSQL" => "rds-postgres", "SQLite" => null, _ => "rds-other" };
                if (id != null && components.ContainsKey(id)) sb.AppendLine($"    {node} --> {Id(id)}");
            }
            if (components.ContainsKey("s3") && p.HasAny(Signal.FileSystemWrites, Signal.AppDataFolder, Signal.UncPaths, Signal.WindowsPaths, Signal.FileUploads, Signal.AzureStorage, Signal.Ftp)) sb.AppendLine($"    {node} --> s3");
            if (components.ContainsKey("efs") && p.Has(Signal.UncPaths)) sb.AppendLine($"    {node} -.-> efs");
            if (components.ContainsKey("ses") && p.Has(Signal.Smtp)) sb.AppendLine($"    {node} --> ses");
            if (components.ContainsKey("elasticache") && (pr.Project.Kind == ProjectKind.Web && p.HasAny(Signal.InProcSession, Signal.ExternalSession, Signal.LocalCache, Signal.SignalR, Signal.StaticState) || p.Has(Signal.Redis))) sb.AppendLine($"    {node} --> elasticache");
            if (components.ContainsKey("amazonmq") && p.Has(Signal.RabbitMq)) sb.AppendLine($"    {node} <--> amazonmq");
            if (components.ContainsKey("msk") && p.Has(Signal.Kafka)) sb.AppendLine($"    {node} <--> msk");
            if (components.ContainsKey("sqs"))
            {
                if (pr.Project.Kind == ProjectKind.Web) sb.AppendLine($"    {node} -- publica --> sqs");
                else if (pr.Hosting!.Primary == AwsHosting.EcsFargateWorker || p.HasAny(Signal.Msmq, Signal.MessageBusFramework)) sb.AppendLine($"    sqs -- consome --> {node}");
            }
            if (components.ContainsKey("eventbridge") && (pr.Hosting!.Primary == AwsHosting.EcsScheduledTask || (pr.Hosting!.Primary != AwsHosting.Lambda && p.HasAny(Signal.Scheduler, Signal.TimerLoop)))) sb.AppendLine($"    eventbridge -- agenda --> {node}");
            if (components.ContainsKey("s3-events") && pr.Project.Kind != ProjectKind.Web && (p.Has(Signal.FileWatcher) || pr.Hosting!.Primary == AwsHosting.Lambda || p.Has(Signal.SpreadsheetFiles)))
            {
                sb.AppendLine($"    s3 -- ObjectCreated --> s3_events -- dispara --> {node}");
                if (components.ContainsKey("storage-gateway") && p.HasAny(Signal.UncPaths, Signal.WindowsPaths)) sb.AppendLine($"    storage_gateway -- SMB → S3 --> s3");
            }
            if (components.ContainsKey("ses-inbound") && p.Has(Signal.MailboxReading)) sb.AppendLine($"    ses_inbound -- e-mail recebido --> {node}");
            if (components.ContainsKey("cognito") && pr.Project.Kind == ProjectKind.Web && p.HasAny(Signal.FormsAuth, Signal.Membership, Signal.Identity2, Signal.OwinOAuth, Signal.WindowsAuth, Signal.ActiveDirectory)) sb.AppendLine($"    {node} -.-> cognito");
            if (components.ContainsKey("vpn"))
                foreach (var h in p.ExternalEndpoints.Select(HostOf).Where(ModernizationAdvisor.IsInternalHost).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
                    sb.AppendLine($"    {node} -. VPN .-> {Id("op_" + h)}");
        }
        if (components.ContainsKey("secrets") && deployables.Count > 0) sb.AppendLine($"    secrets -.-> {Id(deployables[0].Result.Project.Name)}");
        if (components.ContainsKey("cloudwatch") && deployables.Count > 0) sb.AppendLine($"    {Id(deployables[0].Result.Project.Name)} -. logs .-> cloudwatch");
        return sb.ToString().TrimEnd();
    }

    private static string ShortName(AwsComponent c) => c.Id switch
    {
        "rds-sqlserver" => "RDS SQL Server", "rds-oracle" => "RDS Oracle", "rds-mysql" => "Aurora MySQL", "rds-postgres" => "Aurora PostgreSQL", "rds-other" => "RDS",
        "documentdb" => "DocumentDB", "opensearch" => "OpenSearch", "elasticache" => "ElastiCache", "efs" => "EFS", "s3" => "S3", "sqs" => "SQS / SNS", "amazonmq" => "Amazon MQ", "msk" => "MSK",
        "ses" => "SES", "ses-inbound" => "SES recebimento", "s3-events" => "S3 Events → SQS", "storage-gateway" => "File Gateway", "lambda" => "Lambda", "eventbridge" => "EventBridge Scheduler", "secrets" => "Secrets Manager", "ssm" => "Parameter Store", "cloudwatch" => "CloudWatch", "cognito" => "Cognito", "transfer" => "Transfer Family",
        _ => c.Service
    };

    private static string Id(string name) => new(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_').ToArray());

    // ---------------------------------------------------------------- Container artifacts

    public static string Dockerfile(ProjectInfo project, HostingRecommendation hosting, ApplicationProfile profile, string projectRelativePath, IReadOnlyList<string> dependencyRelativePaths)
    {
        var windows = hosting.Primary is AwsHosting.EcsWindows or AwsHosting.Ec2Windows;
        var isWeb = project.Kind == ProjectKind.Web;
        var unix = projectRelativePath.Replace('\\', '/');
        var projectDir = Path.GetDirectoryName(unix)?.Replace('\\', '/') ?? "";
        var assembly = string.IsNullOrEmpty(project.AssemblyName) ? project.Name : project.AssemblyName;
        var sdkTag = windows ? "10.0-windowsservercore-ltsc2022" : "10.0";
        var runtimeImage = isWeb ? "mcr.microsoft.com/dotnet/aspnet" : "mcr.microsoft.com/dotnet/runtime";
        var runtimeTag = windows ? "10.0-windowsservercore-ltsc2022" : "10.0";

        var sb = new StringBuilder();
        sb.AppendLine("# syntax=docker/dockerfile:1");
        sb.AppendLine($"# Gerado pelo Migrator para {project.Name} — destino: {hosting.Primary.Display()}.");
        sb.AppendLine($"# Build a partir da raiz da solução:  docker build -f {unix.Replace(Path.GetFileName(unix), "Dockerfile")} -t {Id(project.Name).ToLowerInvariant()} .");
        if (hosting.HardWindowsDependencies.Count > 0)
            sb.AppendLine($"# Imagem Windows: o projeto depende de {string.Join("; ", hosting.HardWindowsDependencies)}.");
        if (!windows && profile.Has(Signal.WindowsServiceHost))
            sb.AppendLine("# ATENÇÃO: o projeto ainda usa ServiceBase (Windows Service). Converta para BackgroundService (item MOD-WIN-SERVICE) antes de executar em Linux.");
        if (!windows && profile.Has(Signal.SystemDrawing))
            sb.AppendLine("# ATENÇÃO: System.Drawing lança PlatformNotSupportedException em Linux (item MOD-WIN-DRAWING).");
        sb.AppendLine();
        sb.AppendLine($"FROM mcr.microsoft.com/dotnet/sdk:{sdkTag} AS build");
        sb.AppendLine("WORKDIR /src");
        sb.AppendLine("# Restaura primeiro só os .csproj para aproveitar o cache de camadas");
        foreach (var path in dependencyRelativePaths.Append(projectRelativePath).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p, StringComparer.Ordinal))
        {
            var u = path.Replace('\\', '/');
            var dir = Path.GetDirectoryName(u)?.Replace('\\', '/') ?? "";
            sb.AppendLine($"COPY [\"{u}\", \"{(dir.Length == 0 ? "./" : dir + "/")}\"]");
        }
        sb.AppendLine("COPY [\"*.slnx\", \"NuGet.config*\", \"Directory.Build.*\", \"./\"]");
        sb.AppendLine($"RUN dotnet restore \"{unix}\"");
        sb.AppendLine("COPY . .");
        sb.AppendLine($"RUN dotnet publish \"{unix}\" -c Release -o /app/publish /p:UseAppHost=false");
        sb.AppendLine();
        sb.AppendLine($"FROM {runtimeImage}:{runtimeTag} AS final");
        sb.AppendLine("WORKDIR /app");
        if (!windows)
        {
            var env = new List<string>();
            if (isWeb) env.Add("ASPNETCORE_HTTP_PORTS=8080");
            env.Add("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false");
            // Without <globalization culture> we assume the portfolio default when the code depends on local time/culture.
            var culture = profile.Culture ?? (profile.Has(Signal.DateTimeNow) || profile.Has(Signal.WindowsTimeZoneIds) ? DefaultCulture : null);
            if (culture != null)
            {
                var posix = culture.Replace('-', '_') + ".UTF-8";
                env.Add($"LANG={posix}");
                env.Add($"LC_ALL={posix}");
            }
            if (culture?.StartsWith("pt-BR", StringComparison.OrdinalIgnoreCase) == true || profile.Has(Signal.DateTimeNow))
                env.Add("TZ=America/Sao_Paulo");
            sb.AppendLine("# Cultura/fuso explícitos: containers Linux rodam em UTC e, sem LANG, em cultura invariante (veja itens MOD-CS-DATETIME-NOW / MOD-CS-PARSE-CULTURE)");
            sb.AppendLine("ENV " + string.Join(" \\\n    ", env));
            if (isWeb) sb.AppendLine("EXPOSE 8080");
            sb.AppendLine("# Usuário não-root (definido na imagem base do .NET 8+)");
            sb.AppendLine("USER $APP_UID");
        }
        else
        {
            if (isWeb)
            {
                sb.AppendLine("ENV ASPNETCORE_HTTP_PORTS=8080");
                sb.AppendLine("EXPOSE 8080");
            }
        }
        sb.AppendLine("COPY --from=build /app/publish .");
        if (isWeb) sb.AppendLine("# Health check do ECS/ALB: GET /health (gerado no Program.cs)");
        sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"{assembly}.dll\"]");
        return sb.ToString().Replace("\r\n", "\n");
    }

    public static string DockerIgnore() =>
        """
        **/bin/
        **/obj/
        **/.vs/
        **/*.user
        **/_Legacy/
        **/_migration-report/
        _secrets/
        .git/
        .migrator-output
        **/Dockerfile
        **/.dockerignore
        """.Replace("\r\n", "\n");
}
