using System.Text.RegularExpressions;

namespace Migrator.Core.Migration;

public sealed record CodeChange(string RuleId, string Description, int Count);

public sealed class CodeFacts
{
    public bool RewroteConfiguration { get; set; }
    public bool UsesLegacyConfigurationApi { get; set; }
    public bool UsesSqlClient { get; set; }
    public bool UsesAspNetCore { get; set; }
    public bool UsesHttpContextCurrent { get; set; }
    public bool UsesWcfClient { get; set; }
    public bool UsesNetTcp { get; set; }
    public bool UsesSystemDrawing { get; set; }
    public bool UsesEventLog { get; set; }
    public bool UsesPerformanceCounter { get; set; }
    public bool UsesNewtonsoftAttributes { get; set; }
    public bool UsesMemoryCache { get; set; }
    public bool UsesOutputCache { get; set; }
    public bool HasMainMethod { get; set; }

    public void Merge(CodeFacts other)
    {
        UsesOutputCache |= other.UsesOutputCache;
        RewroteConfiguration |= other.RewroteConfiguration;
        UsesLegacyConfigurationApi |= other.UsesLegacyConfigurationApi;
        UsesSqlClient |= other.UsesSqlClient;
        UsesAspNetCore |= other.UsesAspNetCore;
        UsesHttpContextCurrent |= other.UsesHttpContextCurrent;
        UsesWcfClient |= other.UsesWcfClient;
        UsesNetTcp |= other.UsesNetTcp;
        UsesSystemDrawing |= other.UsesSystemDrawing;
        UsesEventLog |= other.UsesEventLog;
        UsesPerformanceCounter |= other.UsesPerformanceCounter;
        UsesNewtonsoftAttributes |= other.UsesNewtonsoftAttributes;
        UsesMemoryCache |= other.UsesMemoryCache;
        HasMainMethod |= other.HasMainMethod;
    }
}

public sealed record CodeTransformOptions(bool Log4NetConfigExtracted, bool IsWebProject);

public static partial class CodeTransformer
{
    private static readonly Dictionary<string, string?> NamespaceMap = new(StringComparer.Ordinal)
    {
        ["System.Web.Mvc"] = "Microsoft.AspNetCore.Mvc",
        ["System.Web.Mvc.Html"] = "Microsoft.AspNetCore.Mvc.Rendering",
        ["System.Web.Mvc.Filters"] = "Microsoft.AspNetCore.Mvc.Filters",
        ["System.Web.Mvc.Routing"] = "Microsoft.AspNetCore.Mvc.Routing",
        ["System.Web.Http"] = "Microsoft.AspNetCore.Mvc",
        ["System.Web.Http.Filters"] = "Microsoft.AspNetCore.Mvc.Filters",
        ["System.Web.Http.Controllers"] = "Microsoft.AspNetCore.Mvc.Controllers",
        ["System.Web.Http.Description"] = "Microsoft.AspNetCore.Mvc",
        ["System.Web.Http.Results"] = "Microsoft.AspNetCore.Mvc",
        ["System.Web.Http.ModelBinding"] = "Microsoft.AspNetCore.Mvc.ModelBinding",
        ["System.Web.ModelBinding"] = "Microsoft.AspNetCore.Mvc.ModelBinding",
        ["System.Web.Http.Routing"] = "Microsoft.AspNetCore.Routing",
        ["System.Web.Http.Cors"] = "Microsoft.AspNetCore.Cors",
        ["System.Web.Cors"] = "Microsoft.AspNetCore.Cors",
        ["System.Web.Routing"] = "Microsoft.AspNetCore.Routing",
        ["System.Web.SessionState"] = "Microsoft.AspNetCore.Http",
    };

