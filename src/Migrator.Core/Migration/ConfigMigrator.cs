using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

public sealed record FormsAuthHint(string? LoginPath, int? TimeoutMinutes, string? CookieName, bool? SlidingExpiration, bool RequireSsl);
public sealed record SessionHint(string Mode, int? TimeoutMinutes);

public sealed class ProgramHints
{
    public FormsAuthHint? FormsAuth { get; set; }
    public bool WindowsAuth { get; set; }
    public bool GlobalDenyAnonymous { get; set; }
    public SessionHint? Session { get; set; }
    public string? Culture { get; set; }
    public long? MaxRequestBodyBytes { get; set; }
    public string? ErrorRedirect { get; set; }
    public bool CookiesRequireSsl { get; set; }
    public bool CookiesHttpOnly { get; set; }
}

public sealed class ConfigMigrationResult
{
    public string? AppSettingsJson { get; set; }
    public Dictionary<string, string> EnvironmentJson { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ExtraFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Log4NetExtracted { get; set; }
    public bool NeedsSystemConfiguration { get; set; }
    public ProgramHints Hints { get; } = new();
    public List<InventoryItem> Items { get; } = [];
}

public static partial class ConfigMigrator
{
    private static readonly XNamespace Xdt = "http://schemas.microsoft.com/XML-Document-Transform";

    private static readonly HashSet<string> InfrastructureKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "webpages:Version", "webpages:Enabled", "ClientValidationEnabled", "UnobtrusiveJavaScriptEnabled", "PreserveLoginUrl",
        "owin:AutomaticAppStartup", "owin:appStartup", "vs:EnableBrowserLink", "autoFormsAuthentication", "enableSimpleMembership"
    };

