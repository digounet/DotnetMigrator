using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Migrator.Core.Models;

namespace Migrator.Core.Analysis;

public sealed record MvcRoute(string Name, string Pattern, string? Defaults, string? Constraints, string? Area);

public sealed record CorsPlan(string Origins, string Headers, string Methods);

public sealed class StartupPlan
{
    public List<MvcRoute> Routes { get; } = [];
    public bool HasAreas { get; set; }
    public bool HasWebApiConfig { get; set; }
    public string ApiRouteTemplate { get; set; } = "api/[controller]";
    public bool ApiCamelCase { get; set; }
    public bool ApiRemovesXml { get; set; }
    public bool LowercaseUrls { get; set; }
    public bool GlobalAuthorize { get; set; }
    public bool RequireHttps { get; set; }
    public List<string> CustomGlobalFilters { get; } = [];
    public CorsPlan? Cors { get; set; }
    public List<string> DiRegistrations { get; } = [];
    public HashSet<string> DiNamespaces { get; } = new(StringComparer.Ordinal);
    public List<(string File, string Statement)> PendingStatements { get; } = [];
    public List<InventoryItem> Items { get; } = [];
}

public static partial class StartupAnalyzer
{
    private static readonly HashSet<string> KnownStartupCalls = new(StringComparer.Ordinal)
    {
        "RouteConfig.RegisterRoutes", "FilterConfig.RegisterGlobalFilters", "BundleConfig.RegisterBundles",
        "WebApiConfig.Register", "GlobalConfiguration.Configure", "AreaRegistration.RegisterAllAreas",
        "BundleTable.EnableOptimizations", "AuthConfig.RegisterAuth", "MvcHandler.DisableMvcResponseHeader",
        "GlobalConfiguration.Configuration.EnsureInitialized", "SwaggerConfig.Register", "UnityConfig.RegisterComponents",
        "UnityWebActivator.Start", "NinjectWebCommon.Start", "IdentityConfig", "ViewEngines.Engines.Clear", "ViewEngines.Engines.Add"
    };

    private static readonly Dictionary<string, (InventorySeverity Severity, string Hint)> GlobalAsaxEvents = new(StringComparer.Ordinal)
    {
        ["Application_Error"] = (InventorySeverity.Breaking, "Use app.UseExceptionHandler(\"/Home/Error\") (já no Program.cs) ou implemente IExceptionHandler e registre com builder.Services.AddExceptionHandler<T>()."),
        ["Application_BeginRequest"] = (InventorySeverity.Breaking, "Reescreva como middleware: app.Use(async (ctx, next) => { /* antes */ await next(); });"),
        ["Application_EndRequest"] = (InventorySeverity.Breaking, "Reescreva como middleware: app.Use(async (ctx, next) => { await next(); /* depois */ });"),
        ["Application_PreSendRequestHeaders"] = (InventorySeverity.Breaking, "Use ctx.Response.OnStarting(() => { ... }) em um middleware."),
        ["Application_PostRequestHandlerExecute"] = (InventorySeverity.Breaking, "Reescreva como middleware."),
        ["Application_AcquireRequestState"] = (InventorySeverity.Breaking, "Reescreva como middleware posicionado após app.UseSession()."),
        ["Application_AuthenticateRequest"] = (InventorySeverity.Breaking, "Use IClaimsTransformation para enriquecer o usuário ou um middleware após app.UseAuthentication()."),
        ["Application_PostAuthenticateRequest"] = (InventorySeverity.Breaking, "Use IClaimsTransformation para enriquecer o usuário ou um middleware após app.UseAuthentication()."),
        ["Application_AuthorizeRequest"] = (InventorySeverity.Breaking, "Use policies de autorização (AddAuthorization) ou um middleware após app.UseAuthorization()."),
        ["Session_Start"] = (InventorySeverity.Warning, "Não há eventos de sessão no ASP.NET Core; inicialize os valores sob demanda."),
        ["Session_End"] = (InventorySeverity.Warning, "Não há eventos de sessão no ASP.NET Core; use expiração do IDistributedCache se precisar limpar recursos."),
        ["Application_End"] = (InventorySeverity.Warning, "Use app.Lifetime.ApplicationStopping.Register(() => { ... })."),
        ["Init"] = (InventorySeverity.Breaking, "HttpApplication.Init não existe; registre middlewares no Program.cs."),
    };