    private static readonly (string Id, Regex Pattern, string Replacement, string Description)[] AspNetRenames =
    [
        ("CS-RESULT", Rx(@"\bIHttpActionResult\b"), "IActionResult", "IHttpActionResult → IActionResult"),
        ("CS-RESULT", Rx(@"\bnew\s+HttpStatusCodeResult\("), "new StatusCodeResult((int)", "new HttpStatusCodeResult(...) → new StatusCodeResult((int)...)"),
        ("CS-RESULT", Rx(@"\bHttpStatusCodeResult\b"), "StatusCodeResult", "HttpStatusCodeResult → StatusCodeResult"),
        ("CS-RESULT", Rx(@"\bHttpNotFound\("), "NotFound(", "HttpNotFound() → NotFound()"),
        ("CS-RESULT", Rx(@"\bHttpNotFoundResult\b"), "NotFoundResult", "HttpNotFoundResult → NotFoundResult"),
        ("CS-RESULT", Rx(@"\bHttpUnauthorizedResult\b"), "UnauthorizedResult", "HttpUnauthorizedResult → UnauthorizedResult"),
        ("CS-RESULT", Rx(@"\bFilePathResult\b"), "PhysicalFileResult", "FilePathResult → PhysicalFileResult"),
        ("CS-RESULT", Rx(@",\s*JsonRequestBehavior\.(AllowGet|DenyGet)"), "", "Json(x, JsonRequestBehavior.AllowGet) → Json(x)"),
        ("CS-RESULT", Rx(@"\bInternalServerError\(\)"), "StatusCode(500)", "InternalServerError() → StatusCode(500)"),
        ("CS-RESULT", Rx(@"\bRequest\.CreateResponse(?:<[^>()]+>)?\(\s*(?=HttpStatusCode\.)"), "StatusCode((int)", "Request.CreateResponse(HttpStatusCode.X, x) → StatusCode((int)HttpStatusCode.X, x)"),
        ("CS-RESULT", Rx(@"\bRequest\.CreateErrorResponse\(\s*(?=HttpStatusCode\.)"), "StatusCode((int)", "Request.CreateErrorResponse(HttpStatusCode.X, msg) → StatusCode((int)HttpStatusCode.X, msg)"),
        ("CS-RESULT", Rx(@"\bStatusCode\(\s*HttpStatusCode\."), "StatusCode((int)HttpStatusCode.", "StatusCode(HttpStatusCode.X) → StatusCode((int)HttpStatusCode.X)"),
        ("CS-TYPES", Rx(@"\bHttpPostedFileBase\b|\bHttpPostedFileWrapper\b"), "IFormFile", "HttpPostedFileBase → IFormFile"),
        ("CS-TYPES", Rx(@"\bHttpContextBase\b|\bHttpContextWrapper\b"), "HttpContext", "HttpContextBase → HttpContext"),
        ("CS-TYPES", Rx(@"\bHttpRequestBase\b|\bHttpRequestWrapper\b"), "HttpRequest", "HttpRequestBase → HttpRequest"),
        ("CS-TYPES", Rx(@"\bHttpResponseBase\b|\bHttpResponseWrapper\b"), "HttpResponse", "HttpResponseBase → HttpResponse"),
        ("CS-TYPES", Rx(@"\bMvcHtmlString\.Create\("), "new HtmlString(", "MvcHtmlString.Create → new HtmlString"),
        ("CS-TYPES", Rx(@"\bMvcHtmlString\b"), "HtmlString", "MvcHtmlString → HtmlString"),
        ("CS-TYPES", Rx(@"\bIHtmlString\b"), "IHtmlContent", "IHtmlString → IHtmlContent"),
        ("CS-TYPES", Rx(@"\bthis\s+HtmlHelper\b"), "this IHtmlHelper", "Extensões de HtmlHelper → IHtmlHelper"),
        ("CS-TYPES", Rx(@"(?<![\w.])HtmlHelper<"), "IHtmlHelper<", "HtmlHelper<T> → IHtmlHelper<T>"),
        ("CS-ATTR", Rx(@"\bFromUri\b"), "FromQuery", "[FromUri] → [FromQuery]"),
        ("CS-ATTR", Rx(@"\bRoutePrefix\("), "Route(", "[RoutePrefix] → [Route]"),
        ("CS-ATTR", Rx(@"\bResponseType\(\s*(typeof\([^)]*\))\s*\)"), "ProducesResponseType($1, 200)", "[ResponseType] → [ProducesResponseType]"),
        ("CS-ATTR", Rx(@"\[Bind\(\s*Include\s*=\s*"), "[Bind(", "[Bind(Include = ...)] → [Bind(...)]"),
        ("CS-ATTR", Rx(@"^[ \t]*\[(AllowHtml|ValidateInput\(\s*false\s*\))\][ \t]*\r?\n"), "", "[AllowHtml]/[ValidateInput(false)] removidos (sem request validation no ASP.NET Core)"),
        ("CS-ATTR", Rx(@"\[(AllowHtml|ValidateInput\(\s*false\s*\))\]\s*"), "", "[AllowHtml]/[ValidateInput(false)] removidos (sem request validation no ASP.NET Core)"),
        ("CS-REQUEST", Rx(@"\bRequest\.IsAuthenticated\b"), "User.Identity.IsAuthenticated", "Request.IsAuthenticated → User.Identity.IsAuthenticated"),
        ("CS-REQUEST", Rx(@"\bRequest\.QueryString\["), "Request.Query[", "Request.QueryString[...] → Request.Query[...]"),
        ("CS-REQUEST", Rx(@"\bRequest\.UserHostAddress\b"), "HttpContext.Connection.RemoteIpAddress?.ToString()", "Request.UserHostAddress → HttpContext.Connection.RemoteIpAddress"),
        ("CS-REQUEST", Rx(@"\bRequest\.Files\b"), "Request.Form.Files", "Request.Files → Request.Form.Files"),
        ("CS-REQUEST", Rx(@"\bRequest\.IsAjaxRequest\(\)"), "(Request.Headers[\"X-Requested-With\"] == \"XMLHttpRequest\")", "Request.IsAjaxRequest() → cabeçalho X-Requested-With"),
    ];

