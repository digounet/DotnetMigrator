using System.Text.RegularExpressions;
using Migrator.Core.Models;

namespace Migrator.Core.Data;

/// <summary>
/// A C# pattern that still compiles on .NET 10 but behaves differently (especially on Linux containers),
/// is a security smell, or has a clearly better modern idiom. Unlike <see cref="CodeRule"/>, these never block the migration.
/// </summary>
public sealed record CodeModernizationRule(
    string Id,
    string Pattern,
    ModernizationKind Kind,
    Impact Impact,
    Effort Effort,
    string Title,
    string Why,
    string Proposal,
    string? FileMustMatch = null,
    ProjectKind[]? OnlyKinds = null,
    string? AwsService = null)
{
    public Regex Regex { get; } = new(Pattern, RegexOptions.Multiline | RegexOptions.Compiled);
    public Regex? FileFilter { get; } = FileMustMatch is null ? null : new Regex(FileMustMatch, RegexOptions.Multiline | RegexOptions.Compiled);
}

public static class CodeModernizationRules
{
    private static readonly ProjectKind[] Hosted = [ProjectKind.Web, ProjectKind.WindowsService, ProjectKind.Console, ProjectKind.ClassLibrary];
    private static readonly ProjectKind[] Workers = [ProjectKind.WindowsService, ProjectKind.Console];
    private static readonly ProjectKind[] ServerSide = [ProjectKind.Web, ProjectKind.WindowsService];

