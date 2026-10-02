using System.Text.RegularExpressions;
using System.Xml.Linq;
using Migrator.Core.Models;

namespace Migrator.Core.Analysis;

/// <summary>Architectural traits detected in a project. Each one maps to a modernization suggestion and/or an AWS component.</summary>
public enum Signal
{
    // Data
    SqlServer, Oracle, MySql, PostgreSql, Sqlite, OleDbOrOdbc, LocalDb, Ef6, EfCore, Edmx, MongoDb, Elasticsearch,
    // Storage
    FileSystemWrites, AppDataFolder, UncPaths, WindowsPaths, FileUploads, Ftp, AzureStorage, FileWatcher, SpreadsheetFiles, OfficeOleDb,
    // Messaging
    Msmq, RabbitMq, AzureServiceBus, Kafka, MessageBusFramework,
    // Email
    Smtp, MailboxReading,
    // State
    InProcSession, ExternalSession, LocalCache, StaticState, Redis, MachineKey,
    // Jobs
    Scheduler, TimerLoop, WindowsServiceHost,
    // Identity
    FormsAuth, WindowsAuth, ActiveDirectory, Membership, Identity2, OwinOAuth, Jwt, Saml,
    // Integration
    SignalR, WcfHost, WcfClient, Asmx, Remoting, ExternalHttp, HardcodedUrls, WebForms,
    // Windows-only
    SystemDrawing, EventLog, PerformanceCounter, Registry, Com, ComPlus, PInvoke, OfficeInterop, CrystalReports, ReportViewer, Wmi, IisAdministration, WinForms, Wpf,
    // Observability / config
    FileLogging, AppInsights, AzureKeyVault, SecretsInConfig, SecretsInCode, AwsSdk,
    // Runtime behaviour
    DateTimeNow, WindowsTimeZoneIds, SyncOverAsync, BinaryFormatter, LargeUploads, LongRequests, Pdf
}

public sealed class SignalEvidence
{
    private const int MaxLocations = 5;

    public required Signal Signal { get; init; }
    public int Count { get; set; }
    public List<string> Locations { get; } = [];
    public SortedSet<string> Details { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string? location, string? detail)
    {
        Count++;
        if (location != null && Locations.Count < MaxLocations && !Locations.Contains(location)) Locations.Add(location);
        if (!string.IsNullOrWhiteSpace(detail)) Details.Add(detail.Trim());
    }

    public void Merge(SignalEvidence other)
    {
        Count += other.Count;
        foreach (var l in other.Locations) if (Locations.Count < MaxLocations && !Locations.Contains(l)) Locations.Add(l);
        foreach (var d in other.Details) Details.Add(d);
    }
}

public sealed record DatabaseUse(string Provider, string? Server, string? Database, bool IntegratedSecurity, string Name, string Project);