    private static readonly Regex UsingSystemWeb = Rx(@"^(?<indent>[ \t]*)using\s+(?<ns>System\.Web(?:\.[\w.]+)?)\s*;[ \t]*(?=\r?$)");
    private static readonly Regex AnyUsing = Rx(@"^[ \t]*using\s+(?<ns>[\w.]+)\s*;[ \t]*\r?$");
    private static readonly Regex ConfigManagerPrefix = Rx(@"(?:System\.(?:Web\.)?Configuration\.)?(?:Web)?ConfigurationManager");
    private static readonly Regex LegacyConfigApi = Rx(@"\b(ConfigurationManager|WebConfigurationManager|ConfigurationSection|ConfigurationElement|ConfigurationElementCollection|ConfigurationProperty|ConfigurationPropertyAttribute|ConfigurationCollectionAttribute|IConfigurationSectionHandler|ConfigurationErrorsException|ApplicationSettingsBase|SettingsBase|SettingsProperty|UserScopedSettingAttribute|ApplicationScopedSettingAttribute|DefaultSettingValueAttribute|SpecialSettingAttribute|ConfigurationSaveMode|ConnectionStringSettings|AppSettingsReader|ConfigurationUserLevel|ExeConfigurationFileMap|SettingsProvider)\b");

    public static (string Text, List<CodeChange> Changes, CodeFacts Facts) Transform(string source, CodeTransformOptions options)
    {
        var changes = new Dictionary<(string Id, string Description), int>();
        var facts = new CodeFacts();
        var text = source;
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";

        void Record(string id, string description, int count)
        {
            if (count <= 0) return;
            changes[(id, description)] = changes.GetValueOrDefault((id, description)) + count;
        }

        text = MapSystemWebUsings(text, eol, Record);

        var aspNet = text.Contains("using Microsoft.AspNetCore.Mvc", StringComparison.Ordinal) || options.IsWebProject;
        if (aspNet)
        {
            foreach (var (id, pattern, replacement, description) in AspNetRenames)
                text = Replace(text, pattern, replacement, c => Record(id, description, c));
            text = ConvertOutputCache(text, Record, facts);
        }

        text = RewriteConfiguration(text, eol, facts);

        if (Rx(@"^[ \t]*using\s+System\.Data\.SqlClient\s*;", RegexOptions.Multiline).IsMatch(text) || Rx(@"\bSystem\.Data\.SqlClient\.(?=[A-Z])").IsMatch(text))
        {
            var before = text;
            text = Rx(@"^([ \t]*)using\s+System\.Data\.SqlClient\s*;").Replace(text, "$1using Microsoft.Data.SqlClient;");
            text = Rx(@"\bSystem\.Data\.SqlClient\.(?=[A-Z])").Replace(text, "Microsoft.Data.SqlClient.");
            if (before != text) Record("CS-SQLCLIENT", "System.Data.SqlClient → Microsoft.Data.SqlClient", 1);
        }

        if (options.Log4NetConfigExtracted)
        {
            text = Replace(text, Rx(@"\[assembly:\s*(log4net\.Config\.)?XmlConfigurator\((?![^)]*ConfigFile)"),
                "[assembly: $1XmlConfigurator(ConfigFile = \"log4net.config\", ",
                c => Record("CS-LOG4NET", "Atributo XmlConfigurator apontado para log4net.config", c));
            text = Replace(text, Rx(@"\bXmlConfigurator\.Configure\(\s*\)"),
                "XmlConfigurator.Configure(new System.IO.FileInfo(System.IO.Path.Combine(System.AppContext.BaseDirectory, \"log4net.config\")))",
                c => Record("CS-LOG4NET", "XmlConfigurator.Configure() apontado para log4net.config", c));
            text = text.Replace("XmlConfigurator(ConfigFile = \"log4net.config\", )", "XmlConfigurator(ConfigFile = \"log4net.config\")");
        }

        if (aspNet) text = AddAspNetCoreUsings(text, eol);
        text = RemoveDuplicateUsings(text);

        facts.UsesSqlClient = text.Contains("Microsoft.Data.SqlClient", StringComparison.Ordinal);
        facts.UsesAspNetCore = text.Contains("Microsoft.AspNetCore", StringComparison.Ordinal);
        facts.UsesHttpContextCurrent = Rx(@"\bHttpContext\.Current\b").IsMatch(text);
        facts.UsesWcfClient = Rx(@"\bClientBase<|\bChannelFactory<|\bDuplexClientBase<|\[System\.ServiceModel\.ServiceContractAttribute").IsMatch(text);
        facts.UsesNetTcp = Rx(@"\bNetTcpBinding\b").IsMatch(text);
        facts.UsesSystemDrawing = Rx(@"^[ \t]*using\s+System\.Drawing(\.\w+)*\s*;|\bSystem\.Drawing\.(Bitmap|Image|Graphics|Color|Font)\b").IsMatch(text);
        facts.UsesEventLog = Rx(@"\bEventLog\b").IsMatch(text);
        facts.UsesPerformanceCounter = Rx(@"\bPerformanceCounter\b").IsMatch(text);
        facts.UsesNewtonsoftAttributes = Rx(@"\[(JsonProperty|JsonIgnore|JsonConverter|JsonObject)\b").IsMatch(text) && text.Contains("Newtonsoft.Json", StringComparison.Ordinal);
        facts.UsesMemoryCache = Rx(@"\bMemoryCache\.Default\b|\bObjectCache\b|\bCacheItemPolicy\b").IsMatch(text);
        facts.HasMainMethod = Rx(@"\bstatic\s+(async\s+)?(void|int|Task|Task<int>)\s+Main\s*\(").IsMatch(text);

        return (text, changes.Select(kv => new CodeChange(kv.Key.Id, kv.Key.Description, kv.Value)).ToList(), facts);
    }

