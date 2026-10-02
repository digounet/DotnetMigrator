using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Data;
using Migrator.Core.Models;

namespace Migrator.Core.Cloud;

/// <summary>
/// Produces <see cref="ModernizationItem"/>s for one project from three sources: package rules, C# code rules
/// and architectural signals from the <see cref="ApplicationProfile"/>.
/// </summary>
public static class ModernizationAdvisor
{
    public static List<ModernizationItem> Analyze(ProjectInfo project, ApplicationProfile profile, IEnumerable<(string Path, string Text)> code, CloudTarget cloud)
    {
        var items = new List<ModernizationItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(ModernizationItem item)
        {
            if (cloud == CloudTarget.None && item.Kind == ModernizationKind.Cloud) return;
            if (!seen.Add(item.RuleId + "|" + item.Evidence)) return;
            item.Project = project.Name;
            items.Add(item);
        }

        // 1. Packages
        foreach (var package in project.Packages.DistinctBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            var rule = ModernizationRules.Find(package.Id);
            if (rule == null) continue;
            if (items.Any(i => i.RuleId == rule.Id && i.Project == project.Name && i.Kind == rule.Kind && i.Title == rule.Title))
            {
                items.First(i => i.RuleId == rule.Id && i.Title == rule.Title).Evidence += ", " + package.Id;
                continue;
            }
            Add(new ModernizationItem
            {
                Kind = rule.Kind, RuleId = rule.Id, Title = rule.Title, Why = rule.Why, Proposal = rule.Proposal,
                Impact = rule.Impact, Effort = rule.Effort, Evidence = $"{package.Id} {package.Version}", AwsService = rule.AwsService
            });
        }

        // 2. C# code
        var hits = new Dictionary<string, (CodeModernizationRule Rule, int Count, List<string> Locations)>(StringComparer.Ordinal);
        foreach (var (path, text) in code)
        {
            LineIndex? lines = null;
            foreach (var rule in CodeModernizationRules.CSharp)
            {
                if (rule.OnlyKinds != null && !rule.OnlyKinds.Contains(project.Kind)) continue;
                if (rule.FileFilter != null && !rule.FileFilter.IsMatch(text)) continue;
                foreach (Match match in rule.Regex.Matches(text))
                {
                    lines ??= new LineIndex(text);
                    var (line, content) = lines.Locate(match.Index);
                    if (IsComment(content)) continue;
                    if (!hits.TryGetValue(rule.Id, out var hit)) hit = (rule, 0, []);
                    hit.Count++;
                    if (hit.Locations.Count < 5) hit.Locations.Add($"{path}:{line}");
                    hits[rule.Id] = hit;
                }
            }
        }
        foreach (var (rule, count, locations) in hits.Values.OrderBy(h => h.Rule.Id, StringComparer.Ordinal))
            Add(new ModernizationItem
            {
                Kind = rule.Kind, RuleId = rule.Id, Title = rule.Title, Why = rule.Why, Proposal = rule.Proposal,
                Impact = rule.Impact, Effort = rule.Effort, Occurrences = count, AwsService = rule.AwsService,
                Evidence = string.Join(", ", locations) + (count > locations.Count ? $" (+{count - locations.Count})" : "")
            });

        // 3. Architectural signals (only what packages/code rules above did not already cover)
        foreach (var item in FromProfile(project, profile)) Add(item);

        return items;
    }