public sealed class ApplicationProfile
{
    public required string Project { get; init; }
    public required ProjectKind Kind { get; init; }
    public Dictionary<Signal, SignalEvidence> Signals { get; } = [];
    public List<DatabaseUse> Databases { get; } = [];
    public SortedSet<string> ExternalEndpoints { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<string> Packages { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Culture { get; set; }
    public int StaticFileCount { get; set; }
    public int ViewCount { get; set; }
    public int ApiControllerCount { get; set; }
    public int MvcControllerCount { get; set; }
    /// <summary>Projects whose traits were merged into this one (transitive project references).</summary>
    public SortedSet<string> MergedFrom { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Has(Signal signal) => Signals.ContainsKey(signal);
    public bool HasAny(params Signal[] signals) => signals.Any(Signals.ContainsKey);
    public SignalEvidence? Get(Signal signal) => Signals.GetValueOrDefault(signal);
    public int Count(Signal signal) => Signals.TryGetValue(signal, out var e) ? e.Count : 0;

    public void Add(Signal signal, string? location = null, string? detail = null)
    {
        if (!Signals.TryGetValue(signal, out var evidence)) Signals[signal] = evidence = new SignalEvidence { Signal = signal };
        evidence.Add(location, detail);
    }

    /// <summary>Returns a copy that also contains the traits of every referenced project (what the deployable actually ships).</summary>
    public ApplicationProfile MergeWith(IEnumerable<ApplicationProfile> dependencies)
    {
        var merged = new ApplicationProfile { Project = Project, Kind = Kind, Culture = Culture, StaticFileCount = StaticFileCount, ViewCount = ViewCount, ApiControllerCount = ApiControllerCount, MvcControllerCount = MvcControllerCount };
        void Absorb(ApplicationProfile p)
        {
            foreach (var (signal, evidence) in p.Signals)
            {
                if (!merged.Signals.TryGetValue(signal, out var target)) merged.Signals[signal] = target = new SignalEvidence { Signal = signal };
                target.Merge(evidence);
            }
            foreach (var db in p.Databases) if (!merged.Databases.Contains(db)) merged.Databases.Add(db);
            foreach (var e in p.ExternalEndpoints) merged.ExternalEndpoints.Add(e);
            foreach (var pkg in p.Packages) merged.Packages.Add(pkg);
        }
        Absorb(this);
        foreach (var dependency in dependencies)
        {
            Absorb(dependency);
            merged.MergedFrom.Add(dependency.Project);
        }
        return merged;
    }
}

public static partial class ApplicationProfiler
{
    private sealed record CodeProbe(Signal Signal, Regex Regex, int DetailGroup = 0, ProjectKind[]? OnlyKinds = null);

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly ProjectKind[] Workers = [ProjectKind.WindowsService, ProjectKind.Console];

    private static readonly CodeProbe[] CodeProbes =
    [
        new(Signal.SqlServer, Rx(@"\bSqlConnection\b|\bSystem\.Data\.SqlClient\b|\bMicrosoft\.Data\.SqlClient\b|\bSqlProviderServices\b")),
        new(Signal.Oracle, Rx(@"\bOracleConnection\b|\bOracle\.ManagedDataAccess\b|\bOracle\.DataAccess\b")),
        new(Signal.MySql, Rx(@"\bMySqlConnection\b|\bMySql\.Data\b|\bMySqlConnector\b")),
        new(Signal.PostgreSql, Rx(@"\bNpgsqlConnection\b|\bNpgsql\b")),
        new(Signal.Sqlite, Rx(@"\bSQLiteConnection\b|\bSqliteConnection\b")),
        new(Signal.OleDbOrOdbc, Rx(@"\bOleDbConnection\b|\bOdbcConnection\b")),
        new(Signal.Ef6, Rx(@"\bSystem\.Data\.Entity\b|\bDbContext\s*\(\s*""name=")),
        new(Signal.EfCore, Rx(@"\bMicrosoft\.EntityFrameworkCore\b")),
        new(Signal.MongoDb, Rx(@"\bMongoDB\.Driver\b|\bIMongoCollection<")),
        new(Signal.Elasticsearch, Rx(@"\bNest\b\.|\bElasticClient\b|\bElasticsearch\.Net\b")),

        new(Signal.FileSystemWrites, Rx(@"\bFile\.(WriteAll\w+|AppendAll\w+|Copy|Move|Delete|Create|OpenWrite)\(|\bnew\s+(FileStream|StreamWriter)\(|\bDirectory\.(CreateDirectory|Move|Delete)\(|\.SaveAs\(|\bFile\.ReadAll\w+\(")),
        new(Signal.AppDataFolder, Rx(@"\bApp_Data\b|\bServer\.MapPath\(|\bHostingEnvironment\.MapPath\(|\bHttpRuntime\.AppDomainAppPath\b|\|DataDirectory\|")),
        new(Signal.UncPaths, Rx(@"(?:@""\\\\|""\\\\\\\\)(?<host>[\w\-.$]+)\\"), DetailGroup: 1),
        new(Signal.WindowsPaths, Rx(@"""(?<path>[A-Za-z]:\\[^""\r\n]{0,40})"), DetailGroup: 1),
        new(Signal.FileUploads, Rx(@"\bHttpPostedFile(Base|Wrapper)?\b|\bRequest\.Files\b|\bIFormFile\b")),
        new(Signal.Ftp, Rx(@"\bFtpWebRequest\b|\bWebRequestMethods\.Ftp\b|\bFluentFTP\b|""s?ftp://")),
        new(Signal.AzureStorage, Rx(@"\bWindowsAzure\.Storage\b|\bCloudBlobClient\b|\bCloudStorageAccount\b|\bAzure\.Storage\b|\bBlobServiceClient\b")),

        new(Signal.Msmq, Rx(@"\bSystem\.Messaging\b|\bMessageQueue\b")),
        new(Signal.RabbitMq, Rx(@"\bRabbitMQ\b|\bBasicPublish\(|\bEasyNetQ\b")),
        new(Signal.AzureServiceBus, Rx(@"\bMicrosoft\.ServiceBus\b|\bAzure\.Messaging\.ServiceBus\b|\bServiceBusClient\b|\bTopicClient\b")),
        new(Signal.Kafka, Rx(@"\bConfluent\.Kafka\b")),
        new(Signal.MessageBusFramework, Rx(@"\bMassTransit\b|\bNServiceBus\b|\bRebus\b|\bIBusControl\b|\bIEndpointInstance\b"), DetailGroup: 0),

        new(Signal.Smtp, Rx(@"\bSmtpClient\b|\bSystem\.Net\.Mail\b|\bMailKit\.Net\.Smtp\b|\bSmtpClient\b|\bSendGrid\b")),
        new(Signal.MailboxReading, Rx(@"\bMicrosoft\.Exchange\.WebServices\b|\bExchangeService\b|\bFindItems\(|\bImapClient\b|\bPop3Client\b|\bMailKit\.Net\.(Imap|Pop3)\b|\bOpenPop\b|\bS22\.Imap\b|\bLimilabs\b|\bAE\.Net\.Mail\b|\bGraphServiceClient\b[^;\n]*\.(Me|Users)\b|\bMicrosoft\.Graph\b|\bOutlook\.(Application|MAPIFolder|NameSpace)\b"), DetailGroup: 0),
        new(Signal.FileWatcher, Rx(@"\bFileSystemWatcher\b|\bDirectory\.(GetFiles|EnumerateFiles)\([^)]*\)[^;\n]*(Sleep|Timer|while)|\bwhile\s*\([^)]*\)\s*\{[^}]*Directory\.(GetFiles|EnumerateFiles)\("), OnlyKinds: Workers),
        new(Signal.SpreadsheetFiles, Rx(@"\bOfficeOpenXml\b|\bExcelPackage\b|\bClosedXML\b|\bXLWorkbook\b|\bNPOI\b|\bExcelDataReader\b|\bCsvHelper\b|\bCsvReader\b|\.xlsx?""")),
        new(Signal.OfficeOleDb, Rx(@"Microsoft\.(ACE|Jet)\.OLEDB"), DetailGroup: 0),

        new(Signal.InProcSession, Rx(@"\bSession\[|\bSession\s*\(\s*""|\bHttpSessionState(Base)?\b|\bSession\.(Add|Remove|Clear|Abandon|SetString|GetString)\(")),
        new(Signal.LocalCache, Rx(@"\bHttpRuntime\.Cache\b|\bHttpContext\.Cache\b|\bMemoryCache\.Default\b|\bObjectCache\b|\bIMemoryCache\b|\bSystem\.Runtime\.Caching\b|\bnew\s+MemoryCache\(")),
        new(Signal.StaticState, Rx(@"\b(private|public|internal|protected)\s+static\s+(readonly\s+)?(System\.Collections\.(Generic|Concurrent)\.)?(Dictionary|ConcurrentDictionary|List|HashSet|Queue|ConcurrentQueue|ConcurrentBag)<")),
        new(Signal.Redis, Rx(@"\bStackExchange\.Redis\b|\bConnectionMultiplexer\b|\bServiceStack\.Redis\b")),

        new(Signal.Scheduler, Rx(@"\bQuartz\b|\bIScheduler\b|\bHangfire\b|\bRecurringJob\b|\bFluentScheduler\b|\bBackgroundJob\.(Enqueue|Schedule)\(")),
        new(Signal.TimerLoop, Rx(@"\bnew\s+(System\.Timers\.|System\.Threading\.)?Timer\(|\bThread\.Sleep\(\s*TimeSpan|\bwhile\s*\(\s*true\s*\)|\bwhile\s*\(\s*!\w*(stop|cancel|parar|encerr)\w*|\bTask\.Delay\(\s*TimeSpan"), OnlyKinds: Workers),
        new(Signal.WindowsServiceHost, Rx(@"\bServiceBase\b|\[RunInstaller|\bServiceInstaller\b|\bTopshelf\b|\bHostFactory\.Run\(")),

        new(Signal.FormsAuth, Rx(@"\bFormsAuthentication\b")),
        new(Signal.WindowsAuth, Rx(@"\bWindowsIdentity\.GetCurrent\(|\bWindowsPrincipal\b|\bWindowsImpersonationContext\b")),
        new(Signal.ActiveDirectory, Rx(@"\bSystem\.DirectoryServices\b|\bPrincipalContext\b|\bDirectoryEntry\b|\bDirectorySearcher\b|\bLdapConnection\b|LDAP://")),
        new(Signal.Membership, Rx(@"\bMembership\.(GetUser|CreateUser|ValidateUser|Provider)\b|\bRoles\.(IsUserInRole|GetRolesForUser|Provider)\b|\bMembershipProvider\b|\bRoleProvider\b")),
        new(Signal.Identity2, Rx(@"\bMicrosoft\.AspNet\.Identity\b|\bIdentityDbContext\b|\bApplicationUserManager\b")),
        new(Signal.OwinOAuth, Rx(@"\bOAuthAuthorizationServerProvider\b|\bUseOAuthBearerTokens\b|\bUseOAuthAuthorizationServer\b|\bOAuthBearerAuthenticationOptions\b")),
        new(Signal.Jwt, Rx(@"\bJwtSecurityToken(Handler)?\b|\bUseJwtBearerAuthentication\b|\bAddJwtBearer\b")),
        new(Signal.Saml, Rx(@"\bSystem\.IdentityModel\.Services\b|\bWSFederationAuthenticationModule\b|\bSaml2\b|\bSustainsys\b|\bKentor\.AuthServices\b|\bComponentSpace\b")),

        new(Signal.SignalR, Rx(@"\bMicrosoft\.AspNet\.SignalR\b|\bGlobalHost\.ConnectionManager\b|\bIHubContext\b|:\s*Hub\b")),
        new(Signal.WcfHost, Rx(@"\bnew\s+ServiceHost\(|\bWebServiceHost\b|\[ServiceBehavior|\[AspNetCompatibilityRequirements")),
        new(Signal.WcfClient, Rx(@"\bClientBase<|\bChannelFactory<|\bDuplexClientBase<")),
        new(Signal.Asmx, Rx(@"\bSoapHttpClientProtocol\b|\[WebMethod|\bSystem\.Web\.Services\b")),
        new(Signal.Remoting, Rx(@"\bSystem\.Runtime\.Remoting\b|\bMarshalByRefObject\b|\bRemotingConfiguration\b")),
        new(Signal.ExternalHttp, Rx(@"\bHttpClient\b|\bWebClient\b|\bHttpWebRequest\b|\bRestClient\b|\bRestSharp\b|\bRefit\b|\bFlurl\b")),
        new(Signal.HardcodedUrls, Rx(@"""(?<url>https?://(?!localhost|127\.0\.0\.1|www\.w3\.org|schemas\.|tempuri\.org)[\w.\-]+(?::\d+)?)"), DetailGroup: 1),

        new(Signal.SystemDrawing, Rx(@"\bSystem\.Drawing\b|\bnew\s+Bitmap\(|\bImage\.From(File|Stream)\(|\bGraphics\.FromImage\(")),
        new(Signal.EventLog, Rx(@"\bEventLog\.(WriteEntry|CreateEventSource|SourceExists)\(|\bnew\s+EventLog\(|\bEventLogTraceListener\b")),
        new(Signal.PerformanceCounter, Rx(@"\bPerformanceCounter\b")),
        new(Signal.Registry, Rx(@"\bRegistry\.(LocalMachine|CurrentUser|ClassesRoot|Users)\b|\bRegistryKey\b|\bMicrosoft\.Win32\.Registry\b")),
        new(Signal.Com, Rx(@"\[ComImport\]|\bMarshal\.(GetActiveObject|ReleaseComObject|BindToMoniker)\(|\bType\.GetTypeFromProgID\(|\bType\.GetTypeFromCLSID\(")),
        new(Signal.ComPlus, Rx(@"\bSystem\.EnterpriseServices\b|\bServicedComponent\b")),
        new(Signal.PInvoke, Rx(@"\[DllImport\(\s*""(?<dll>user32|kernel32|advapi32|gdi32|shell32|ole32|winspool|wininet|crypt32|ntdll|psapi|setupapi|winmm|comdlg32|oleaut32)(\.dll)?"""), DetailGroup: 1),
        new(Signal.OfficeInterop, Rx(@"\bMicrosoft\.Office\.Interop\b|\bExcel\.Application\b|\bWord\.Application\b|\bOutlook\.Application\b")),
        new(Signal.CrystalReports, Rx(@"\bCrystalDecisions\b|\bReportDocument\b")),
        new(Signal.ReportViewer, Rx(@"\bMicrosoft\.Reporting\b|\bReportViewer\b|\bLocalReport\b|\bServerReport\b")),
        new(Signal.Wmi, Rx(@"\bSystem\.Management\b|\bManagementObjectSearcher\b|\bManagementClass\b")),
        new(Signal.IisAdministration, Rx(@"\bMicrosoft\.Web\.Administration\b|\bServerManager\b")),

        new(Signal.FileLogging, Rx(@"\bWriteTo\.(File|RollingFile)\(|\bFileTarget\b|\bRollingFileAppender\b")),
        new(Signal.AppInsights, Rx(@"\bTelemetryClient\b|\bMicrosoft\.ApplicationInsights\b")),
        new(Signal.AzureKeyVault, Rx(@"\bKeyVaultClient\b|\bAzure\.Security\.KeyVault\b|\bSecretClient\b")),
        new(Signal.AwsSdk, Rx(@"\bAmazon\.(S3|SQS|SNS|DynamoDBv2|Lambda|SecretsManager|SimpleEmail|Runtime)\b")),

        // Credentials embedded in source: connection strings with a password, named key/secret/token literals, AWS access keys.
        new(Signal.SecretsInCode, Rx(@"""[^""\r\n]*\b(?:Password|Pwd)\s*=\s*(?!\s*[;""{]|\$\{|%)[^;""\r\n]{3,}[^""\r\n]*""|\b(?<name>\w*(?:ApiKey|Api_Key|SecretKey|ClientSecret|AccessKey|SecretAccessKey|Password|Senha|Token|PrivateKey)\w*)\s*=\s*""(?!\s*$|\{|<)[^""\r\n]{6,}""|""(?<name>AKIA[0-9A-Z]{16})"""), DetailGroup: 1),
        new(Signal.DateTimeNow, Rx(@"\bDateTime\.(Now|Today)\b|\bDateTimeOffset\.Now\b")),
        new(Signal.WindowsTimeZoneIds, Rx(@"FindSystemTimeZoneById\(\s*""(?<id>[^""]*Standard Time)"""), DetailGroup: 1),
        new(Signal.SyncOverAsync, Rx(@"\)\.Result\b|\)\.Wait\(\)|\.GetAwaiter\(\)\.GetResult\(\)")),
        new(Signal.BinaryFormatter, Rx(@"\bBinaryFormatter\b|\bNetDataContractSerializer\b|\bSoapFormatter\b")),
        new(Signal.Pdf, Rx(@"\biTextSharp\b|\biText\.Kernel\b|\bPdfSharp\b|\bRotativa\b|\bwkhtmltopdf\b|\bSelectPdf\b|\bQuestPDF\b|\bIronPdf\b|\bAspose\.Pdf\b"))
    ];

    private static readonly (string Prefix, Signal Signal)[] PackageProbes =
    [
        ("Quartz", Signal.Scheduler), ("Hangfire", Signal.Scheduler), ("FluentScheduler", Signal.Scheduler), ("Coravel", Signal.Scheduler),
        ("RabbitMQ.Client", Signal.RabbitMq), ("MassTransit.RabbitMQ", Signal.RabbitMq), ("EasyNetQ", Signal.RabbitMq),
        ("MassTransit", Signal.MessageBusFramework), ("NServiceBus", Signal.MessageBusFramework), ("Rebus", Signal.MessageBusFramework),
        ("Confluent.Kafka", Signal.Kafka),
        ("StackExchange.Redis", Signal.Redis), ("ServiceStack.Redis", Signal.Redis), ("Microsoft.Extensions.Caching.StackExchangeRedis", Signal.Redis),
        ("Microsoft.AspNet.SignalR", Signal.SignalR), ("Microsoft.AspNetCore.SignalR", Signal.SignalR),
        ("iTextSharp", Signal.Pdf), ("itext", Signal.Pdf), ("PdfSharp", Signal.Pdf), ("Rotativa", Signal.Pdf), ("Select.HtmlToPdf", Signal.Pdf), ("IronPdf", Signal.Pdf), ("Aspose.Pdf", Signal.Pdf), ("QuestPDF", Signal.Pdf),
        ("CrystalDecisions", Signal.CrystalReports), ("CrystalReports", Signal.CrystalReports),
        ("Microsoft.ReportViewer", Signal.ReportViewer), ("Microsoft.ReportingServices", Signal.ReportViewer),
        ("Microsoft.Office.Interop", Signal.OfficeInterop),
        ("EntityFramework", Signal.Ef6), ("Microsoft.EntityFrameworkCore", Signal.EfCore),
        ("Oracle.ManagedDataAccess", Signal.Oracle), ("MySql.Data", Signal.MySql), ("MySqlConnector", Signal.MySql), ("Npgsql", Signal.PostgreSql),
        ("System.Data.SQLite", Signal.Sqlite), ("Microsoft.Data.Sqlite", Signal.Sqlite),
        ("MongoDB.Driver", Signal.MongoDb), ("NEST", Signal.Elasticsearch), ("Elasticsearch.Net", Signal.Elasticsearch), ("Elastic.Clients.Elasticsearch", Signal.Elasticsearch),
        ("WindowsAzure.Storage", Signal.AzureStorage), ("Azure.Storage", Signal.AzureStorage), ("Microsoft.Azure.Storage", Signal.AzureStorage),
        ("Microsoft.Azure.KeyVault", Signal.AzureKeyVault), ("Azure.Security.KeyVault", Signal.AzureKeyVault),
        ("Microsoft.ApplicationInsights", Signal.AppInsights),
        ("WindowsAzure.ServiceBus", Signal.AzureServiceBus), ("Azure.Messaging.ServiceBus", Signal.AzureServiceBus), ("Microsoft.Azure.ServiceBus", Signal.AzureServiceBus),
        ("Microsoft.AspNet.Identity", Signal.Identity2),
        ("Microsoft.Owin.Security.OAuth", Signal.OwinOAuth), ("Microsoft.Owin.Security.Jwt", Signal.Jwt), ("System.IdentityModel.Tokens.Jwt", Signal.Jwt),
        ("Sustainsys.Saml2", Signal.Saml), ("Kentor.AuthServices", Signal.Saml), ("ITfoxtec.Identity.Saml2", Signal.Saml),
        ("Serilog.Sinks.File", Signal.FileLogging), ("Serilog.Sinks.RollingFile", Signal.FileLogging),
        ("MailKit", Signal.Smtp), ("SendGrid", Signal.Smtp),
        ("Microsoft.Exchange.WebServices", Signal.MailboxReading), ("Exchange.WebServices.Managed.Api", Signal.MailboxReading), ("OpenPop.NET", Signal.MailboxReading), ("S22.Imap", Signal.MailboxReading),
        ("Limilabs.Mail", Signal.MailboxReading), ("AE.Net.Mail", Signal.MailboxReading), ("Microsoft.Graph", Signal.MailboxReading), ("Microsoft.Office.Interop.Outlook", Signal.MailboxReading),
        ("EPPlus", Signal.SpreadsheetFiles), ("ClosedXML", Signal.SpreadsheetFiles), ("NPOI", Signal.SpreadsheetFiles), ("ExcelDataReader", Signal.SpreadsheetFiles), ("CsvHelper", Signal.SpreadsheetFiles),
        ("RestSharp", Signal.ExternalHttp), ("Refit", Signal.ExternalHttp), ("Flurl", Signal.ExternalHttp),
        ("Topshelf", Signal.WindowsServiceHost),
        ("System.Drawing.Common", Signal.SystemDrawing),
        ("Microsoft.Web.Administration", Signal.IisAdministration),
        ("AWSSDK", Signal.AwsSdk)
    ];

    private static readonly Dictionary<string, Signal> FrameworkReferenceProbes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System.Messaging"] = Signal.Msmq,
        ["System.DirectoryServices"] = Signal.ActiveDirectory,
        ["System.DirectoryServices.AccountManagement"] = Signal.ActiveDirectory,
        ["System.DirectoryServices.Protocols"] = Signal.ActiveDirectory,
        ["System.Management"] = Signal.Wmi,
        ["System.ServiceProcess"] = Signal.WindowsServiceHost,
        ["System.Configuration.Install"] = Signal.WindowsServiceHost,
        ["System.Drawing"] = Signal.SystemDrawing,
        ["System.Web.Services"] = Signal.Asmx,
        ["System.EnterpriseServices"] = Signal.ComPlus,
        ["System.Runtime.Remoting"] = Signal.Remoting,
        ["System.Windows.Forms"] = Signal.WinForms,
        ["PresentationFramework"] = Signal.Wpf,
        ["System.IdentityModel.Services"] = Signal.Saml,
        ["Microsoft.Web.Administration"] = Signal.IisAdministration
    };

    private static readonly HashSet<string> StaticFolders = new(StringComparer.OrdinalIgnoreCase)
        { "Content", "Scripts", "fonts", "Images", "img", "css", "js", "lib", "assets", "Styles" };

    public static ApplicationProfile Analyze(ProjectInfo project, IEnumerable<(string Path, string Text)> code, XElement? config)
    {
        var profile = new ApplicationProfile { Project = project.Name, Kind = project.Kind };
        foreach (var package in project.Packages) profile.Packages.Add(package.Id);

        var texts = code.ToList();
        foreach (var (path, text) in texts)
            ScanCode(profile, project.Kind, path, text, project.IsVisualBasic);

        foreach (var package in project.Packages)
            foreach (var (prefix, signal) in PackageProbes)
                if (package.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    profile.Add(signal, null, package.Id);

        // GAC references only count when the code actually uses the namespace: old project templates
        // reference System.EnterpriseServices, System.Management, System.Drawing... without ever using them.
        foreach (var reference in project.FrameworkReferences)
            if (FrameworkReferenceProbes.TryGetValue(reference, out var signal) && texts.Any(t => UsesNamespace(t.Text, reference)))
                profile.Add(signal, null, reference);

        if (project.ComReferences.Count > 0)
            foreach (var com in project.ComReferences) profile.Add(Signal.Com, null, $"COMReference {com}");
        if (project.UsesWinForms) profile.Add(Signal.WinForms);
        if (project.UsesWpf) profile.Add(Signal.Wpf);

        ScanItems(profile, project);
        if (config != null) ScanConfig(profile, project, config);
        return profile;
    }

    private static bool UsesNamespace(string text, string ns) =>
        text.Contains("using " + ns + ";", StringComparison.Ordinal) || text.Contains(ns + ".", StringComparison.Ordinal);

    /// <summary>VB.NET is case-insensitive (New SqlConnection, new sqlconnection...): the same probes, ignoring case.</summary>
    private static readonly Lazy<CodeProbe[]> CodeProbesVb = new(() =>
        CodeProbes.Select(p => p with
        {
            // VB strings have no escape sequences: a UNC path is written "\\servidor\pasta" literally.
            Regex = p.Signal == Signal.UncPaths
                ? new Regex(@"""\\\\(?<host>[\w\-.$]+)\\", RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase)
                : new Regex(p.Regex.ToString(), p.Regex.Options | RegexOptions.IgnoreCase)
        }).ToArray());

    private static void ScanCode(ApplicationProfile profile, ProjectKind kind, string path, string text, bool visualBasic = false)
    {
        var isController = ControllerFile().IsMatch(text);
        if (isController)
        {
            if (ApiController().IsMatch(text)) profile.ApiControllerCount++;
            else profile.MvcControllerCount++;
        }

        LineIndex? lines = null;
        foreach (var probe in visualBasic ? CodeProbesVb.Value : CodeProbes)
        {
            if (probe.OnlyKinds != null && !probe.OnlyKinds.Contains(kind)) continue;
            foreach (Match match in probe.Regex.Matches(text))
            {
                lines ??= new LineIndex(text);
                var (line, content) = lines.Locate(match.Index);
                if (IsComment(content, visualBasic)) continue;
                var detail = probe.DetailGroup > 0 ? match.Groups[probe.DetailGroup].Value.Replace(@"\\", @"\") : null;
                profile.Add(probe.Signal, $"{path}:{line}", detail);
            }
        }
    }

    private static void ScanItems(ApplicationProfile profile, ProjectInfo project)
    {
        foreach (var item in project.Items)
        {
            var ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
            var relative = item.IsInside(project.ProjectDir) ? Path.GetRelativePath(project.ProjectDir, item.FullPath).Replace('\\', '/') : null;
            switch (ext)
            {
                case ".aspx": profile.Add(Signal.WebForms, relative, "página .aspx"); break;
                case ".ascx": profile.Add(Signal.WebForms, relative, "controle .ascx"); break;
                case ".master": profile.Add(Signal.WebForms, relative, "master page"); break;
                case ".svc": profile.Add(Signal.WcfHost, relative, "arquivo .svc"); break;
                case ".asmx": profile.Add(Signal.Asmx, relative, "arquivo .asmx"); break;
                case ".edmx": profile.Add(Signal.Edmx, relative, "modelo EDMX"); break;
                case ".cshtml": profile.ViewCount++; break;
                case ".rpt": profile.Add(Signal.CrystalReports, relative, "relatório .rpt"); break;
                case ".rdlc": profile.Add(Signal.ReportViewer, relative, "relatório .rdlc"); break;
            }
            if (relative != null && project.Kind == ProjectKind.Web)
            {
                var top = relative.Split('/')[0];
                if (StaticFolders.Contains(top)) profile.StaticFileCount++;
            }
        }
    }

    private static void ScanConfig(ApplicationProfile profile, ProjectInfo project, XElement configuration)
    {
        var fileName = project.ConfigFilePath != null ? Path.GetFileName(project.ConfigFilePath) : "config";

        foreach (var cs in configuration.Element("connectionStrings")?.Elements("add") ?? [])
        {
            var name = cs.Attribute("name")?.Value ?? "";
            var value = cs.Attribute("connectionString")?.Value ?? "";
            var provider = cs.Attribute("providerName")?.Value ?? "";
            if (value.Length == 0) continue;
            var db = ParseConnectionString(name, value, provider, project.Name);
            profile.Databases.Add(db);
            profile.Add(DatabaseSignal(db.Provider), fileName, db.Server != null ? $"{db.Server}/{db.Database}" : db.Database);
            if (db.Server != null && db.Server.Contains("localdb", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.LocalDb, fileName, name);
            if (value.Contains("metadata=", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.Edmx, fileName, name);
            if (value.Contains("|DataDirectory|", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.AppDataFolder, fileName, name);
        }

        foreach (var add in configuration.Element("appSettings")?.Elements("add") ?? [])
        {
            var key = add.Attribute("key")?.Value ?? "";
            var value = add.Attribute("value")?.Value ?? "";
            if (Url().IsMatch(value) && !value.Contains("localhost", StringComparison.OrdinalIgnoreCase)) profile.ExternalEndpoints.Add(Url().Match(value).Value);
            if (WindowsPath().IsMatch(value)) profile.Add(Signal.WindowsPaths, fileName, value);
            if (value.StartsWith(@"\\", StringComparison.Ordinal)) profile.Add(Signal.UncPaths, fileName, value.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            if (Secret().IsMatch(key) && value.Length >= 3) profile.Add(Signal.SecretsInConfig, fileName, key);
        }
        foreach (var setting in configuration.Element("applicationSettings")?.Descendants("setting") ?? [])
        {
            var value = setting.Element("value")?.Value ?? "";
            if (WindowsPath().IsMatch(value)) profile.Add(Signal.WindowsPaths, fileName, value);
            if (value.StartsWith(@"\\", StringComparison.Ordinal)) profile.Add(Signal.UncPaths, fileName, value.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
        }
        foreach (var cs in configuration.Element("connectionStrings")?.Elements("add") ?? [])
            if (PasswordInConnectionString().IsMatch(cs.Attribute("connectionString")?.Value ?? ""))
                profile.Add(Signal.SecretsInConfig, fileName, $"connectionStrings/{cs.Attribute("name")?.Value}");
        // Any other attribute that looks like a credential: <network password>, <identity impersonate userName password>,
        // <sessionState sqlConnectionString="...Password=...">, custom sections, fixed <machineKey>.
        foreach (var attribute in configuration.Descendants().Attributes())
        {
            var name = attribute.Name.LocalName;
            var value = attribute.Value.Trim();
            if (value.Length < 3 || value.StartsWith("AutoGenerate", StringComparison.OrdinalIgnoreCase) || value.StartsWith('$') || value.StartsWith('%')) continue;
            var owner = attribute.Parent!;
            var isAppSettingValue = owner.Name.LocalName == "add" && owner.Parent?.Name.LocalName is "appSettings" or "connectionStrings";
            if (isAppSettingValue) continue; // handled above with the key name
            if (name.Contains("publicKeyToken", StringComparison.OrdinalIgnoreCase) || owner.AncestorsAndSelf().Any(a => a.Name.LocalName is "runtime" or "assemblyBinding" or "configSections")) continue;
            if (Secret().IsMatch(name) && name is not ("tokenType" or "type" or "name" or "key"))
                profile.Add(Signal.SecretsInConfig, fileName, $"{owner.Name.LocalName}/@{name}");
            else if (name.EndsWith("ConnectionString", StringComparison.OrdinalIgnoreCase) && PasswordInConnectionString().IsMatch(value))
                profile.Add(Signal.SecretsInConfig, fileName, $"{owner.Name.LocalName}/@{name}");
        }

        var web = configuration.Element("system.web");
        if (web != null)
        {
            var session = web.Element("sessionState");
            var mode = session?.Attribute("mode")?.Value ?? (session != null ? "InProc" : null);
            if (mode != null)
            {
                if (mode.Equals("InProc", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.InProcSession, fileName, "sessionState mode=InProc");
                else if (!mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.ExternalSession, fileName, $"sessionState mode={mode}");
            }
            var auth = web.Element("authentication")?.Attribute("mode")?.Value;
            if (auth?.Equals("Forms", StringComparison.OrdinalIgnoreCase) == true) profile.Add(Signal.FormsAuth, fileName, "authentication mode=Forms");
            if (auth?.Equals("Windows", StringComparison.OrdinalIgnoreCase) == true) profile.Add(Signal.WindowsAuth, fileName, "authentication mode=Windows");
            if (web.Element("membership") != null || web.Element("roleManager")?.Attribute("enabled")?.Value == "true") profile.Add(Signal.Membership, fileName, "<membership>/<roleManager>");
            if (web.Element("machineKey") is { } mk && mk.Attribute("validationKey")?.Value is { } vk && !vk.StartsWith("AutoGenerate", StringComparison.OrdinalIgnoreCase))
            {
                profile.Add(Signal.MachineKey, fileName, "machineKey fixa");
                profile.Add(Signal.SecretsInConfig, fileName, "machineKey/@validationKey");
            }
            var culture = web.Element("globalization")?.Attribute("culture")?.Value;
            if (!string.IsNullOrEmpty(culture) && !culture.Equals("auto", StringComparison.OrdinalIgnoreCase)) profile.Culture = culture;

            var runtime = web.Element("httpRuntime");
            if (int.TryParse(runtime?.Attribute("maxRequestLength")?.Value, out var kb) && kb > 10 * 1024) profile.Add(Signal.LargeUploads, fileName, $"maxRequestLength={kb} KB");
            if (int.TryParse(runtime?.Attribute("executionTimeout")?.Value, out var seconds) && seconds > 60) profile.Add(Signal.LongRequests, fileName, $"executionTimeout={seconds}s");
        }
        var limits = configuration.Element("system.webServer")?.Element("security")?.Element("requestFiltering")?.Element("requestLimits");
        if (long.TryParse(limits?.Attribute("maxAllowedContentLength")?.Value, out var bytes) && bytes > 10L * 1024 * 1024)
            profile.Add(Signal.LargeUploads, fileName, $"maxAllowedContentLength={bytes / (1024 * 1024)} MB");

        var serviceModel = configuration.Element("system.serviceModel");
        if (serviceModel != null)
        {
            foreach (var endpoint in serviceModel.Element("client")?.Elements("endpoint") ?? [])
            {
                var address = endpoint.Attribute("address")?.Value;
                profile.Add(Signal.WcfClient, fileName, address);
                if (address != null && Url().IsMatch(address)) profile.ExternalEndpoints.Add(Url().Match(address).Value);
            }
            if (serviceModel.Element("services")?.Elements("service").Any() == true) profile.Add(Signal.WcfHost, fileName, "<system.serviceModel><services>");
        }

        if (configuration.Element("system.net")?.Element("mailSettings")?.Element("smtp") is { } smtp)
            profile.Add(Signal.Smtp, fileName, smtp.Element("network")?.Attribute("host")?.Value ?? "mailSettings");

        foreach (var appender in configuration.Element("log4net")?.Elements("appender") ?? [])
        {
            var type = appender.Attribute("type")?.Value ?? "";
            if (type.Contains("FileAppender", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.FileLogging, fileName, "log4net " + type.Split(',')[0].Split('.')[^1]);
            if (type.Contains("EventLogAppender", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.EventLog, fileName, "log4net EventLogAppender");
        }
        foreach (var target in configuration.Descendants().Where(e => e.Name.LocalName == "target" && e.Parent?.Name.LocalName == "targets"))
        {
            var type = target.Attribute("type")?.Value ?? target.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))?.Value ?? "";
            if (type.Equals("File", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.FileLogging, fileName, "nlog File");
            if (type.Equals("EventLog", StringComparison.OrdinalIgnoreCase)) profile.Add(Signal.EventLog, fileName, "nlog EventLog");
        }
    }

    internal static DatabaseUse ParseConnectionString(string name, string value, string providerName, string project)
    {
        var parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToLookup(kv => kv[0].Trim(), kv => kv[1].Trim(), StringComparer.OrdinalIgnoreCase);
        string? Get(params string[] keys) => keys.Select(k => parts[k].FirstOrDefault()).FirstOrDefault(v => v != null);

        // EF6 EDMX connection strings wrap the real one in provider connection string="..."
        var inner = Regex.Match(value, @"provider connection string=&quot;(.*?)&quot;|provider connection string=""(.*?)""", RegexOptions.IgnoreCase);
        if (inner.Success)
        {
            var innerValue = inner.Groups[1].Success ? inner.Groups[1].Value : inner.Groups[2].Value;
            var innerProvider = Regex.Match(value, @"provider=([\w.]+)", RegexOptions.IgnoreCase).Groups[1].Value;
            return ParseConnectionString(name, innerValue, innerProvider, project);
        }

        var provider = providerName.ToLowerInvariant() switch
        {
            var p when p.Contains("oracle") => "Oracle",
            var p when p.Contains("mysql") => "MySQL",
            var p when p.Contains("npgsql") || p.Contains("postgres") => "PostgreSQL",
            var p when p.Contains("sqlite") => "SQLite",
            var p when p.Contains("oledb") || p.Contains("odbc") => "OLE DB/ODBC",
            var p when p.Contains("sqlclient") || p.Contains("sqlserver") => "SQL Server",
            _ => value.Contains("User Id=", StringComparison.OrdinalIgnoreCase) && Get("Data Source") is { } ds && !ds.Contains('\\') && !ds.Contains(',') && value.Contains("Oracle", StringComparison.OrdinalIgnoreCase) ? "Oracle"
               : value.Contains("Provider=", StringComparison.OrdinalIgnoreCase) ? "OLE DB/ODBC"
               : value.Contains("Port=3306", StringComparison.OrdinalIgnoreCase) ? "MySQL"
               : value.Contains("Port=5432", StringComparison.OrdinalIgnoreCase) ? "PostgreSQL"
               : "SQL Server"
        };

        var server = Get("Data Source", "Server", "Host", "Address", "Addr", "Network Address");
        var database = Get("Initial Catalog", "Database", "AttachDbFilename");
        var integrated = (Get("Integrated Security") ?? "").ToLowerInvariant() is "true" or "sspi" or "yes" ||
                         (Get("Trusted_Connection") ?? "").ToLowerInvariant() is "true" or "yes";
        return new DatabaseUse(provider, server, database, integrated, name, project);
    }

    private static Signal DatabaseSignal(string provider) => provider switch
    {
        "Oracle" => Signal.Oracle,
        "MySQL" => Signal.MySql,
        "PostgreSQL" => Signal.PostgreSql,
        "SQLite" => Signal.Sqlite,
        "OLE DB/ODBC" => Signal.OleDbOrOdbc,
        _ => Signal.SqlServer
    };

    private static bool IsComment(string line, bool visualBasic = false)
    {
        var trimmed = line.TrimStart();
        if (visualBasic) return trimmed.StartsWith('\'') || trimmed.StartsWith("REM ", StringComparison.OrdinalIgnoreCase);
        return trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("*", StringComparison.Ordinal) || trimmed.StartsWith("///", StringComparison.Ordinal);
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

    [GeneratedRegex(@":\s*(ApiController|ControllerBase|Controller)\b")]
    private static partial Regex ControllerFile();

    [GeneratedRegex(@":\s*ApiController\b|\[ApiController\]")]
    private static partial Regex ApiController();

    [GeneratedRegex(@"https?://[\w.\-]+(?::\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"password|pwd|secret|apikey|api_key|api-key|clientsecret|accesskey|access_key|token|senha|chave", RegexOptions.IgnoreCase)]
    private static partial Regex Secret();

    [GeneratedRegex(@"(password|pwd)\s*=\s*[^;]{1,}", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordInConnectionString();
}