    private static string MapSystemWebUsings(string text, string eol, Action<string, string, int> record) =>
        UsingSystemWeb.Replace(text, m =>
        {
            var ns = m.Groups["ns"].Value;
            var indent = m.Groups["indent"].Value;
            if (ns == "System.Web") return m.Value;

            if (NamespaceMap.TryGetValue(ns, out var mapped) && mapped != null)
            {
                record("CS-USING", "using System.Web.* → Microsoft.AspNetCore.*", 1);
                return $"{indent}using {mapped};";
            }

            record("CS-USING-REMOVED", "using de namespace do System.Web sem equivalente comentado (os tipos usados aparecerão como erro de build)", 1);
            return $"{indent}// Migrator: namespace inexistente no .NET 10 → using {ns};";
        });

    private static string RewriteConfiguration(string text, string eol, CodeFacts facts)
    {
        var count = 0;
        var cm = ConfigManagerPrefix.ToString();

        text = Replace(text, Rx(cm + @"\.AppSettings\[\s*""(?<k>[^""]+)""\s*\]"), "configuration[\"AppSettings:${k}\"]", c => count += c);
        text = Replace(text, Rx(cm + @"\.AppSettings\.Get\(\s*""(?<k>[^""]+)""\s*\)"), "configuration[\"AppSettings:${k}\"]", c => count += c);
        text = Replace(text, Rx(cm + @"\.AppSettings\[(?<e>[^\]]+)\]"), "configuration[\"AppSettings:\" + ${e}]", c => count += c);
        text = Replace(text, Rx(cm + @"\.AppSettings\.Get\((?<e>[^()]+)\)"), "configuration[\"AppSettings:\" + ${e}]", c => count += c);
        text = Replace(text, Rx(cm + @"\.ConnectionStrings\[\s*""(?<k>[^""]+)""\s*\]\.ConnectionString\b"), "configuration[\"ConnectionStrings:${k}\"]", c => count += c);
        text = Replace(text, Rx(cm + @"\.ConnectionStrings\[(?<e>[^\]]+)\]\.ConnectionString\b"), "configuration[\"ConnectionStrings:\" + ${e}]", c => count += c);

        var stillLegacy = LegacyConfigApi.IsMatch(text);
        facts.UsesLegacyConfigurationApi = stillLegacy;
        if (count == 0) return text;
        facts.RewroteConfiguration = true;

        if (stillLegacy) return text;

        text = Rx(@"\bconfiguration\[""ConnectionStrings:(?<k>[^""]+)""\]").Replace(text, "configuration.GetConnectionString(\"${k}\")");
        text = Rx(@"\bconfiguration\[""ConnectionStrings:"" \+ (?<e>[^\]]+)\]").Replace(text, "configuration.GetConnectionString(${e})");

        var usingConfig = Rx(@"^([ \t]*)using\s+System\.Configuration\s*;");
        if (usingConfig.IsMatch(text))
            return usingConfig.Replace(text, "$1using Microsoft.Extensions.Configuration;", 1);
        return InsertUsings(text, eol, ["Microsoft.Extensions.Configuration"]);
    }