    public static readonly IReadOnlyList<CodeModernizationRule> CSharp =
    [
        // ---- Compiles, but breaks or changes at runtime on .NET 10 / Linux ----
        new("MOD-CS-CODEPAGES", @"\bEncoding\.GetEncoding\(\s*(\d+|""(windows-125\d|iso-8859-\d+|latin1|ibm\d+)"")", ModernizationKind.Modernize, Impact.High, Effort.Low,
            "Encoding.GetEncoding(codepage) lança exceção em tempo de execução",
            "No .NET 10 só UTF-8/16/32 e ASCII vêm no runtime; Windows-1252, ISO-8859-1 etc. exigem o provider de code pages. O código compila e falha com NotSupportedException na primeira chamada.",
            "Adicione o pacote System.Text.Encoding.CodePages e chame Encoding.RegisterProvider(CodePagesEncodingProvider.Instance) uma vez no startup. Avalie converter os arquivos/integrações para UTF-8."),
        new("MOD-CS-ENCODING-DEFAULT", @"\bEncoding\.Default\b", ModernizationKind.Modernize, Impact.Medium, Effort.Low,
            "Encoding.Default muda de ANSI para UTF-8",
            "No .NET Framework Encoding.Default era a página de código do Windows (1252 no Brasil); no .NET 10 é sempre UTF-8. Arquivos legados com acentuação passam a ser lidos errado.",
            "Use explicitamente Encoding.UTF8 ou Encoding.GetEncoding(1252) (com o provider de code pages) conforme a origem real dos dados."),
        new("MOD-CS-DTC", @"\bTransactionScope\b", ModernizationKind.Cloud, Impact.High, Effort.Medium,
            "TransactionScope pode escalar para transação distribuída (MSDTC)",
            "Quando um TransactionScope envolve mais de uma conexão (ou bancos diferentes) o .NET escala para o MSDTC, que não existe no Linux nem no ECS Fargate: a operação falha com PlatformNotSupportedException.",
            "Garanta uma única conexão por escopo (passe a mesma SqlConnection/DbContext), use transações locais (connection.BeginTransaction) ou o padrão outbox para consistência entre banco e fila.",
            OnlyKinds: Hosted, AwsService: "Amazon RDS"),
        new("MOD-CS-CULTURE-THREAD", @"\bThread\.CurrentThread\.Current(UI)?Culture\s*=|\bCultureInfo\.CurrentCulture\s*=", ModernizationKind.Modernize, Impact.Medium, Effort.Low,
            "Cultura definida por thread",
            "Em código assíncrono a thread muda a cada await; definir a cultura por thread deixa de valer. Em containers Linux a cultura padrão depende da variável LANG (sem ela, é a cultura invariante).",
            "Defina CultureInfo.DefaultThreadCurrentCulture/DefaultThreadCurrentUICulture no startup (para web use app.UseRequestLocalization, já gerado no Program.cs) e configure LANG no container."),
        new("MOD-CS-PARSE-CULTURE", @"\b(decimal|double|float|DateTime)\.(Parse|TryParse)\(\s*(""[^""]*""|[\w.\[\]]+)\s*\)|\bConvert\.To(Decimal|Double|Single|DateTime)\(\s*(""[^""]*""|[\w.\[\]]+)\s*\)", ModernizationKind.Modernize, Impact.High, Effort.Medium,
            "Parse de número/data sem cultura explícita",
            "O resultado depende da cultura atual do processo. No .NET Framework em servidores pt-BR era 'pt-BR'; num container Linux sem LANG definido vira a cultura invariante e '1.234,50' passa a ser rejeitado ou interpretado como 1234.50.",
            "Passe CultureInfo explicitamente (CultureInfo.GetCultureInfo(\"pt-BR\") ou InvariantCulture conforme a origem do dado) e use TryParse. O Dockerfile gerado define LANG/LC_ALL para a cultura do web.config quando existe."),
        new("MOD-CS-FORMAT-CULTURE", @"\.ToString\(\s*""[CNP]\d?""\s*\)|\{\d+:[CNP]\d?\}", ModernizationKind.Modernize, Impact.Low, Effort.Low,
            "Formatação monetária/numérica muda com ICU",
            "O .NET 10 usa ICU (não mais o NLS do Windows): em pt-BR 'R$ 1.234,50' passa a ter um espaço não separável (U+00A0) após o símbolo e alguns padrões de data mudam. Código que compara ou faz parse dessas strings quebra.",
            "Nunca faça parse de valores formatados; se precisar de saída idêntica à antiga, use NumberFormatInfo customizado. Teste relatórios e integrações que dependem do formato."),
        new("MOD-CS-STRING-COMPARE", @"\bstring\.Compare\((?![^)]*StringComparison)[^)]*\)|\.(IndexOf|StartsWith|EndsWith|LastIndexOf)\(\s*""[^""]+""\s*\)|\.CompareTo\(", ModernizationKind.Modernize, Impact.Low, Effort.Low,
            "Comparações de string sensíveis à cultura",
            "IndexOf/StartsWith/Compare sem StringComparison usam a cultura atual; com ICU a ordenação e a igualdade de alguns caracteres mudam em relação ao Windows (e caracteres de controle como \\r\\n passaram a ser ignorados em IndexOf).",
            "Use StringComparison.Ordinal/OrdinalIgnoreCase para chaves, caminhos e identificadores; reserve comparações culturais para texto exibido ao usuário."),
        new("MOD-CS-PATH-BACKSLASH", @"\b(File|Directory|Path|StreamReader|StreamWriter|FileStream|FileInfo|DirectoryInfo)\b[^;\n]*""[^""\n]*\\\\[^""\n]*""", ModernizationKind.Cloud, Impact.High, Effort.Low,
            "Caminhos com barra invertida e nomes de arquivo com maiúsculas/minúsculas",
            "Em Linux '\\' não é separador de diretório e o sistema de arquivos diferencia maiúsculas de minúsculas: Path.Combine(dir, \"Relatorios\\\\saida.txt\") cria um arquivo com esse nome literal, e \"Imagens/Logo.PNG\" não encontra \"imagens/logo.png\".",
            "Use Path.Combine com segmentos separados e Path.DirectorySeparatorChar; confira a caixa dos nomes de arquivos e pastas. Arquivos persistentes devem ir para o S3 (a pasta do container é efêmera).",
            OnlyKinds: Hosted, AwsService: "Amazon S3"),
        new("MOD-CS-PROCESS-START", @"\bProcess\.Start\(", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
            "Process.Start de executáveis externos",
            "Executáveis Windows (.exe, .bat, wkhtmltopdf, ferramentas de conversão) não existem num container Linux, e Process.Start(url) não abre o navegador no .NET 10 (UseShellExecute agora é false por padrão).",
            "Substitua a ferramenta externa por biblioteca .NET ou por um serviço (ex.: conversão de arquivos em Lambda); se for indispensável, inclua o binário Linux equivalente na imagem Docker.",
            OnlyKinds: Hosted),
        new("MOD-CS-SPECIAL-FOLDER", @"\bEnvironment\.GetFolderPath\(|\bEnvironment\.SpecialFolder\.", ModernizationKind.Cloud, Impact.Medium, Effort.Low,
            "Pastas especiais do Windows",
            "MyDocuments, ApplicationData, ProgramFiles etc. apontam para outros lugares (ou para vazio) no Linux, e o disco do container some a cada deploy.",
            "Receba o caminho por configuração (variável de ambiente) ou grave em S3/EFS.",
            OnlyKinds: Hosted, AwsService: "Amazon S3"),
        new("MOD-CS-HASHCODE-PERSISTED", @"""[^""]*""\.GetHashCode\(\)|\.GetHashCode\(\)\s*%|\.GetHashCode\(\)\.ToString\(|\bGetHashCode\(\)\s*\)\s*;?\s*//|Math\.Abs\(\s*\w+\.GetHashCode\(\)", ModernizationKind.Modernize, Impact.Medium, Effort.Low,
            "string.GetHashCode() usado como valor estável",
            "No .NET 10 o hash de strings é aleatório por processo: valores persistidos, usados para particionar ou comparar entre instâncias deixam de bater (cada task do ECS gera hashes diferentes).",
            "Use um hash determinístico (XxHash32/64 de System.IO.Hashing, SHA-256) quando o valor sair do processo."),
        new("MOD-CS-ASSEMBLY-LOAD", @"\bAssembly\.(LoadFrom|LoadFile|LoadWithPartialName)\(", ModernizationKind.Modernize, Impact.Low, Effort.Medium,
            "Carregamento dinâmico de assemblies",
            "LoadFrom/LoadFile funcionam, mas o modelo de plugins mudou (AssemblyLoadContext) e caminhos relativos ao bin/ se comportam diferente em publish/containers.",
            "Use AssemblyLoadContext com AssemblyDependencyResolver, ou registre os plugins por DI em vez de descobrir DLLs no disco."),
        new("MOD-CS-VISUALBASIC", @"\bMicrosoft\.VisualBasic\.(Interaction|Information|Strings|Financial|FileIO)\b|\bInteraction\.(MsgBox|InputBox)\(", ModernizationKind.Deprecated, Impact.Low, Effort.Low,
            "Helpers do Microsoft.VisualBasic",
            "Parte do namespace existe no .NET 10 (Microsoft.VisualBasic.Core), mas MsgBox/InputBox e vários helpers não; o restante só sobrevive por compatibilidade.",
            "Troque por equivalentes do BCL (string.Format, Convert, System.IO) ou por bibliotecas específicas (ex.: TextFieldParser → CsvHelper)."),

        // ---- Running behind a load balancer / in containers ----
        new("MOD-CS-CLIENT-IP", @"\bRequest\.UserHostAddress\b|\bConnection\.RemoteIpAddress\b|\bRequest\.IsSecureConnection\b|\bRequest\.Url\.Scheme\b|\bRequest\.Scheme\b", ModernizationKind.Cloud, Impact.Medium, Effort.Low,
            "IP do cliente / esquema HTTPS atrás do balanceador",
            "Atrás do ALB o endereço remoto é o IP do balanceador e a conexão chega em HTTP (TLS termina no ALB). Logs, auditoria, bloqueios por IP e redirecionamentos para HTTPS ficam errados.",
            "Use os cabeçalhos X-Forwarded-For/X-Forwarded-Proto via app.UseForwardedHeaders (já incluído no Program.cs gerado) e remova redirecionamentos manuais para HTTPS.",
            OnlyKinds: [ProjectKind.Web], AwsService: "Application Load Balancer"),
        new("MOD-CS-DATETIME-NOW", @"\bDateTime\.(Now|Today)\b|\bDateTimeOffset\.Now\b", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
            "DateTime.Now depende do fuso do servidor",
            "Containers Linux rodam em UTC por padrão: horários gravados, agendamentos e comparações com 'hoje' passam a ter 3 horas de diferença em relação ao servidor Windows em horário de Brasília.",
            "Armazene em UTC (DateTime.UtcNow) e converta na borda com TimeZoneInfo.FindSystemTimeZoneById(\"America/Sao_Paulo\"); ou injete TimeProvider. Como mitigação imediata o Dockerfile gerado define TZ=America/Sao_Paulo.",
            OnlyKinds: Hosted),
        new("MOD-CS-TIMEZONE-ID", @"FindSystemTimeZoneById\(\s*""[^""]*Standard Time""", ModernizationKind.Cloud, Impact.Low, Effort.Low,
            "IDs de fuso horário do Windows",
            "'E. South America Standard Time' é um ID do Windows. O .NET 10 converte para IANA quando a ICU está presente, mas falha em imagens com globalização invariante ou sem ICU.",
            "Use IDs IANA (America/Sao_Paulo) ou TimeZoneInfo.TryConvertWindowsIdToIanaId.",
            OnlyKinds: Hosted),
        new("MOD-CS-CONSOLE-LOG", @"\bConsole\.WriteLine\(|\bDebug\.WriteLine\(|\bTrace\.(WriteLine|TraceInformation|TraceError)\(", ModernizationKind.Cloud, Impact.Low, Effort.Low,
            "Logs via Console/Debug/Trace",
            "No ECS tudo que vai para stdout chega ao CloudWatch Logs, mas sem nível, categoria nem estrutura; Debug.WriteLine some em Release.",
            "Use ILogger<T> (Microsoft.Extensions.Logging) com saída JSON no console (builder.Logging.AddJsonConsole()) para consultas no CloudWatch Logs Insights.",
            OnlyKinds: ServerSide, AwsService: "Amazon CloudWatch Logs"),

        // ---- Scalability / async ----
        new("MOD-CS-HTTPCLIENT-NEW", @"\bnew\s+(System\.Net\.Http\.)?HttpClient\s*\(", ModernizationKind.Modernize, Impact.Medium, Effort.Low,
            "new HttpClient() por chamada",
            "Criar HttpClient a cada requisição esgota sockets (TIME_WAIT) e ignora rotação de DNS, problema clássico em containers com tráfego alto.",
            "Registre clientes tipados com builder.Services.AddHttpClient<T>() (IHttpClientFactory) e, se quiser resiliência, Microsoft.Extensions.Http.Resilience."),
        new("MOD-CS-SYNC-OVER-ASYNC", @"\)\.Result\b|\)\.Wait\(\s*\)|\.GetAwaiter\(\)\.GetResult\(\)", ModernizationKind.Modernize, Impact.Medium, Effort.Medium,
            "Bloqueio síncrono sobre código assíncrono (.Result / .Wait())",
            "Bloqueia threads do pool e, em ASP.NET Core, provoca starvation sob carga; em containers pequenos (0,5 vCPU) o efeito aparece cedo.",
            "Propague async/await até a action/handler; use IAsyncEnumerable e CancellationToken."),
        new("MOD-CS-ASYNC-VOID", @"\basync\s+void\s+\w+\s*\((?!\s*object\s+sender)", ModernizationKind.Modernize, Impact.Medium, Effort.Low,
            "Métodos async void",
            "Exceções em async void derrubam o processo e não podem ser aguardadas; em ASP.NET Core isso encerra o container.",
            "Retorne Task (ou ValueTask) e aguarde a chamada."),
        new("MOD-CS-THREADS", @"\bnew\s+Thread\s*\(|\bThreadPool\.QueueUserWorkItem\(|\bnew\s+BackgroundWorker\s*\(", ModernizationKind.Modernize, Impact.Medium, Effort.Medium,
            "Threads criadas manualmente",
            "Threads dedicadas, BackgroundWorker e QueueUserWorkItem não participam do ciclo de vida do host (sem graceful shutdown no SIGTERM do ECS) nem do CancellationToken.",
            "Use BackgroundService/IHostedService com Task e CancellationToken; para filas internas, System.Threading.Channels.",
            OnlyKinds: Hosted),
        new("MOD-CS-TIMERS", @"\bnew\s+(System\.Timers\.|System\.Threading\.)?Timer\s*\(|\bThread\.Sleep\(", ModernizationKind.Cloud, Impact.Medium, Effort.Medium,
            "Timer/Thread.Sleep como agendador",
            "Em várias instâncias o job roda em duplicidade; um container 'dormindo' o dia inteiro paga por hora e não tem retry nem histórico.",
            "Converta para um BackgroundService com PeriodicTimer (uma instância) ou, melhor, dispare pelo EventBridge Scheduler uma tarefa ECS/Lambda no horário.",
            OnlyKinds: Workers, AwsService: "Amazon EventBridge Scheduler"),
        new("MOD-CS-STATIC-STATE", @"\b(private|public|internal|protected)\s+static\s+(readonly\s+)?(System\.Collections\.(Generic|Concurrent)\.)?(Dictionary|ConcurrentDictionary|List|HashSet|Queue|ConcurrentQueue|ConcurrentBag)<", ModernizationKind.Cloud, Impact.High, Effort.Medium,
            "Estado em coleções estáticas",
            "Coleções estáticas vivem só dentro de uma instância: com duas tasks no ECS cada usuário vê dados diferentes a cada requisição, e tudo se perde no deploy.",
            "Mova o estado para o banco, para IDistributedCache (ElastiCache) ou para uma fila; se for cache, use IMemoryCache com expiração e aceite a divergência entre instâncias.",
            OnlyKinds: ServerSide, AwsService: "Amazon ElastiCache"),

        // ---- Security ----
        new("MOD-CS-WEAK-CRYPTO", @"\b(MD5|SHA1)(CryptoServiceProvider|Managed)?\.Create\(|\bnew\s+(MD5|SHA1)(CryptoServiceProvider|Managed)\s*\(|\bnew\s+(DES|RC2|TripleDES)CryptoServiceProvider\s*\(|\bRijndaelManaged\b|\bnew\s+Rijndael\b", ModernizationKind.Security, Impact.High, Effort.Medium,
            "Criptografia fraca ou obsoleta (MD5/SHA1/DES/Rijndael)",
            "MD5 e SHA-1 não servem para senhas nem assinaturas; DES/3DES/RC2 são inseguros; RijndaelManaged está obsoleto (SYSLIB0022). Em FIPS/Linux alguns provedores *CryptoServiceProvider não existem.",
            "Senhas: PasswordHasher<T> do ASP.NET Core Identity ou Rfc2898DeriveBytes.Pbkdf2 (ou Argon2). Simetria: Aes.Create() com AES-GCM. Integridade: HMACSHA256. Chaves em AWS Secrets Manager / KMS.",
            AwsService: "AWS Secrets Manager / KMS"),
        new("MOD-CS-RANDOM-SECRET", @"\bnew\s+Random\s*\(", ModernizationKind.Security, Impact.High, Effort.Low,
            "System.Random para gerar token/senha",
            "Random não é criptograficamente seguro: tokens de recuperação de senha, códigos e salts ficam previsíveis.",
            "Use RandomNumberGenerator.GetBytes/GetInt32 ou RandomNumberGenerator.GetHexString.",
            FileMustMatch: @"(?i)\b(token|senha|password|salt|otp|codigo\w*verific|nonce)\w*"),
        new("MOD-CS-SQL-CONCAT", @"""\s*(SELECT|INSERT|UPDATE|DELETE|EXEC|WHERE)\b[^""]*""\s*\+\s*\w+|\$""\s*(SELECT|INSERT|UPDATE|DELETE|EXEC)\b[^""]*\{", ModernizationKind.Security, Impact.High, Effort.Medium,
            "SQL montado por concatenação",
            "Concatenar/interpolar valores em comandos SQL permite injeção e impede o cache de planos no RDS.",
            "Use parâmetros (Dapper: new { id }, SqlParameter) ou EF Core; nunca interpole entrada do usuário."),
        new("MOD-CS-EMPTY-CATCH", @"catch\s*(\(\s*\w*Exception\s*\w*\s*\))?\s*\{\s*\}", ModernizationKind.Modernize, Impact.Low, Effort.Low,
            "Exceções engolidas (catch vazio)",
            "Falhas somem sem rastro; em produção na AWS é o que impede o CloudWatch de mostrar o motivo de um comportamento errado.",
            "Registre com ILogger (LogError(ex, ...)) e decida explicitamente se a exceção deve propagar."),

        // ---- Old idioms ----
        new("MOD-CS-LEGACY-COLLECTIONS", @"\bnew\s+(ArrayList|Hashtable|SortedList|Queue|Stack)\s*\(", ModernizationKind.Modernize, Impact.Low, Effort.Low,
            "Coleções não genéricas (ArrayList/Hashtable)",
            "Boxing, casts e sem segurança de tipos; sobrevivem só por compatibilidade.",
            "Troque por List<T>, Dictionary<TKey,TValue>, Queue<T>, Stack<T>."),
        new("MOD-CS-DATASET", @"\bnew\s+(DataSet|DataTable|SqlDataAdapter)\s*\(|\bDataSet\s+\w+\s*=|\bDataTable\s+\w+\s*=", ModernizationKind.Modernize, Impact.Low, Effort.High,
            "Acesso a dados com DataSet/DataTable/SqlDataAdapter",
            "Funciona no .NET 10, mas é verboso, sem tipagem e sem async; dificulta a eventual troca de banco (ex.: Aurora PostgreSQL para reduzir licenciamento).",
            "Migre gradualmente para Dapper (consultas) ou EF Core (CRUD) com modelos tipados."),
        new("MOD-CS-WEBFORMS-CONTROLS", @"\bSystem\.Web\.UI\.(WebControls|HtmlControls)\b", ModernizationKind.Deprecated, Impact.High, Effort.High,
            "Controles de WebForms referenciados em código",
            "WebForms não existe no .NET 10; o código que manipula controles de página precisa ser reescrito.",
            "Reescreva as telas em Razor Pages/MVC ou Blazor; para portar gradualmente, exponha a lógica como API e mantenha o WebForms antigo até a troca.")
    ];
}