    private static readonly Dictionary<string, (InventorySeverity Severity, string Hint)> OwinHints = new(StringComparer.Ordinal)
    {
        ["UseCookieAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o => { o.LoginPath = \"/Account/Login\"; });"),
        ["UseExternalSignInCookie"] = (InventorySeverity.Breaking, "Use ASP.NET Core Identity: builder.Services.AddIdentity<TUser, TRole>().AddEntityFrameworkStores<TContext>()."),
        ["UseTwoFactorSignInCookie"] = (InventorySeverity.Breaking, "2FA é parte do ASP.NET Core Identity (SignInManager.TwoFactorSignInAsync)."),
        ["UseTwoFactorRememberBrowserCookie"] = (InventorySeverity.Breaking, "2FA é parte do ASP.NET Core Identity."),
        ["CreatePerOwinContext"] = (InventorySeverity.Breaking, "Registre os serviços no DI (builder.Services.AddScoped<...>()); UserManager/SignInManager vêm do AddIdentity()."),
        ["UseOAuthBearerTokens"] = (InventorySeverity.Breaking, "O servidor de autorização OAuth do OWIN não existe no ASP.NET Core. Use OpenIddict, Duende IdentityServer ou Microsoft Entra ID e valide com AddJwtBearer."),
        ["UseOAuthAuthorizationServer"] = (InventorySeverity.Breaking, "O servidor de autorização OAuth do OWIN não existe no ASP.NET Core. Use OpenIddict, Duende IdentityServer ou Microsoft Entra ID."),
        ["UseOAuthBearerAuthentication"] = (InventorySeverity.Breaking, "Tokens do servidor OWIN não são JWT e não podem ser validados no ASP.NET Core; migre a emissão para JWT e use AddJwtBearer."),
        ["UseJwtBearerAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o => { o.TokenValidationParameters = ... });"),
        ["UseOpenIdConnectAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication().AddOpenIdConnect(...) ou Microsoft.Identity.Web (AddMicrosoftIdentityWebApp)."),
        ["UseWindowsAzureActiveDirectoryBearerAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration) (Microsoft.Identity.Web)."),
        ["UseWsFederationAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication().AddWsFederation(...)."),
        ["UseGoogleAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication().AddGoogle(...)."),
        ["UseFacebookAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication().AddFacebook(...)."),
        ["UseMicrosoftAccountAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication().AddMicrosoftAccount(...)."),
        ["UseTwitterAuthentication"] = (InventorySeverity.Warning, "builder.Services.AddAuthentication().AddTwitter(...)."),
        ["UseCors"] = (InventorySeverity.Warning, "builder.Services.AddCors(...) + app.UseCors(). Evite CorsOptions.AllowAll: declare as origens permitidas."),
        ["MapSignalR"] = (InventorySeverity.Breaking, "app.MapHub<THub>(\"/signalr\"); os clientes precisam migrar para @microsoft/signalr."),
        ["UseHangfireDashboard"] = (InventorySeverity.Warning, "app.UseHangfireDashboard() com o pacote Hangfire.AspNetCore."),
        ["UseHangfireServer"] = (InventorySeverity.Warning, "builder.Services.AddHangfireServer() com o pacote Hangfire.AspNetCore."),
        ["UseWebApi"] = (InventorySeverity.Info, "Coberto por app.MapControllers() no Program.cs gerado."),
        ["UseStageMarker"] = (InventorySeverity.Info, "Não se aplica ao ASP.NET Core; removido."),
        ["UseStaticFiles"] = (InventorySeverity.Info, "app.UseStaticFiles() já está no Program.cs gerado."),
        ["UseFileServer"] = (InventorySeverity.Warning, "app.UseFileServer()."),
        ["UseErrorPage"] = (InventorySeverity.Info, "Use app.UseDeveloperExceptionPage() em desenvolvimento."),
        ["SetDataProtectionProvider"] = (InventorySeverity.Warning, "Use ASP.NET Core Data Protection (builder.Services.AddDataProtection())."),
    };