    private static string AddAspNetCoreUsings(string text, string eol)
    {
        var wanted = new List<string>();
        void Want(string ns, string pattern)
        {
            if (!text.Contains($"using {ns};", StringComparison.Ordinal) && Rx(pattern).IsMatch(text)) wanted.Add(ns);
        }

        Want("Microsoft.AspNetCore.Mvc.Filters", @"\b(ActionFilterAttribute|ActionExecutingContext|ActionExecutedContext|ResultExecutingContext|ResultExecutedContext|IActionFilter|IAsyncActionFilter|IResultFilter|IAuthorizationFilter|AuthorizationFilterContext|ExceptionContext|IExceptionFilter|IFilterMetadata|ResultFilterAttribute|ExceptionFilterAttribute)\b");
        Want("Microsoft.AspNetCore.Mvc.Rendering", @"\b(SelectList|SelectListItem|MultiSelectList|SelectListGroup|TagBuilder|IHtmlHelper|ViewContext)\b");
        Want("Microsoft.AspNetCore.Http", @"\b(IFormFile|IFormFileCollection|HttpContext|HttpRequest|HttpResponse|StatusCodes|IHttpContextAccessor|ISession|CookieOptions)\b");
        Want("Microsoft.AspNetCore.Html", @"\b(HtmlString|IHtmlContent)\b");
        Want("Microsoft.AspNetCore.Authorization", @"\[(Authorize|AllowAnonymous)\b|,\s*(Authorize|AllowAnonymous)\b[\](]");
        Want("Microsoft.AspNetCore.OutputCaching", @"\[OutputCache\(");

        return wanted.Count == 0 ? text : InsertUsings(text, eol, wanted);
    }