    private static IEnumerable<ModernizationItem> FromProfile(ProjectInfo project, ApplicationProfile p)
    {
        static string Where(SignalEvidence e) => string.Join(", ", e.Locations.Concat(e.Details).Distinct().Take(6));
        ModernizationItem Item(string id, ModernizationKind kind, Impact impact, Effort effort, string title, string why, string proposal, SignalEvidence evidence, string? aws = null) =>
            new() { RuleId = id, Kind = kind, Impact = impact, Effort = effort, Title = title, Why = why, Proposal = proposal, Evidence = Where(evidence), Occurrences = evidence.Count, AwsService = aws };

        var hosted = project.Kind is ProjectKind.Web or ProjectKind.WindowsService or ProjectKind.Console or ProjectKind.ClassLibrary;

        if (p.Get(Signal.Msmq) is { } msmq)
            yield return Item("MOD-ARCH-MSMQ", ModernizationKind.Cloud, Impact.High, Effort.High,
                "MSMQ → Amazon SQS",
                "System.Messaging não existe no .NET 10 e o MSMQ é um componente do Windows: não existe no Linux nem como serviço gerenciado na AWS.",
                "Filas → Amazon SQS (FIFO quando a ordem importa; transacional vira padrão outbox). Publicação/consumo com AWS.Messaging (handlers + DI) ou AWSSDK.SQS; consumidores viram BackgroundService no ECS ou Lambda com SQS trigger.",
                msmq, "Amazon SQS");

        var storage = new[] { Signal.FileSystemWrites, Signal.AppDataFolder, Signal.UncPaths, Signal.WindowsPaths, Signal.FileUploads }
            .Select(p.Get).Where(e => e != null).Select(e => e!).ToList();
        if (hosted && storage.Count > 0)
        {
            var merged = new SignalEvidence { Signal = Signal.FileSystemWrites };
            foreach (var e in storage) merged.Merge(e);
            var hasUnc = p.Has(Signal.UncPaths);
            yield return Item("MOD-ARCH-FILES", ModernizationKind.Cloud, Impact.High, Effort.Medium,
                "Arquivos no disco local/compartilhamento → Amazon S3",
                "O disco de um container é efêmero e não é compartilhado entre instâncias: uploads, exportações, App_Data e pastas de rede (UNC) deixam de funcionar ou somem no próximo deploy." +
                (hasUnc ? " Compartilhamentos SMB (\\\\servidor\\pasta) não são acessíveis do Fargate sem Amazon FSx/EFS." : ""),
                "Grave e leia via AWSSDK.S3 (um bucket por aplicação, prefixos por tipo); uploads grandes direto do navegador com URL pré-assinada; arquivos servidos via CloudFront. " +
                (hasUnc ? "Se for lift-and-shift, monte um volume Amazon EFS na task do ECS (ou FSx for Windows File Server quando outros sistemas Windows também usam a pasta)." : "Para cache temporário, use /tmp (até 20 GB de storage efêmero no Fargate)."),
                merged, "Amazon S3");
        }

        if (p.Get(Signal.Ftp) is { } ftp)
            yield return Item("MOD-ARCH-FTP", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
                "FTP → AWS Transfer Family / S3",
                "Servidores FTP próprios exigem EC2 e portas abertas; na AWS a troca de arquivos com parceiros é feita com SFTP gerenciado sobre o S3.",
                "AWS Transfer Family (SFTP/FTPS) com o bucket S3 como backend; a aplicação passa a ler/gravar no S3 e um evento S3 → Lambda/SQS dispara o processamento.",
                ftp, "AWS Transfer Family");

        if (p.Get(Signal.Smtp) is { } smtp && !p.Packages.Contains("MailKit") && !p.Packages.Contains("SendGrid"))
            yield return Item("MOD-ARCH-SMTP", ModernizationKind.Cloud, Impact.Medium, Effort.Low,
                "Envio de e-mail → Amazon SES",
                "O servidor SMTP interno fica fora da VPC (ou some com a migração) e SmtpClient está obsoleto (sem suporte a autenticação moderna).",
                "Amazon SES: endpoint SMTP (email-smtp.<região>.amazonaws.com, porta 587 STARTTLS) com MailKit, ou API via AWSSDK.SimpleEmailV2. Verifique o domínio remetente (DKIM/SPF) e saia do sandbox do SES antes do go-live.",
                smtp, "Amazon SES");

        if (project.Kind == ProjectKind.Web && p.Get(Signal.InProcSession) is { } session)
            yield return Item("MOD-ARCH-SESSION", ModernizationKind.Cloud, Impact.High, Effort.Medium,
                "Sessão InProc → cache distribuído (ElastiCache)",
                "Com mais de uma task no ECS (ou a cada deploy) a sessão em memória se perde e o usuário é deslogado/perde o carrinho. Sticky sessions no ALB só mascaram o problema.",
                "Program.cs: troque AddDistributedMemoryCache por AddStackExchangeRedisCache apontando para o Amazon ElastiCache (Valkey/Redis OSS, TLS). Objetos em Session[] precisam ser serializados (JSON) — veja os itens WEB003 do inventário.",
                session, "Amazon ElastiCache");

        if (project.Kind == ProjectKind.Web && p.HasAny(Signal.FormsAuth, Signal.InProcSession, Signal.ExternalSession, Signal.MachineKey) || (project.Kind == ProjectKind.Web && p.ViewCount > 0))
        {
            var evidence = p.Get(Signal.FormsAuth) ?? p.Get(Signal.InProcSession) ?? p.Get(Signal.MachineKey) ?? new SignalEvidence { Signal = Signal.MachineKey, Count = 1 };
            yield return Item("MOD-ARCH-DATAPROTECTION", ModernizationKind.Cloud, Impact.High, Effort.Low,
                "Chaves do Data Protection compartilhadas entre instâncias",
                "Cookies de autenticação, antiforgery e sessão são protegidos pelo Data Protection; cada container gera chaves próprias (o equivalente ao machineKey do web.config), então um cookie emitido por uma task é inválido na outra e todos são invalidados a cada deploy.",
                "Persista o anel de chaves fora do container: pacote Amazon.AspNetCore.DataProtection.SSM (PersistKeysToAWSSystemsManager) ou S3 + KMS (AspNetCore.DataProtection.Aws.S3), e defina SetApplicationName.",
                evidence, "AWS Systems Manager Parameter Store");
        }

        if (project.Kind == ProjectKind.Web && p.Get(Signal.LocalCache) is { } cache && !p.Packages.Contains("Microsoft.Extensions.Caching.Memory"))
            yield return Item("MOD-ARCH-CACHE", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
                "Cache em memória com várias instâncias",
                "HttpRuntime.Cache/MemoryCache são por processo: cada task do ECS tem uma cópia e invalidações não se propagam.",
                "Dados pouco voláteis: IMemoryCache com expiração curta. Dados compartilhados/invalidados: IDistributedCache ou HybridCache com Amazon ElastiCache.",
                cache, "Amazon ElastiCache");

        if (p.Get(Signal.WindowsServiceHost) is { } svc)
            yield return Item("MOD-WIN-SERVICE", ModernizationKind.Cloud, Impact.High, Effort.Medium,
                "Windows Service → Worker Service (BackgroundService)",
                "ServiceBase/ServiceInstaller só existem no Windows; no ECS o processo precisa responder a SIGTERM e expor logs em stdout. Enquanto usar ServiceBase o projeto só roda em containers Windows ou EC2.",
                "Host.CreateApplicationBuilder(args) + classe : BackgroundService (ExecuteAsync com CancellationToken); AddWindowsService() apenas se também for instalado on-premises. Thread.Sleep/Timer → PeriodicTimer; o Dockerfile gerado já assume o executável como entrypoint.",
                svc, "Amazon ECS");

        if (hosted && p.Get(Signal.SystemDrawing) is { } drawing)
            yield return Item("MOD-WIN-DRAWING", ModernizationKind.Cloud, Impact.High, Effort.Medium,
                "System.Drawing não funciona em Linux",
                "Desde o .NET 6 System.Drawing.Common lança PlatformNotSupportedException fora do Windows (depende do GDI+). Enquanto existir, o projeto precisa de container Windows.",
                "Imagens/thumbnails: SkiaSharp (MIT) ou ImageSharp; gráficos: ScottPlot/SkiaSharp; códigos de barras/QR: ZXing.Net com SkiaSharp. Se for só Color/Point/Size, referencie System.Drawing.Primitives.",
                drawing, "Amazon ECS (Linux)");

        if (hosted && p.Get(Signal.EventLog) is { } eventLog)
            yield return Item("MOD-WIN-EVENTLOG", ModernizationKind.Cloud, Impact.Medium, Effort.Low,
                "Event Log do Windows → CloudWatch Logs",
                "Não existe Event Log no Linux; o pacote System.Diagnostics.EventLog só funciona no Windows.",
                "Substitua por ILogger (stdout → CloudWatch Logs). Para alertas, crie métricas de filtro e alarmes no CloudWatch a partir dos logs.",
                eventLog, "Amazon CloudWatch Logs");

        if (hosted && p.Get(Signal.PerformanceCounter) is { } perf)
            yield return Item("MOD-WIN-PERFCOUNTER", ModernizationKind.Cloud, Impact.Medium, Effort.Low,
                "PerformanceCounter → métricas do CloudWatch",
                "Contadores de desempenho são exclusivos do Windows.",
                "Use System.Diagnostics.Metrics (Meter/Counter/Histogram) exportados via OpenTelemetry/ADOT para o CloudWatch, ou EMF (Embedded Metric Format) nos logs.",
                perf, "Amazon CloudWatch");

        if (hosted && p.Get(Signal.Registry) is { } registry)
            yield return Item("MOD-WIN-REGISTRY", ModernizationKind.Cloud, Impact.High, Effort.Low,
                "Registro do Windows",
                "Não existe Registry no Linux; é dependência dura de container Windows.",
                "Leia essas configurações de IConfiguration (variáveis de ambiente / Parameter Store / Secrets Manager).",
                registry, "AWS Systems Manager Parameter Store");

        if (hosted && (p.Get(Signal.Com) ?? p.Get(Signal.ComPlus)) is { } com)
            yield return Item("MOD-WIN-COM", ModernizationKind.Cloud, Impact.High, Effort.High,
                "Componentes COM/COM+",
                "COM só existe no Windows e a maioria dos componentes de terceiros é 32 bits; impede containers Linux e, com frequência, Fargate.",
                "Identifique o que o componente faz e substitua por biblioteca .NET; se for inevitável, isole-o num serviço em EC2 Windows (ou container Windows) e exponha por HTTP/SQS para o restante da aplicação.",
                com, "Amazon EC2 (Windows)");

        if (hosted && p.Get(Signal.PInvoke) is { } pinvoke)
            yield return Item("MOD-WIN-PINVOKE", ModernizationKind.Cloud, Impact.High, Effort.Medium,
                "P/Invoke em DLLs do Windows",
                $"Chamadas a {string.Join(", ", pinvoke.Details.Take(4))} não existem no Linux.",
                "Verifique se há API gerenciada equivalente (quase sempre há) ou condicione com OperatingSystem.IsWindows() enquanto migra.",
                pinvoke, "Amazon ECS (Linux)");

        if (hosted && p.Get(Signal.Wmi) is { } wmi)
            yield return Item("MOD-WIN-WMI", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
                "WMI (System.Management)", "WMI é exclusivo do Windows.",
                "Para métricas de host use CloudWatch Container Insights; para inventário/configuração, AWS Systems Manager.", wmi, "Amazon CloudWatch");

        if (p.HasAny(Signal.WindowsAuth, Signal.ActiveDirectory))
        {
            var evidence = p.Get(Signal.WindowsAuth) ?? p.Get(Signal.ActiveDirectory)!;
            yield return Item("MOD-ARCH-AD", ModernizationKind.Cloud, Impact.High, Effort.High,
                "Autenticação Windows / Active Directory na AWS",
                "Negotiate/Kerberos em containers exige keytab (Linux) ou gMSA (Windows) e visibilidade dos controladores de domínio; System.DirectoryServices (LDAP) funciona no Linux só via System.DirectoryServices.Protocols.",
                "Recomendado: Amazon Cognito ou Entra ID com OpenID Connect (AddOpenIdConnect) e autorização por grupos/claims — elimina Kerberos. Se precisar manter Windows Auth: AWS Managed Microsoft AD (ou AD Connector) + gMSA em containers Windows no ECS.",
                evidence, "Amazon Cognito / AWS Managed Microsoft AD");
        }
        else if (project.Kind == ProjectKind.Web && p.HasAny(Signal.FormsAuth, Signal.Membership) && !p.Has(Signal.Identity2))
        {
            var evidence = p.Get(Signal.FormsAuth) ?? p.Get(Signal.Membership)!;
            yield return Item("MOD-ARCH-IDENTITY", ModernizationKind.Modernize, Impact.Medium, Effort.High,
                "Login próprio (Forms/Membership) → Amazon Cognito ou ASP.NET Core Identity",
                "A ferramenta converteu Forms Authentication em cookie authentication, mas a validação de usuário/senha, recuperação de senha e MFA continuam por conta da aplicação.",
                "Amazon Cognito (hosted UI, MFA, federação) via AddOpenIdConnect, ou ASP.NET Core Identity no RDS se a base de usuários precisar ficar na aplicação.",
                evidence, "Amazon Cognito");
        }

        if (p.Get(Signal.SecretsInConfig) is { } secrets)
            yield return Item("MOD-SEC-SECRETS", ModernizationKind.Security, Impact.High, Effort.Low,
                "Senhas e chaves em arquivos de configuração → AWS Secrets Manager",
                $"Credenciais em texto claro ({string.Join(", ", secrets.Details.Take(5))}) foram copiadas para o appsettings.json e acabariam na imagem Docker/repositório.",
                "Remova do appsettings.json; injete via Secrets Manager (secrets na task definition do ECS ou Amazon.Extensions.Configuration.SystemsManager no IConfiguration). Para o RDS, prefira autenticação IAM ou rotação automática do Secrets Manager.",
                secrets, "AWS Secrets Manager");

        if (p.Get(Signal.FileLogging) is { } fileLog && !p.Packages.Contains("log4net") && !p.Packages.Contains("NLog") && !p.Packages.Contains("Serilog.Sinks.File"))
            yield return Item("MOD-ARCH-LOGFILES", ModernizationKind.Cloud, Impact.Medium, Effort.Low,
                "Logs em arquivo → stdout/CloudWatch Logs",
                "Arquivos de log no container se perdem e não são pesquisáveis.",
                "Escreva em stdout (JSON) e deixe o ECS enviar ao CloudWatch Logs; defina retenção e alarmes.",
                fileLog, "Amazon CloudWatch Logs");

        foreach (var db in p.Databases.Where(d => d.IntegratedSecurity && !(d.Server ?? "").Contains("localdb", StringComparison.OrdinalIgnoreCase)).DistinctBy(d => d.Name))
            yield return new ModernizationItem
            {
                RuleId = "MOD-ARCH-DB-INTEGRATED", Kind = ModernizationKind.Cloud, Impact = Impact.High, Effort = Effort.Low,
                Title = $"Connection string '{db.Name}' usa Integrated Security",
                Why = "Autenticação Windows no SQL Server exige que o container esteja no domínio (gMSA em Windows ou Kerberos em Linux). O Amazon RDS for SQL Server aceita Windows Auth apenas com AWS Managed Microsoft AD.",
                Proposal = "Troque para autenticação SQL com a senha no Secrets Manager (rotação automática) e Encrypt=True com o certificado do RDS; alternativa: AWS Managed Microsoft AD + gMSA.",
                Evidence = $"{db.Server}/{db.Database}", AwsService = "Amazon RDS for SQL Server / AWS Secrets Manager"
            };

        var internalHosts = p.ExternalEndpoints.Select(Host).Where(IsInternalHost).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        internalHosts.AddRange(p.Databases.Select(d => (d.Server ?? "").Split(',')[0].Split('\\')[0]).Where(IsInternalHost).Where(s => !s.Contains("localdb", StringComparison.OrdinalIgnoreCase)));
        internalHosts = internalHosts.Distinct(StringComparer.OrdinalIgnoreCase).Where(h => h.Length > 0).ToList();
        if (internalHosts.Count > 0)
            yield return new ModernizationItem
            {
                RuleId = "MOD-ARCH-HYBRID", Kind = ModernizationKind.Cloud, Impact = Impact.High, Effort = Effort.Medium,
                Title = "Dependências on-premises (rede interna)",
                Why = $"A aplicação fala com hosts da rede interna ({string.Join(", ", internalHosts.Take(6))}). Da AWS eles só são alcançáveis com conectividade híbrida, e a latência (ida e volta por VPN) multiplica o tempo de chamadas em loop.",
                Proposal = "Provisione Site-to-Site VPN ou Direct Connect e resolução de nomes (Route 53 Resolver outbound endpoints para o DNS interno). Bancos devem ir para o RDS na mesma região; integrações (ERP, WCF) ficam via VPN com timeouts e retry explícitos. Remova hosts fixos do código: configuração/variáveis de ambiente.",
                Evidence = string.Join(", ", internalHosts.Take(8)), AwsService = "AWS Site-to-Site VPN / Direct Connect"
            };

        if (p.Get(Signal.HardcodedUrls) is { } urls && urls.Details.Count > 0)
            yield return Item("MOD-ARCH-HARDCODED-URL", ModernizationKind.Modernize, Impact.Low, Effort.Low,
                "URLs fixas no código",
                $"Endereços embutidos ({string.Join(", ", urls.Details.Take(4))}) impedem ter ambientes distintos (dev/homolog/prod) com a mesma imagem.",
                "Leve para IConfiguration (appsettings por ambiente / variáveis de ambiente / Parameter Store) e injete com IOptions<T>.",
                urls, "AWS Systems Manager Parameter Store");

        if (project.Kind == ProjectKind.Web && p.Get(Signal.LargeUploads) is { } uploads)
            yield return Item("MOD-ARCH-UPLOADS", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
                "Uploads grandes passando pela aplicação",
                $"Limites configurados ({string.Join(", ", uploads.Details)}) indicam uploads grandes. Passar pelo ALB e pelo container consome memória/CPU da task e esbarra em timeouts.",
                "Envie direto do navegador para o S3 com URL pré-assinada (PUT) ou multipart upload, e notifique a aplicação (evento S3 → SQS/Lambda).",
                uploads, "Amazon S3");

        if (project.Kind == ProjectKind.Web && p.Get(Signal.LongRequests) is { } longRequests)
            yield return Item("MOD-ARCH-LONG-REQUESTS", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
                "Requisições longas (executionTimeout alto)",
                $"{string.Join(", ", longRequests.Details)}: o ALB encerra conexões ociosas em 60 s por padrão e o Kestrel não tem executionTimeout.",
                "Para processamento demorado use o padrão assíncrono: a action enfileira (SQS) e devolve 202 + URL de status; um worker ECS/Lambda processa. Se precisar, aumente o idle timeout do ALB (até 4000 s).",
                longRequests, "Amazon SQS");

        if (project.Kind == ProjectKind.Web && p.StaticFileCount >= 25)
            yield return new ModernizationItem
            {
                RuleId = "MOD-ARCH-STATIC", Kind = ModernizationKind.Cloud, Impact = Impact.Low, Effort = Effort.Low,
                Title = "Conteúdo estático via CloudFront",
                Why = $"{p.StaticFileCount} arquivos estáticos (Scripts/Content/fonts) são servidos pelo próprio container.",
                Proposal = "Coloque o CloudFront na frente do ALB com cache para /css, /js, /lib, /fonts (asp-append-version já gera URLs versionadas) ou publique o wwwroot num bucket S3 como origem adicional.",
                Evidence = $"{p.StaticFileCount} arquivos em wwwroot", AwsService = "Amazon CloudFront"
            };

        if (p.Get(Signal.Edmx) is { } edmx)
            yield return Item("MOD-ARCH-EDMX", ModernizationKind.Modernize, Impact.Medium, Effort.High,
                "Modelo EDMX (EF Designer)",
                "O EF6 Designer não funciona em projetos SDK-style e o EF Core não suporta EDMX.",
                "Gere as entidades e o DbContext com dotnet ef dbcontext scaffold (EF Core) a partir do banco, ou converta para Code First no EF6 como etapa intermediária.",
                edmx);

        if (p.Has(Signal.SqlServer) && !p.Has(Signal.Oracle) && p.Databases.Count > 0 && (p.Has(Signal.Ef6) || p.Has(Signal.EfCore)))
            yield return new ModernizationItem
            {
                RuleId = "MOD-COST-SQLSERVER", Kind = ModernizationKind.Cloud, Impact = Impact.Medium, Effort = Effort.High,
                Title = "Custo de licença do SQL Server no RDS",
                Why = "No RDS o SQL Server é cobrado com licença inclusa (Standard/Enterprise), frequentemente o maior item da fatura para aplicações pequenas.",
                Proposal = "Após estabilizar: avalie Amazon Aurora PostgreSQL com Babelfish (fala TDS/T-SQL, reduz reescrita) ou migração via EF Core + Npgsql; use AWS SCT/DMS para converter schema e dados. Para bancos pequenos, RDS SQL Server Express (gratuito em licença) pode bastar.",
                Evidence = string.Join(", ", p.Databases.Select(d => d.Database ?? d.Name).Distinct().Take(5)), AwsService = "Amazon Aurora PostgreSQL (Babelfish)"
            };
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    internal static bool IsInternalHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "." or "(local)" || host.StartsWith("(localdb)")) return false;
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            var b = ip.GetAddressBytes();
            return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168));
        }
        if (!host.Contains('.')) return true;
        return host.EndsWith(".local") || host.EndsWith(".interno") || host.EndsWith(".internal") || host.EndsWith(".corp") || host.EndsWith(".lan") || host.EndsWith(".intranet") || host.EndsWith(".int");
    }

    private static bool IsComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal);
    }

    private sealed class LineIndex
    {
        private readonly string _text;
        private readonly List<int> _starts = [0];

        public LineIndex(string text)
        {
            _text = text;
            for (var i = 0; i < text.Length; i++) if (text[i] == '\n') _starts.Add(i + 1);
        }

        public (int Line, string Content) Locate(int offset)
        {
            var index = _starts.BinarySearch(offset);
            if (index < 0) index = ~index - 1;
            var start = _starts[index];
            var end = index + 1 < _starts.Count ? _starts[index + 1] - 1 : _text.Length;
            return (index + 1, _text[start..Math.Max(start, end)]);
        }
    }
}