    private static readonly HashSet<string> HandledSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "configSections", "appSettings", "connectionStrings", "system.web", "system.webServer", "system.web.webPages.razor",
        "runtime", "startup", "system.codedom", "entityFramework", "log4net", "nlog", "system.serviceModel", "system.net",
        "system.diagnostics", "applicationSettings", "userSettings", "location", "system.web.extensions", "system.identityModel",
        "system.identityModel.services", "system.transactions", "system.data", "uri", "system.runtime.caching", "system.net.mail",
        "elmah", "glimpse", "unity", "dataCacheClients", "hibernate-configuration", "quartz", "system.xml.serialization"
    };

    private static readonly HashSet<string> StandardHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "ExtensionlessUrlHandler-Integrated-4.0", "ExtensionlessUrlHandler-ISAPI-4.0_32bit", "ExtensionlessUrlHandler-ISAPI-4.0_64bit",
        "TRACEVerbHandler", "OPTIONSVerbHandler", "WebDAV", "BlockViewHandler", "UrlRoutingHandler", "aspNetCore", "Owin"
    };

    private static readonly string[] StandardModulePrefixes = ["System.Web.", "Microsoft.AspNet.TelemetryCorrelation", "Microsoft.ApplicationInsights", "Microsoft.Owin.Host.SystemWeb"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static ConfigMigrationResult Migrate(ProjectInfo project, bool preserveSqlEncryptionBehavior)
    {
        var result = new ConfigMigrationResult();
        var isWeb = project.Kind == ProjectKind.Web;
        var appSettings = new JsonObject();
        var root = new JsonObject
        {
            ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Information", ["Microsoft.AspNetCore"] = "Warning" } }
        };
        if (isWeb) root["AllowedHosts"] = "*";

        var hasContent = false;
        if (project.ConfigFilePath != null)
        {
            var fileName = Path.GetFileName(project.ConfigFilePath);
            XDocument? doc = null;
            try { doc = XDocument.Load(project.ConfigFilePath); }
            catch (Exception ex)
            {
                result.Items.Add(Item(project, InventorySeverity.Breaking, "CFG-PARSE", fileName,
                    $"{fileName} inválido", $"Não foi possível ler o arquivo: {ex.Message}", "Corrija o XML e execute a ferramenta novamente."));
            }

            if (doc?.Root != null)
            {
                var configuration = doc.Root;
                InlineConfigSources(configuration, Path.GetDirectoryName(project.ConfigFilePath)!, result, project, fileName);
                FlattenRootLocations(configuration);
                hasContent |= MigrateAppSettings(configuration, appSettings, result, project, fileName);
                hasContent |= MigrateConnectionStrings(configuration, root, result, project, fileName, preserveSqlEncryptionBehavior);
                hasContent |= MigrateKnownSections(configuration, root, result, project, fileName, isWeb);
                hasContent |= MigrateCustomSections(configuration, root, result, project, fileName);
                if (isWeb)
                {
                    MigrateSystemWeb(configuration, result, project, fileName);
                    MigrateSystemWebServer(configuration, result, project, fileName);
                }
                ReportLocations(configuration, result, project, fileName);
            }
        }

        if (appSettings.Count > 0) root["AppSettings"] = appSettings;

        if (isWeb || hasContent)
            result.AppSettingsJson = root.ToJsonString(JsonOptions);

        foreach (var transform in project.ConfigTransformFiles)
            MigrateTransform(transform, result, project, preserveSqlEncryptionBehavior);

        var dev = result.EnvironmentJson.TryGetValue("Development", out var existingDev) ? JsonNode.Parse(existingDev)!.AsObject() : new JsonObject();
        dev["Logging"] ??= new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Information", ["Microsoft.AspNetCore"] = "Warning" } };
        if (result.AppSettingsJson != null) result.EnvironmentJson["Development"] = dev.ToJsonString(JsonOptions);

        ReportSecrets(result, project);
        return result;
    }

    private static void InlineConfigSources(XElement configuration, string dir, ConfigMigrationResult result, ProjectInfo project, string fileName)
    {
        foreach (var section in configuration.Elements().ToList())
        {
            var source = section.Attribute("configSource")?.Value;
            if (source == null) continue;
            var path = Path.Combine(dir, source.Replace('\\', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-CONFIGSOURCE", fileName,
                    $"configSource não encontrado: {source}", $"A seção <{section.Name.LocalName}> aponta para um arquivo inexistente.",
                    "Os valores dessa seção não foram migrados; copie-os manualmente para o appsettings.json."));
                continue;
            }
            var external = XDocument.Load(path).Root!;
            section.ReplaceWith(external);
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-CONFIGSOURCE", fileName,
                $"configSource incorporado: {source}", $"A seção <{section.Name.LocalName}> foi lida de {source} e migrada.",
                "Nenhuma ação necessária.", auto: true));
        }

        var appSettingsFile = configuration.Element("appSettings")?.Attribute("file")?.Value;
        if (appSettingsFile != null)
        {
            var path = Path.Combine(dir, appSettingsFile);
            if (File.Exists(path) && XDocument.Load(path).Root is { } extra)
                configuration.Element("appSettings")!.Add(extra.Elements("add"));
        }
    }

    private static void FlattenRootLocations(XElement configuration)
    {
        foreach (var location in configuration.Elements("location").ToList())
        {
            var path = location.Attribute("path")?.Value;
            if (path is not (null or "" or ".")) continue;
            foreach (var child in location.Elements().ToList())
            {
                var existing = configuration.Element(child.Name);
                if (existing == null) configuration.Add(child);
                else existing.Add(child.Elements());
            }
            location.Remove();
        }
    }

    private static bool MigrateAppSettings(XElement configuration, JsonObject appSettings, ConfigMigrationResult result, ProjectInfo project, string fileName)
    {
        var dropped = new List<string>();
        foreach (var add in configuration.Elements("appSettings").Elements("add"))
        {
            var key = add.Attribute("key")?.Value;
            if (key == null) continue;
            if (InfrastructureKeys.Contains(key) || key.StartsWith("aspnet:", StringComparison.OrdinalIgnoreCase))
            {
                dropped.Add(key);
                continue;
            }
            appSettings[key] = add.Attribute("value")?.Value ?? "";
        }

        if (appSettings.Count > 0)
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-APPSETTINGS", fileName,
                $"{appSettings.Count} appSettings migradas para appsettings.json (seção AppSettings)",
                "As chaves estão em appsettings.json → \"AppSettings\" e o código foi reescrito para configuration[\"AppSettings:Chave\"].",
                "Considere agrupar chaves relacionadas em classes de opções (IOptions<T>).", auto: true));
        if (dropped.Count > 0)
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-APPSETTINGS-INFRA", fileName,
                $"{dropped.Count} chave(s) de infraestrutura do ASP.NET clássico descartadas",
                string.Join(", ", dropped), "Não se aplicam ao ASP.NET Core.", auto: true));
        return appSettings.Count > 0;
    }

    private static bool MigrateConnectionStrings(XElement configuration, JsonObject root, ConfigMigrationResult result, ProjectInfo project, string fileName, bool preserveEncryption)
    {
        var connections = new JsonObject();
        var patched = new List<string>();
        foreach (var add in configuration.Elements("connectionStrings").Elements("add"))
        {
            var name = add.Attribute("name")?.Value;
            var value = add.Attribute("connectionString")?.Value;
            if (name == null || value == null || name.Equals("LocalSqlServer", StringComparison.OrdinalIgnoreCase) && value.Contains("aspnetdb.mdf", StringComparison.OrdinalIgnoreCase)) continue;
            var provider = add.Attribute("providerName")?.Value ?? "System.Data.SqlClient";

            if (value.Contains("metadata=", StringComparison.OrdinalIgnoreCase))
                result.Items.Add(Item(project, InventorySeverity.Breaking, "CFG-EDMX", fileName,
                    $"Connection string de EDMX (Entity Framework Database First): {name}",
                    "Connection strings com metadata=res://... dependem do modelo EDMX, que não tem suporte de design-time em projetos SDK-style.",
                    "Gere um modelo Code First (EF6 'Code First from Database' ou EF Core scaffolding) e use a 'provider connection string' interna."));
            else if (preserveEncryption && WithEncryptFalse(value, provider) is var patchedValue && patchedValue != value)
            {
                value = patchedValue;
                patched.Add(name);
            }

            if (value.Contains("|DataDirectory|", StringComparison.OrdinalIgnoreCase))
                result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-DATADIRECTORY", fileName,
                    $"|DataDirectory| na connection string {name}",
                    "O .NET 10 não define |DataDirectory| automaticamente.",
                    "No Program.cs: AppDomain.CurrentDomain.SetData(\"DataDirectory\", Path.Combine(builder.Environment.ContentRootPath, \"App_Data\")); ou use caminho absoluto."));

            if (!provider.Contains("SqlClient", StringComparison.OrdinalIgnoreCase) && !provider.Contains("EntityClient", StringComparison.OrdinalIgnoreCase))
                result.Items.Add(Item(project, InventorySeverity.Info, "CFG-PROVIDER", fileName,
                    $"Provider {provider} na connection string {name}",
                    "O providerName não existe no appsettings.json; o provider é definido pelo pacote/código que abre a conexão.",
                    "Garanta que o pacote do provider (ex.: Oracle.ManagedDataAccess.Core, MySqlConnector, Npgsql) esteja referenciado."));

            connections[name] = value;
        }

        if (connections.Count == 0) return false;
        root["ConnectionStrings"] = connections;
        result.Items.Add(Item(project, InventorySeverity.Info, "CFG-CONNSTR", fileName,
            $"{connections.Count} connection string(s) migradas para appsettings.json",
            "Leitura via configuration.GetConnectionString(\"Nome\").", "Nenhuma ação necessária.", auto: true));
        if (patched.Count > 0)
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-ENCRYPT", fileName,
                "Encrypt=False adicionado às connection strings do SQL Server",
                $"Microsoft.Data.SqlClient usa Encrypt=true por padrão; para preservar o comportamento anterior foi adicionado Encrypt=False em: {string.Join(", ", patched)}.",
                "Recomendado: habilitar criptografia (Encrypt=True) com certificado válido no servidor SQL, ou TrustServerCertificate=True apenas em desenvolvimento."));
        return true;
    }

    private static bool MigrateKnownSections(XElement configuration, JsonObject root, ConfigMigrationResult result, ProjectInfo project, string fileName, bool isWeb)
    {
        var hasContent = false;

        if (configuration.Element("runtime")?.Descendants().Any(e => e.Name.LocalName == "dependentAssembly") == true)
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-BINDING", fileName,
                "Binding redirects removidos", "O .NET 10 resolve versões de assembly sem bindingRedirect.", "Nenhuma ação necessária.", auto: true));

        if (configuration.Element("log4net") is { } log4net)
        {
            result.ExtraFiles["log4net.config"] = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + StripNamespaces(log4net);
            result.Log4NetExtracted = true;
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-LOG4NET", fileName,
                "Seção <log4net> extraída para log4net.config",
                "O log4net no .NET 10 não lê a seção do web.config/app.config. O arquivo é copiado para a saída e as chamadas XmlConfigurator foram apontadas para ele.",
                "Considere integrar ao ILogger com Microsoft.Extensions.Logging.Log4Net.AspNetCore.", auto: true));
        }

        if (configuration.Element(XName.Get("nlog", "http://www.nlog-project.org/schemas/NLog.xsd")) is { } nlogNs)
            ExtractNLog(nlogNs, result, project, fileName);
        else if (configuration.Element("nlog") is { } nlog)
            ExtractNLog(nlog, result, project, fileName);

        var settings = new JsonObject();
        foreach (var group in configuration.Elements().Where(e => e.Name.LocalName is "applicationSettings" or "userSettings").Elements())
        {
            var values = new JsonObject();
            foreach (var setting in group.Elements("setting"))
                if (setting.Attribute("name")?.Value is { } name)
                    values[name] = setting.Element("value")?.Value ?? "";
            if (values.Count > 0) settings[group.Name.LocalName] = values;
        }
        if (settings.Count > 0)
        {
            root["ApplicationSettings"] = settings;
            result.NeedsSystemConfiguration = true;
            hasContent = true;
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-SETTINGS", fileName,
                "applicationSettings/userSettings (Settings.settings)",
                isWeb ? "Os valores foram copiados para appsettings.json → ApplicationSettings, mas Properties.Settings.Default não lê o appsettings.json."
                      : "O App.config foi mantido para que Properties.Settings.Default continue funcionando (via System.Configuration.ConfigurationManager). Os valores também estão em appsettings.json → ApplicationSettings.",
                "Migre os usos de Properties.Settings.Default para IOptions<T>/IConfiguration."));
        }

        var serviceModel = configuration.Element("system.serviceModel");
        if (serviceModel != null)
        {
            var endpoints = new JsonObject();
            foreach (var endpoint in serviceModel.Elements("client").Elements("endpoint"))
            {
                var name = endpoint.Attribute("name")?.Value ?? endpoint.Attribute("contract")?.Value ?? $"Endpoint{endpoints.Count + 1}";
                endpoints[name] = new JsonObject
                {
                    ["Address"] = endpoint.Attribute("address")?.Value ?? "",
                    ["Binding"] = endpoint.Attribute("binding")?.Value ?? "",
                    ["BindingConfiguration"] = endpoint.Attribute("bindingConfiguration")?.Value ?? "",
                    ["Contract"] = endpoint.Attribute("contract")?.Value ?? ""
                };
            }
            if (endpoints.Count > 0)
            {
                root["WcfClient"] = new JsonObject { ["Endpoints"] = endpoints };
                hasContent = true;
                result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-WCF-CLIENT", fileName,
                    $"{endpoints.Count} endpoint(s) de cliente WCF copiados para appsettings.json (WcfClient)",
                    "O cliente WCF do .NET 10 não lê <system.serviceModel>; bindings (timeouts, tamanhos, segurança) precisam ser criadas em código.",
                    "Crie o cliente com new XClient(new BasicHttpBinding { MaxReceivedMessageSize = ... }, new EndpointAddress(configuration[\"WcfClient:Endpoints:Nome:Address\"])) ou regenere com dotnet-svcutil."));
            }
            if (serviceModel.Element("services")?.Elements("service").Any() == true)
                result.Items.Add(Item(project, InventorySeverity.Breaking, "CFG-WCF-SERVICE", fileName,
                    "Serviços WCF hospedados (<system.serviceModel><services>)",
                    "O .NET 10 não hospeda serviços WCF.",
                    "Migre para CoreWCF (mantém contratos e a configuração pode ser portada para código/XML do CoreWCF) ou reescreva como gRPC/Web API."));
        }

        var smtp = configuration.Element("system.net")?.Element("mailSettings")?.Element("smtp");
        if (smtp != null)
        {
            var network = smtp.Element("network");
            var smtpJson = new JsonObject();
            void Set(string key, string? value) { if (!string.IsNullOrEmpty(value)) smtpJson[key] = value; }
            Set("From", smtp.Attribute("from")?.Value);
            Set("DeliveryMethod", smtp.Attribute("deliveryMethod")?.Value);
            Set("PickupDirectoryLocation", smtp.Element("specifiedPickupDirectory")?.Attribute("pickupDirectoryLocation")?.Value);
            Set("Host", network?.Attribute("host")?.Value);
            Set("Port", network?.Attribute("port")?.Value);
            Set("UserName", network?.Attribute("userName")?.Value);
            Set("Password", network?.Attribute("password")?.Value);
            Set("EnableSsl", network?.Attribute("enableSsl")?.Value);
            root["Smtp"] = smtpJson;
            hasContent = true;
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-SMTP", fileName,
                "<mailSettings> copiado para appsettings.json (Smtp)",
                "SmtpClient não lê mais a configuração do arquivo.",
                "Configure o SmtpClient (ou MailKit) em código a partir de builder.Configuration.GetSection(\"Smtp\")."));
        }

        if (configuration.Element("system.net")?.Element("defaultProxy") != null)
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-PROXY", fileName,
                "<defaultProxy> configurado", "O proxy do config não é lido no .NET 10.",
                "Use as variáveis de ambiente HTTP_PROXY/HTTPS_PROXY ou configure HttpClient.DefaultProxy / o handler do IHttpClientFactory."));

        if (configuration.Element("system.diagnostics")?.Descendants("add").Any() == true)
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-TRACE", fileName,
                "Trace listeners em <system.diagnostics>", "Listeners configurados no arquivo não são carregados no .NET 10.",
                "Use ILogger (builder.Logging) ou registre os listeners em código (Trace.Listeners.Add)."));

        if (configuration.Element("entityFramework") is { } ef && ef.Descendants().Any(e => e.Name.LocalName is "provider" or "context" or "interceptor"))
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-EF6", fileName,
                "Configuração <entityFramework> (providers/contexts)",
                "Sem app.config/web.config o EF6 não lê providers nem inicializadores declarados em XML: " +
                string.Join(", ", ef.Descendants().Where(e => e.Name.LocalName == "provider").Select(p => p.Attribute("invariantName")?.Value)),
                "Crie uma classe DbConfiguration (SetProviderServices(\"System.Data.SqlClient\", SqlProviderServices.Instance)) e marque o DbContext com [DbConfigurationType(typeof(...))]."));

        foreach (var name in new[] { "system.identityModel", "system.identityModel.services" })
            if (configuration.Element(name) != null)
                result.Items.Add(Item(project, InventorySeverity.Breaking, "CFG-WIF", fileName,
                    $"<{name}> (Windows Identity Foundation)", "WIF não existe no .NET 10.",
                    "Use builder.Services.AddAuthentication().AddWsFederation(...) ou Microsoft.Identity.Web."));

        foreach (var (section, message) in new[]
                 {
                     ("unity", "Registros XML do Unity: reescreva como builder.Services.AddScoped/AddTransient/AddSingleton."),
                     ("elmah", "ELMAH clássico não funciona no ASP.NET Core."),
                     ("glimpse", "Glimpse não existe para ASP.NET Core."),
                     ("dataCacheClients", "AppFabric Cache não existe; use IDistributedCache (Redis)."),
                     ("hibernate-configuration", "Configuração do NHibernate: mova para configuração em código (Fluent/Loquacious)."),
                     ("quartz", "Configuração do Quartz em XML: use AddQuartz(q => ...) (Quartz.Extensions.Hosting).")
                 })
        {
            if (configuration.Element(section) != null)
                result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-COMPONENT", fileName, $"Seção <{section}>", "Seção de componente de terceiros no config.", message));
        }

        return hasContent;
    }

    private static void ExtractNLog(XElement nlog, ConfigMigrationResult result, ProjectInfo project, string fileName)
    {
        result.ExtraFiles["nlog.config"] = "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + nlog;
        result.Items.Add(Item(project, InventorySeverity.Info, "CFG-NLOG", fileName,
            "Seção <nlog> extraída para nlog.config", "O NLog carrega nlog.config automaticamente da pasta da aplicação.",
            "Para integrar ao ILogger use NLog.Web.AspNetCore (builder.Host.UseNLog()).", auto: true));
    }

    private static bool MigrateCustomSections(XElement configuration, JsonObject root, ConfigMigrationResult result, ProjectInfo project, string fileName)
    {
        var declared = configuration.Element("configSections")?.Descendants("section")
            .Select(s => s.Attribute("name")?.Value).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var groups = configuration.Element("configSections")?.Elements("sectionGroup")
            .Select(g => g.Attribute("name")?.Value).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];

        var migrated = new List<string>();
        foreach (var element in configuration.Elements())
        {
            var name = element.Name.LocalName;
            if (HandledSections.Contains(name) || (!declared.Contains(name) && !groups.Contains(name))) continue;
            root[name] = ToJson(element);
            migrated.Add(name);
        }

        if (migrated.Count == 0) return false;
        result.NeedsSystemConfiguration = true;
        result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-CUSTOM", fileName,
            $"{migrated.Count} seção(ões) customizada(s) convertidas para JSON",
            $"Seções: {string.Join(", ", migrated)}. A estrutura XML foi convertida automaticamente (atributos → propriedades, <add key/value> → dicionário).",
            "Revise o JSON gerado e substitua as classes ConfigurationSection por classes de opções: builder.Services.Configure<T>(builder.Configuration.GetSection(\"Nome\"))."));
        return true;
    }

    private static JsonNode ToJson(XElement element)
    {
        var hasChildren = element.HasElements;
        if (!hasChildren && !element.HasAttributes) return JsonValue.Create(element.Value)!;

        var obj = new JsonObject();
        foreach (var attr in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
            obj[attr.Name.LocalName] = attr.Value;

        foreach (var group in element.Elements().GroupBy(e => e.Name.LocalName))
        {
            var items = group.ToList();
            if (group.Key == "add" && items.All(i => i.Attribute("key") != null || i.Attribute("name") != null))
            {
                foreach (var add in items)
                {
                    var key = add.Attribute("key")?.Value ?? add.Attribute("name")!.Value;
                    var others = add.Attributes().Where(a => a.Name.LocalName is not ("key" or "name")).ToList();
                    obj[key] = others.Count == 1 && others[0].Name.LocalName == "value"
                        ? others[0].Value
                        : new JsonObject(others.Select(a => KeyValuePair.Create(a.Name.LocalName, (JsonNode?)JsonValue.Create(a.Value))));
                }
                continue;
            }
            obj[group.Key] = items.Count == 1 ? ToJson(items[0]) : new JsonArray(items.Select(i => (JsonNode?)ToJson(i)).ToArray());
        }
        return obj;
    }

    private static void MigrateSystemWeb(XElement configuration, ConfigMigrationResult result, ProjectInfo project, string fileName)
    {
        var web = configuration.Element("system.web");
        if (web == null) return;
        var hints = result.Hints;

        var auth = web.Element("authentication");
        var mode = auth?.Attribute("mode")?.Value;
        if (string.Equals(mode, "Forms", StringComparison.OrdinalIgnoreCase))
        {
            var forms = auth!.Element("forms");
            hints.FormsAuth = new FormsAuthHint(
                forms?.Attribute("loginUrl")?.Value?.TrimStart('~'),
                int.TryParse(forms?.Attribute("timeout")?.Value, out var t) ? t : null,
                forms?.Attribute("name")?.Value,
                bool.TryParse(forms?.Attribute("slidingExpiration")?.Value, out var s) ? s : null,
                string.Equals(forms?.Attribute("requireSSL")?.Value, "true", StringComparison.OrdinalIgnoreCase));
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-FORMSAUTH", fileName,
                "Forms Authentication → cookie authentication configurada no Program.cs",
                $"loginUrl={hints.FormsAuth.LoginPath ?? "(padrão)"}, timeout={hints.FormsAuth.TimeoutMinutes?.ToString() ?? "(padrão)"} min.",
                "O código que usa FormsAuthentication.SetAuthCookie/SignOut precisa ser trocado por HttpContext.SignInAsync/SignOutAsync (veja os itens WEB004). Tickets/cookies emitidos pela aplicação antiga não serão aceitos.", auto: true));
        }
        else if (string.Equals(mode, "Windows", StringComparison.OrdinalIgnoreCase))
        {
            hints.WindowsAuth = true;
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-WINAUTH", fileName,
                "Windows Authentication → AddNegotiate configurado no Program.cs",
                "Foi adicionado o pacote Microsoft.AspNetCore.Authentication.Negotiate.",
                "No IIS habilite Windows Authentication (e desabilite Anonymous, se aplicável) no site; no IIS Express ajuste launchSettings.json (windowsAuthentication: true).", auto: true));
        }

        var authorization = web.Element("authorization");
        if (authorization != null)
        {
            if (authorization.Elements("deny").Any(d => d.Attribute("users")?.Value is "?" or "*") && !authorization.Elements("allow").Any())
            {
                hints.GlobalDenyAnonymous = true;
                result.Items.Add(Item(project, InventorySeverity.Info, "CFG-AUTHZ", fileName,
                    "<deny users=\"?\" /> → FallbackPolicy exigindo usuário autenticado",
                    "Todas as rotas exigem autenticação, como no web.config.",
                    "Use [AllowAnonymous] nas actions públicas (ex.: login).", auto: true));
            }
            else
                result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-AUTHZ", fileName,
                    "Regras <authorization> do web.config",
                    StartupAnalyzer.Shorten(authorization.ToString()),
                    "Converta para policies (builder.Services.AddAuthorization(o => o.AddPolicy(...))) e atributos [Authorize(Roles = ...)]."));
        }

        var session = web.Element("sessionState");
        var sessionMode = session?.Attribute("mode")?.Value ?? "InProc";
        if (session != null && !sessionMode.Equals("Off", StringComparison.OrdinalIgnoreCase))
        {
            hints.Session = new SessionHint(sessionMode, int.TryParse(session.Attribute("timeout")?.Value, out var st) ? st : null);
            var inProc = sessionMode.Equals("InProc", StringComparison.OrdinalIgnoreCase);
            result.Items.Add(Item(project, inProc ? InventorySeverity.Info : InventorySeverity.Warning, "CFG-SESSION", fileName,
                $"sessionState mode={sessionMode} → AddSession/UseSession no Program.cs",
                inProc ? "Sessão em memória configurada com o mesmo timeout." : $"O modo {sessionMode} usava um servidor de estado; no ASP.NET Core a sessão usa IDistributedCache.",
                inProc ? "Lembre que ISession guarda apenas byte[]/string." : "Configure um cache distribuído: builder.Services.AddDistributedSqlServerCache(...) ou AddStackExchangeRedisCache(...).",
                auto: inProc));
        }

        var errors = web.Element("customErrors");
        if (errors != null && !string.Equals(errors.Attribute("mode")?.Value, "Off", StringComparison.OrdinalIgnoreCase))
        {
            hints.ErrorRedirect = errors.Attribute("defaultRedirect")?.Value?.TrimStart('~');
            if (errors.Elements("error").Any())
                result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-CUSTOMERRORS", fileName,
                    "Páginas de erro por status code (<customErrors><error statusCode=...>)",
                    string.Join(", ", errors.Elements("error").Select(e => $"{e.Attribute("statusCode")?.Value} → {e.Attribute("redirect")?.Value}")),
                    "Use app.UseStatusCodePagesWithReExecute(\"/Error/{0}\") e uma action que trate o código."));
        }

        var globalization = web.Element("globalization");
        var culture = globalization?.Attribute("culture")?.Value ?? globalization?.Attribute("uiCulture")?.Value;
        if (!string.IsNullOrEmpty(culture) && !culture.StartsWith("auto", StringComparison.OrdinalIgnoreCase))
        {
            hints.Culture = culture;
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-CULTURE", fileName,
                $"Cultura {culture} → app.UseRequestLocalization(\"{culture}\")",
                "Datas, números e model binding usam a mesma cultura do web.config.", "Nenhuma ação necessária.", auto: true));
        }

        if (long.TryParse(web.Element("httpRuntime")?.Attribute("maxRequestLength")?.Value, out var kb))
            hints.MaxRequestBodyBytes = Math.Max(hints.MaxRequestBodyBytes ?? 0, kb * 1024);
        if (web.Element("httpRuntime")?.Attribute("executionTimeout") != null)
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-TIMEOUT", fileName,
                "httpRuntime executionTimeout", "Não há timeout de execução por padrão no ASP.NET Core.",
                "Se necessário, use builder.Services.AddRequestTimeouts(...) + app.UseRequestTimeouts()."));

        var cookies = web.Element("httpCookies");
        hints.CookiesRequireSsl = string.Equals(cookies?.Attribute("requireSSL")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        hints.CookiesHttpOnly = string.Equals(cookies?.Attribute("httpOnlyCookies")?.Value, "true", StringComparison.OrdinalIgnoreCase);

        if (web.Element("machineKey") != null)
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-MACHINEKEY", fileName,
                "<machineKey> configurado",
                "O ASP.NET Core usa Data Protection em vez de machineKey: cookies de autenticação, anti-forgery e dados protegidos da aplicação antiga não serão lidos.",
                "Para várias instâncias configure builder.Services.AddDataProtection().PersistKeysTo...(). Para compartilhar login com a aplicação antiga durante a transição, use o compartilhamento de cookies com Data Protection (Microsoft.Owin.Security.Interop)."));

        foreach (var (section, title, hint) in new[]
                 {
                     ("membership", "Membership provider", "Migre para ASP.NET Core Identity; usuários e hashes exigem migração de dados."),
                     ("roleManager", "Role provider", "Use roles do ASP.NET Core Identity ou claims."),
                     ("profile", "Profile provider", "Sem equivalente; armazene o perfil em tabela própria ou como claims."),
                     ("siteMap", "SiteMap provider", "Sem equivalente; implemente menus/breadcrumbs com ViewComponents.")
                 })
        {
            if (web.Element(section)?.Element("providers")?.Elements("add").Any() == true)
                result.Items.Add(Item(project, InventorySeverity.Breaking, "CFG-PROVIDERS", fileName, title, $"<{section}> configurado no web.config.", hint));
        }

        if (string.Equals(web.Element("identity")?.Attribute("impersonate")?.Value, "true", StringComparison.OrdinalIgnoreCase))
            result.Items.Add(Item(project, InventorySeverity.Breaking, "CFG-IMPERSONATE", fileName,
                "<identity impersonate=\"true\">", "Impersonation automática por requisição não existe no ASP.NET Core.",
                "Use WindowsIdentity.RunImpersonated(((WindowsIdentity)User.Identity).AccessToken, () => ...) nos trechos que precisam."));

        ReportCustomEntries(web.Element("httpModules")?.Elements("add"), "módulo HTTP (system.web/httpModules)", result, project, fileName,
            e => e.Attribute("type")?.Value, IsStandardModule);
        ReportCustomEntries(web.Element("httpHandlers")?.Elements("add"), "handler HTTP (system.web/httpHandlers)", result, project, fileName,
            e => e.Attribute("type")?.Value, t => t.StartsWith("System.Web.", StringComparison.Ordinal));
    }

    private static void MigrateSystemWebServer(XElement configuration, ConfigMigrationResult result, ProjectInfo project, string fileName)
    {
        var server = configuration.Element("system.webServer");
        if (server == null) return;

        ReportCustomEntries(server.Element("modules")?.Elements("add"), "módulo HTTP (system.webServer/modules)", result, project, fileName,
            e => e.Attribute("type")?.Value, IsStandardModule);
        ReportCustomEntries(server.Element("handlers")?.Elements("add"), "handler HTTP (system.webServer/handlers)", result, project, fileName,
            e => StandardHandlers.Contains(e.Attribute("name")?.Value ?? "") ? null : e.Attribute("type")?.Value ?? e.Attribute("name")?.Value,
            t => t.StartsWith("System.Web.", StringComparison.Ordinal));

        if (long.TryParse(server.Element("security")?.Element("requestFiltering")?.Element("requestLimits")?.Attribute("maxAllowedContentLength")?.Value, out var bytes))
            result.Hints.MaxRequestBodyBytes = Math.Max(result.Hints.MaxRequestBodyBytes ?? 0, bytes);
        if (result.Hints.MaxRequestBodyBytes is { } max)
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-REQUESTLIMIT", fileName,
                $"Limite de upload ({max / 1024 / 1024} MB) aplicado ao Kestrel/IIS/FormOptions no Program.cs",
                "maxRequestLength/maxAllowedContentLength convertidos.", "Nenhuma ação necessária.", auto: true));

        var preserved = new XElement(server);
        preserved.Elements().Where(e => e.Name.LocalName is "modules" or "handlers" or "validation" or "aspNetCore").Remove();
        if (!preserved.HasElements) return;

        var sections = preserved.Elements().Select(e => e.Name.LocalName).ToList();
        result.ExtraFiles["web.config"] =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine +
            new XElement("configuration", StripNamespacesElement(preserved));
        result.Items.Add(Item(project, InventorySeverity.Info, "CFG-WEBSERVER", fileName,
            "<system.webServer> preservado em um web.config mínimo (IIS)",
            $"Seções mantidas para o IIS: {string.Join(", ", sections)}. O publish do ASP.NET Core acrescenta o handler aspNetCore a esse arquivo.",
            "Se for hospedar fora do IIS (Kestrel, Linux, contêiner): regras de <rewrite> → app.UseRewriter(new RewriteOptions().AddIISUrlRewrite(...)); <httpProtocol><customHeaders> → middleware com Response.Headers; <staticContent> → FileExtensionContentTypeProvider; <httpCompression> → app.UseResponseCompression().", auto: true));
    }

    private static void ReportCustomEntries(IEnumerable<XElement>? entries, string kind, ConfigMigrationResult result, ProjectInfo project, string fileName,
        Func<XElement, string?> describe, Func<string, bool> isStandard)
    {
        if (entries == null) return;
        foreach (var entry in entries)
        {
            var type = describe(entry);
            if (string.IsNullOrEmpty(type) || isStandard(type)) continue;
            var isModule = kind.StartsWith("módulo", StringComparison.Ordinal);
            result.Items.Add(Item(project, InventorySeverity.Breaking, isModule ? "CFG-MODULE" : "CFG-HANDLER", fileName,
                $"Custom {kind}: {entry.Attribute("name")?.Value ?? type}",
                $"Tipo: {type}",
                isModule
                    ? "Reescreva o módulo como middleware (classe com InvokeAsync(HttpContext, RequestDelegate)) e registre com app.UseMiddleware<T>() no Program.cs."
                    : "Reescreva o handler como endpoint (app.MapGet/MapPost(\"/caminho\", ...)) ou middleware terminal."));
        }
    }

    private static bool IsStandardModule(string type) => StandardModulePrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal));

    private static void ReportLocations(XElement configuration, ConfigMigrationResult result, ProjectInfo project, string fileName)
    {
        var paths = configuration.Elements("location").Select(l => l.Attribute("path")?.Value).OfType<string>().ToList();
        if (paths.Count == 0) return;
        result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-LOCATION", fileName,
            $"{paths.Count} bloco(s) <location path=...>",
            $"Configurações por caminho não são aplicadas no ASP.NET Core: {string.Join(", ", paths)}.",
            "Autorização por caminho → [Authorize]/[AllowAnonymous] nos controllers ou policies; outras configurações → app.UseWhen(ctx => ctx.Request.Path.StartsWithSegments(\"/x\"), ...)."));
    }

    private static void MigrateTransform(string transformPath, ConfigMigrationResult result, ProjectInfo project, bool preserveEncryption)
    {
        var fileName = Path.GetFileName(transformPath);
        var parts = fileName.Split('.');
        if (parts.Length < 3) return;
        var configuration = parts[1];
        var environment = configuration.ToLowerInvariant() switch
        {
            "debug" => "Development",
            "release" => "Production",
            _ => configuration
        };

        XDocument doc;
        try { doc = XDocument.Load(transformPath); }
        catch (Exception) { return; }

        var root = result.EnvironmentJson.TryGetValue(environment, out var existing) ? JsonNode.Parse(existing)!.AsObject() : new JsonObject();
        var connections = new JsonObject();
        foreach (var add in doc.Descendants("connectionStrings").Elements("add"))
            if (add.Attribute(Xdt + "Transform") != null && add.Attribute("name")?.Value is { } name && add.Attribute("connectionString")?.Value is { } cs)
                connections[name] = preserveEncryption ? WithEncryptFalse(cs, add.Attribute("providerName")?.Value) : cs;

        var settings = new JsonObject();
        foreach (var add in doc.Descendants("appSettings").Elements("add"))
            if (add.Attribute(Xdt + "Transform") != null && add.Attribute("key")?.Value is { } key)
                settings[key] = add.Attribute("value")?.Value ?? "";

        var others = doc.Descendants()
            .Where(e => e.Attribute(Xdt + "Transform") != null && e.Parent?.Name.LocalName is not ("connectionStrings" or "appSettings"))
            .Select(e => $"{e.Name.LocalName}({e.Attribute(Xdt + "Transform")!.Value})")
            .Where(d => !d.StartsWith("compilation(", StringComparison.Ordinal))
            .Distinct().ToList();

        if (connections.Count > 0) root["ConnectionStrings"] = connections;
        if (settings.Count > 0) root["AppSettings"] = settings;
        if (root.Count > 0) result.EnvironmentJson[environment] = root.ToJsonString(JsonOptions);

        if (connections.Count + settings.Count > 0)
            result.Items.Add(Item(project, InventorySeverity.Info, "CFG-TRANSFORM", fileName,
                $"{fileName} → appsettings.{environment}.json",
                $"{connections.Count} connection string(s) e {settings.Count} appSetting(s) da transformação viraram sobrescritas por ambiente.",
                $"Defina ASPNETCORE_ENVIRONMENT={environment} (ou DOTNET_ENVIRONMENT) no ambiente correspondente.", auto: true));
        if (others.Count > 0)
            result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-TRANSFORM-OTHER", fileName,
                $"Transformações não convertidas em {fileName}",
                string.Join(", ", others),
                "Aplique o equivalente no Program.cs condicionado a app.Environment.IsEnvironment(\"...\")."));
    }

    private static void ReportSecrets(ConfigMigrationResult result, ProjectInfo project)
    {
        var all = string.Join("\n", new[] { result.AppSettingsJson ?? "" }.Concat(result.EnvironmentJson.Values));
        if (!SecretPattern().IsMatch(all)) return;
        result.Items.Add(Item(project, InventorySeverity.Warning, "CFG-SECRETS", "appsettings.json",
            "Possíveis segredos em texto claro no appsettings.json",
            "Senhas/chaves que estavam no web.config/app.config foram copiadas como estão.",
            "Mova segredos para User Secrets (desenvolvimento), variáveis de ambiente ou Azure Key Vault (builder.Configuration.AddAzureKeyVault) e remova-os do controle de versão."));
    }

    private static string WithEncryptFalse(string connectionString, string? provider) =>
        (provider ?? "System.Data.SqlClient").Contains("SqlClient", StringComparison.OrdinalIgnoreCase) && IsSqlServer(connectionString) &&
        !connectionString.Contains("Encrypt", StringComparison.OrdinalIgnoreCase) && !connectionString.Contains("metadata=", StringComparison.OrdinalIgnoreCase)
            ? connectionString.TrimEnd().TrimEnd(';') + ";Encrypt=False"
            : connectionString;

    private static bool IsSqlServer(string cs) =>
        Regex.IsMatch(cs, @"(Data Source|Server|Address|Addr)\s*=", RegexOptions.IgnoreCase) &&
        Regex.IsMatch(cs, @"(Initial Catalog|Database|AttachDbFilename|Integrated Security|User ID|UID)\s*=", RegexOptions.IgnoreCase);

    private static string StripNamespaces(XElement element) => StripNamespacesElement(element).ToString();

    private static XElement StripNamespacesElement(XElement element) =>
        new(element.Name.LocalName,
            element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => new XAttribute(a.Name.LocalName, a.Value)),
            element.Nodes().Select(n => n is XElement child ? StripNamespacesElement(child) : n));

    private static InventoryItem Item(ProjectInfo project, InventorySeverity severity, string rule, string file, string title, string description, string suggestion, bool auto = false) => new()
    {
        Project = project.Name, Severity = severity, Category = InventoryCategory.Configuration, RuleId = rule,
        Title = title, Description = description, Suggestion = suggestion, FilePath = file, AutoMigrated = auto
    };

    [GeneratedRegex(@"(password|pwd|secret|apikey|api_key|clientsecret|accesskey|token)""?\s*[:=]\s*""?[^""\s;]{3,}", RegexOptions.IgnoreCase)]
    private static partial Regex SecretPattern();
}