    private static string ConvertOutputCache(string text, Action<string, string, int> record, CodeFacts facts) =>
        Rx(@"\[OutputCache\((?<args>[^\]\)]*)\)\]").Replace(text, m =>
        {
            var parts = m.Groups["args"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split('=', 2, StringSplitOptions.TrimEntries))
                .ToList();
            if (parts.Any(p => p.Length != 2 || p[0] is not ("Duration" or "VaryByParam" or "NoStore"))) return m.Value;

            var converted = new List<string>();
            foreach (var p in parts)
            {
                switch (p[0])
                {
                    case "Duration":
                    case "NoStore":
                        converted.Add($"{p[0]} = {p[1]}");
                        break;
                    case "VaryByParam":
                        var value = p[1].Trim('"');
                        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value.Length == 0) break;
                        var keys = value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => $"\"{k}\"");
                        converted.Add($"VaryByQueryKeys = new[] {{ {string.Join(", ", keys)} }}");
                        break;
                }
            }
            facts.UsesOutputCache = true;
            record("CS-OUTPUTCACHE", "[OutputCache] do MVC 5 → Output Caching do ASP.NET Core (AddOutputCache/UseOutputCache no Program.cs)", 1);
            return $"[OutputCache({string.Join(", ", converted)})]";
        });

    private static string InsertUsings(string text, string eol, IEnumerable<string> namespaces)
    {
        var block = string.Concat(namespaces.Select(ns => $"using {ns};{eol}"));
        var usings = AnyUsing.Matches(text);
        var firstDeclaration = Rx(@"^[ \t]*(namespace|public|internal|\[|class|static|sealed|abstract|partial)\b").Match(text);
        var lastTopLevel = usings.Where(u => !firstDeclaration.Success || u.Index < firstDeclaration.Index).LastOrDefault();

        if (lastTopLevel == null) return block + text;

        var insertAt = text.IndexOf('\n', lastTopLevel.Index);
        return insertAt < 0 ? text + eol + block : text.Insert(insertAt + 1, block);
    }

    private static string RemoveDuplicateUsings(string text)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return AnyUsing.Replace(text, m =>
        {
            var key = m.Value.Trim();
            return seen.Add(key) ? m.Value : "\u0000";
        }).Replace("\u0000\r\n", "").Replace("\u0000\n", "").Replace("\u0000", "");
    }

    private static string Replace(string text, Regex pattern, string replacement, Action<int> onCount)
    {
        var count = 0;
        var result = pattern.Replace(text, m => { count++; return m.Result(replacement); });
        onCount(count);
        return result;
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.Multiline);
    private static Regex Rx(string pattern, RegexOptions options) => new(pattern, options);
}