    public static StartupPlan Analyze(IEnumerable<(string RelativePath, string Text)> legacyFiles, string projectName)
    {
        var plan = new StartupPlan();
        foreach (var (path, text) in legacyFiles)
        {
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            AnalyzeInvocations(root, path, plan, projectName);
            AnalyzeGlobalAsax(root, path, plan, projectName);
            AnalyzeOwin(root, path, plan, projectName);
            AnalyzeDiRegistrations(root, text, path, plan, projectName);

            if (text.Contains("CamelCasePropertyNamesContractResolver", StringComparison.Ordinal)) plan.ApiCamelCase = true;
            if (XmlFormatterRemoved().IsMatch(text)) plan.ApiRemovesXml = true;
            if (LowercaseUrls().IsMatch(text)) plan.LowercaseUrls = true;
        }
        return plan;
    }

    private static void AnalyzeInvocations(SyntaxNode root, string path, StartupPlan plan, string project)
    {
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = MethodName(call);
            var receiver = call.Expression is MemberAccessExpressionSyntax ma ? ma.Expression.ToString() : "";

            switch (name)
            {
                case "MapRoute":
                    if (ParseMapRoute(call, AreaNameOf(call)) is { } route) plan.Routes.Add(route);
                    else plan.Items.Add(Item(project, InventorySeverity.Warning, "STARTUP-ROUTE", path, call,
                        "Rota MVC não convertida automaticamente",
                        $"Não foi possível interpretar: {Shorten(call.ToString())}",
                        "Recrie a rota no Program.cs com app.MapControllerRoute(name, pattern) usando a sintaxe {controller=Home}/{action=Index}/{id?}."));
                    break;
                case "MapHttpRoute":
                    plan.HasWebApiConfig = true;
                    if (Argument(call, "routeTemplate", 1) is LiteralExpressionSyntax lit)
                        plan.ApiRouteTemplate = ConvertApiTemplate(lit.Token.ValueText);
                    break;
                case "RegisterAllAreas":
                    plan.HasAreas = true;
                    break;
                case "EnableCors":
                    var attr = call.ArgumentList.Arguments.FirstOrDefault()?.Expression as ObjectCreationExpressionSyntax;
                    var literals = attr?.ArgumentList?.Arguments.Select(a => (a.Expression as LiteralExpressionSyntax)?.Token.ValueText).ToList();
                    plan.Cors = literals is { Count: >= 3 } && literals.All(l => l != null)
                        ? new CorsPlan(literals[0]!, literals[1]!, literals[2]!)
                        : new CorsPlan("", "", "");
                    break;
                case "Add" when receiver.EndsWith("Filters", StringComparison.OrdinalIgnoreCase):
                    ClassifyFilter(call, path, plan, project);
                    break;
                case "Add" when receiver.EndsWith("MessageHandlers", StringComparison.OrdinalIgnoreCase):
                    plan.Items.Add(Item(project, InventorySeverity.Breaking, "STARTUP-HANDLER", path, call,
                        "Message handler global do Web API",
                        $"{Shorten(call.ToString())} registra um DelegatingHandler no pipeline do Web API.",
                        "Reescreva o handler como middleware do ASP.NET Core e registre com app.UseMiddleware<T>() no Program.cs."));
                    break;
                case "Replace" or "Add" when receiver.EndsWith("Services", StringComparison.OrdinalIgnoreCase) && call.ToString().Contains("Exception"):
                    plan.Items.Add(Item(project, InventorySeverity.Breaking, "STARTUP-EXCEPTION", path, call,
                        "Tratamento global de exceções do Web API",
                        $"{Shorten(call.ToString())}",
                        "Implemente Microsoft.AspNetCore.Diagnostics.IExceptionHandler e registre com builder.Services.AddExceptionHandler<T>() + app.UseExceptionHandler()."));
                    break;
            }
        }
    }

    private static void ClassifyFilter(InvocationExpressionSyntax call, string path, StartupPlan plan, string project)
    {
        var arg = call.ArgumentList.Arguments.FirstOrDefault()?.Expression;
        var typeName = arg is ObjectCreationExpressionSyntax oc ? Simple(oc.Type.ToString()) : arg?.ToString() ?? "";
        switch (typeName)
        {
            case "HandleErrorAttribute" or "HandleError":
                return;
            case "AuthorizeAttribute" or "Authorize":
                plan.GlobalAuthorize = true;
                return;
            case "RequireHttpsAttribute" or "RequireHttps":
                plan.RequireHttps = true;
                return;
            default:
                plan.CustomGlobalFilters.Add(arg?.ToString() ?? call.ToString());
                plan.Items.Add(Item(project, InventorySeverity.Breaking, "STARTUP-FILTER", path, call,
                    $"Filtro global customizado: {typeName}",
                    "Filtros MVC/Web API mudaram de assinatura; o registro foi deixado comentado no Program.cs.",
                    "Porte o filtro para Microsoft.AspNetCore.Mvc.Filters (IActionFilter/IAsyncActionFilter) e descomente o registro em AddControllers(o => o.Filters.Add(...))."));
                return;
        }
    }

    private static MvcRoute? ParseMapRoute(InvocationExpressionSyntax call, string? area)
    {
        var name = (Argument(call, "name", 0) as LiteralExpressionSyntax)?.Token.ValueText;
        var url = (Argument(call, "url", 1) as LiteralExpressionSyntax)?.Token.ValueText;
        if (name == null || url == null) return null;

        var defaults = ReadAnonymous(Argument(call, "defaults", 2));
        var constraintsExpr = Argument(call, "constraints", 3);
        if (constraintsExpr is ImplicitArrayCreationExpressionSyntax or ArrayCreationExpressionSyntax) constraintsExpr = null;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pattern = RouteSegment().Replace(url, m =>
        {
            var catchAll = m.Groups[1].Value;
            var seg = m.Groups[2].Value;
            used.Add(seg);
            if (catchAll.Length > 0) return m.Value;
            if (!defaults.TryGetValue(seg, out var value)) return $"{{{seg}}}";
            if (value.EndsWith("Parameter.Optional", StringComparison.Ordinal)) return $"{{{seg}?}}";
            var literal = value.Trim('"');
            return value.StartsWith('"') ? $"{{{seg}={literal}}}" : $"{{{seg}}}";
        });

        var extra = defaults.Where(d => !used.Contains(d.Key) && !d.Value.EndsWith("Parameter.Optional", StringComparison.Ordinal))
            .Select(d => $"{d.Key} = {d.Value}").ToList();
        var extraDefaults = extra.Count > 0 ? $"new {{ {string.Join(", ", extra)} }}" : null;

        string? constraints = null;
        if (constraintsExpr is AnonymousObjectCreationExpressionSyntax anon)
        {
            var parts = anon.Initializers.Select(i =>
            {
                var key = i.NameEquals?.Name.Identifier.Text ?? i.Expression.ToString();
                if (i.Expression is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    var regex = l.Token.ValueText;
                    if (!regex.StartsWith('^')) regex = $"^({regex})$";
                    return $"{key} = @\"{regex.Replace("\"", "\"\"")}\"";
                }
                return $"{key} = {i.Expression}";
            });
            constraints = $"new {{ {string.Join(", ", parts)} }}";
        }

        return new MvcRoute(name, pattern, extraDefaults, constraints, area);
    }

    private static Dictionary<string, string> ReadAnonymous(ExpressionSyntax? expr)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (expr is not AnonymousObjectCreationExpressionSyntax anon) return result;
        foreach (var init in anon.Initializers)
        {
            var key = init.NameEquals?.Name.Identifier.Text ?? init.Expression.ToString().Split('.').Last();
            result[key] = init.Expression.ToString();
        }
        return result;
    }

    private static string? AreaNameOf(SyntaxNode node)
    {
        var cls = node.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault();
        if (cls?.BaseList?.Types.Any(t => t.Type.ToString().EndsWith("AreaRegistration", StringComparison.Ordinal)) != true) return null;
        var prop = cls.Members.OfType<PropertyDeclarationSyntax>().FirstOrDefault(p => p.Identifier.Text == "AreaName");
        return prop?.DescendantNodes().OfType<LiteralExpressionSyntax>().FirstOrDefault()?.Token.ValueText;
    }

    private static void AnalyzeGlobalAsax(SyntaxNode root, string path, StartupPlan plan, string project)
    {
        var app = root.DescendantNodes().OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(c => c.BaseList?.Types.Any(t => t.Type.ToString().EndsWith("HttpApplication", StringComparison.Ordinal)) == true);
        if (app == null) return;

        foreach (var method in app.Members.OfType<MethodDeclarationSyntax>())
        {
            var name = method.Identifier.Text;
            var statements = method.Body?.Statements ?? default;
            if (name is "Application_Start" or "Application_OnStart")
            {
                foreach (var statement in statements)
                {
                    var text = Regex.Replace(statement.ToString(), @"\s+", " ").Trim();
                    if (KnownStartupCalls.Any(k => text.Contains(k, StringComparison.Ordinal))) continue;
                    plan.PendingStatements.Add((path, text));
                }
                if (plan.PendingStatements.Count > 0)
                    plan.Items.Add(new InventoryItem
                    {
                        Project = project, Severity = InventorySeverity.Warning, Category = InventoryCategory.Startup, RuleId = "STARTUP-APPSTART",
                        Title = "Código de inicialização do Application_Start",
                        Description = $"{plan.PendingStatements.Count} instrução(ões) do Global.asax.cs não puderam ser classificadas e foram copiadas como comentário no Program.cs: " +
                                      string.Join(" | ", plan.PendingStatements.Select(p => Shorten(p.Statement))),
                        Suggestion = "Revise cada instrução marcada com 'TODO Migrator' no Program.cs e porte para o equivalente (registro no DI, configuração ou inicialização).",
                        FilePath = path, Line = Line(method)
                    });
                continue;
            }

            if (statements.Count == 0) continue;
            var key = GlobalAsaxEvents.Keys.FirstOrDefault(k => name.Equals(k, StringComparison.Ordinal) || name.StartsWith(k + "_", StringComparison.Ordinal));
            if (key == null) continue;
            var (severity, hint) = GlobalAsaxEvents[key];
            plan.Items.Add(new InventoryItem
            {
                Project = project, Severity = severity, Category = InventoryCategory.Startup, RuleId = "STARTUP-EVENT",
                Title = $"Evento {name} do Global.asax",
                Description = $"O evento tem {statements.Count} instrução(ões) que não existem no pipeline do ASP.NET Core.",
                Suggestion = hint, FilePath = path, Line = Line(method)
            });
        }
    }

    private static void AnalyzeOwin(SyntaxNode root, string path, StartupPlan plan, string project)
    {
        var configMethods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.ParameterList.Parameters.Any(p => p.Type?.ToString().EndsWith("IAppBuilder", StringComparison.Ordinal) == true));

        foreach (var method in configMethods)
        {
            var appParam = method.ParameterList.Parameters.First(p => p.Type!.ToString().EndsWith("IAppBuilder", StringComparison.Ordinal)).Identifier.Text;
            foreach (var call in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (call.Expression is not MemberAccessExpressionSyntax ma || ma.Expression.ToString() != appParam) continue;
                var name = MethodName(call);
                var (severity, hint) = OwinHints.TryGetValue(name, out var known)
                    ? known
                    : name.StartsWith("Use", StringComparison.Ordinal) || name is "Map" or "Run" or "MapWhen"
                        ? (InventorySeverity.Breaking, "Middleware OWIN customizado: reescreva como middleware do ASP.NET Core (classe com InvokeAsync(HttpContext) + app.UseMiddleware<T>()).")
                        : (InventorySeverity.Warning, "Revise e porte para o Program.cs.");
                plan.Items.Add(Item(project, severity, "STARTUP-OWIN", path, call, $"OWIN: app.{name}(...)", Shorten(call.ToString()), hint));
            }
        }
    }

    private static void AnalyzeDiRegistrations(SyntaxNode root, string text, string path, StartupPlan plan, string project)
    {
        var converted = 0;
        var pending = new List<string>();
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = MethodName(call);
            if (name == "RegisterType" && call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax g })
            {
                var args = g.TypeArgumentList.Arguments.Select(a => a.ToString()).ToList();
                var lifetime = call.ArgumentList.ToString() switch
                {
                    var s when s.Contains("ContainerControlledLifetimeManager") || s.Contains("SingletonLifetimeManager") => "AddSingleton",
                    var s when s.Contains("HierarchicalLifetimeManager") || s.Contains("PerRequestLifetimeManager") || s.Contains("PerResolveLifetimeManager") => "AddScoped",
                    var s when s.Contains("InjectionConstructor") || s.Contains("InjectionFactory") => null,
                    _ => "AddTransient"
                };
                if (lifetime == null || args.Count is 0 or > 2) { pending.Add(call.ToString()); continue; }
                plan.DiRegistrations.Add($"builder.Services.{lifetime}<{string.Join(", ", args)}>();");
                converted++;
            }
            else if (name == "To" && call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax to, Expression: InvocationExpressionSyntax bind } &&
                     bind.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.Text: "Bind" } bindName })
            {
                var chain = call.Parent?.Parent is InvocationExpressionSyntax scoped ? scoped.ToString() : call.ToString();
                var lifetime = chain.Contains("InSingletonScope") ? "AddSingleton"
                    : chain.Contains("InRequestScope") || chain.Contains("InThreadScope") ? "AddScoped"
                    : "AddTransient";
                plan.DiRegistrations.Add($"builder.Services.{lifetime}<{bindName.TypeArgumentList.Arguments[0]}, {to.TypeArgumentList.Arguments[0]}>();");
                converted++;
            }
        }

        if (converted == 0 && pending.Count == 0) return;

        foreach (var u in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var ns = u.Name?.ToString() ?? "";
            if (ns.Length == 0 || u.Alias != null || u.StaticKeyword.IsKind(SyntaxKind.StaticKeyword)) continue;
            if (ns.StartsWith("System.Web") || ns.StartsWith("Unity") || ns.StartsWith("Ninject") || ns.StartsWith("Microsoft.Practices") ||
                ns.StartsWith("Owin") || ns.StartsWith("Microsoft.Owin") || ns.StartsWith("WebActivatorEx") || ns.StartsWith("CommonServiceLocator"))
                continue;
            plan.DiNamespaces.Add(ns);
        }
        var fileNamespace = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        if (fileNamespace != null) plan.DiNamespaces.Add(fileNamespace);

        if (converted > 0)
            plan.Items.Add(new InventoryItem
            {
                Project = project, Severity = InventorySeverity.Warning, Category = InventoryCategory.Startup, RuleId = "STARTUP-DI",
                Title = $"{converted} registro(s) de DI convertidos para builder.Services",
                Description = $"Registros de Unity/Ninject em {path} foram convertidos para o DI nativo no Program.cs.",
                Suggestion = "Confira os tempos de vida: Unity sem LifetimeManager e Ninject sem escopo viraram AddTransient; HierarchicalLifetimeManager/PerRequest/InRequestScope viraram AddScoped; ContainerControlled/InSingletonScope viraram AddSingleton.",
                FilePath = path, AutoMigrated = false
            });
        if (pending.Count > 0)
            plan.Items.Add(new InventoryItem
            {
                Project = project, Severity = InventorySeverity.Breaking, Category = InventoryCategory.Startup, RuleId = "STARTUP-DI-MANUAL",
                Title = $"{pending.Count} registro(s) de DI com configuração avançada",
                Description = "Registros com InjectionConstructor/InjectionFactory ou genéricos abertos: " + string.Join(" | ", pending.Select(Shorten)),
                Suggestion = "Use fábricas no DI nativo: builder.Services.AddScoped<IFoo>(sp => new Foo(...)).",
                FilePath = path
            });
    }

    private static string ConvertApiTemplate(string template)
    {
        var converted = template.Replace("{controller}", "[controller]").Replace("{action}", "[action]");
        converted = Regex.Replace(converted, @"/?\{id\??\}", "");
        converted = converted.TrimEnd('/');
        return converted.Contains("[controller]") ? converted : "api/[controller]";
    }

    private static ExpressionSyntax? Argument(InvocationExpressionSyntax call, string name, int position)
    {
        var args = call.ArgumentList.Arguments;
        var named = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.Text == name);
        if (named != null) return named.Expression;
        return position < args.Count && args[position].NameColon == null ? args[position].Expression : null;
    }

    private static string MethodName(InvocationExpressionSyntax call) => call.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => ""
    };

    private static string Simple(string typeName) => typeName.Split('.').Last();

    private static InventoryItem Item(string project, InventorySeverity severity, string rule, string path, SyntaxNode node, string title, string description, string suggestion) => new()
    {
        Project = project, Severity = severity, Category = InventoryCategory.Startup, RuleId = rule,
        Title = title, Description = description, Suggestion = suggestion, FilePath = path, Line = Line(node)
    };

    private static int Line(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    internal static string Shorten(string text)
    {
        var single = Regex.Replace(text, @"\s+", " ").Trim();
        return single.Length <= 160 ? single : single[..157] + "...";
    }

    [GeneratedRegex(@"\{(\*?)(\w+)\}")]
    private static partial Regex RouteSegment();

    [GeneratedRegex(@"Formatters\.Remove\([^)]*XmlFormatter|XmlFormatter\.SupportedMediaTypes\.Clear\(\)")]
    private static partial Regex XmlFormatterRemoved();

    [GeneratedRegex(@"\.LowercaseUrls\s*=\s*true")]
    private static partial Regex LowercaseUrls();
}
