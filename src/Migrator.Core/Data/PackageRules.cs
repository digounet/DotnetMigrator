using System.Text.RegularExpressions;
using Migrator.Core.Models;

namespace Migrator.Core.Data;

public enum PackageAction { Keep, Replace, Remove, Manual }

public enum VersionPolicyKind { SameIfCompatible, DotNet, LatestMajor, Latest, Fixed }

public sealed record VersionPolicy(VersionPolicyKind Kind, string Fallback, int Major = 0, string? MaxExclusive = null)
{
    public static readonly VersionPolicy DotNet = new(VersionPolicyKind.DotNet, "10.0.0", 10);
    public static VersionPolicy SameIfCompatible(string? maxExclusive = null) => new(VersionPolicyKind.SameIfCompatible, "", MaxExclusive: maxExclusive);
    public static VersionPolicy LatestMajor(int major, string fallback) => new(VersionPolicyKind.LatestMajor, fallback, major);
    public static VersionPolicy Latest(string fallback, string? maxExclusive = null) => new(VersionPolicyKind.Latest, fallback, MaxExclusive: maxExclusive);
    public static VersionPolicy Fixed(string version) => new(VersionPolicyKind.Fixed, version);
}

public sealed record PackageTarget(string Id, VersionPolicy Version);

public sealed record PackageRule(
    PackageAction Action,
    string Guidance,
    InventorySeverity? Severity = null,
    VersionPolicy? Version = null,
    PackageTarget[]? Replacements = null);

public static partial class PackageRules
{
    private const string BuiltInAspNetCore = "Incluído no framework compartilhado do ASP.NET Core (Microsoft.AspNetCore.App); o pacote foi removido.";
    private const string ClientSide = "Pacote de conteúdo client-side: os arquivos já estão no projeto (movidos para wwwroot). Gerencie bibliotecas front-end com LibMan ou npm.";
    private const string Inbox = "Já faz parte do runtime do .NET 10; o pacote foi removido.";
    private const string NotNeeded = "Não é necessário em projetos SDK-style do .NET 10; o pacote foi removido.";

    private static readonly PackageRule DiContainerManual = new(PackageAction.Manual,
        "O container de DI do ASP.NET clássico não tem integração com o .NET 10. Reescreva os registros com builder.Services (AddScoped/AddTransient/AddSingleton) no Program.cs. " +
        "Alternativa de menor esforço: Autofac (Autofac.Extensions.DependencyInjection) ou Unity.Microsoft.DependencyInjection.");

    private static readonly PackageRule OwinManual = new(PackageAction.Manual,
        "Componente OWIN/Katana sem equivalente direto. Porte a configuração do Startup OWIN para middlewares do ASP.NET Core no Program.cs.");

    private static PackageRule Remove(string guidance, InventorySeverity severity = InventorySeverity.Info) => new(PackageAction.Remove, guidance, severity);
    private static PackageRule Keep(string guidance, VersionPolicy? version = null, InventorySeverity? severity = null) => new(PackageAction.Keep, guidance, severity, version);
    private static PackageRule Replace(string guidance, params PackageTarget[] targets) => new(PackageAction.Replace, guidance, Replacements: targets);
    private static PackageRule Manual(string guidance) => new(PackageAction.Manual, guidance);
    private static PackageTarget To(string id, VersionPolicy? version = null) => new(id, version ?? VersionPolicy.Latest("1.0.0"));
    private static PackageTarget ToDotNet(string id) => new(id, VersionPolicy.DotNet);

    public static readonly Dictionary<string, PackageRule> ById = new(StringComparer.OrdinalIgnoreCase)
    {
        // ASP.NET MVC / Web API / WebPages
        ["Microsoft.AspNet.Mvc"] = Remove(BuiltInAspNetCore),
        ["Microsoft.AspNet.Razor"] = Remove(BuiltInAspNetCore),
        ["Microsoft.AspNet.WebPages"] = Remove(BuiltInAspNetCore),
        ["Microsoft.Web.Infrastructure"] = Remove(NotNeeded),
        ["Microsoft.AspNet.WebApi"] = Remove(BuiltInAspNetCore),
        ["Microsoft.AspNet.WebApi.Core"] = Remove(BuiltInAspNetCore),
        ["Microsoft.AspNet.WebApi.WebHost"] = Remove(BuiltInAspNetCore),
        ["Microsoft.AspNet.WebApi.Owin"] = Remove(BuiltInAspNetCore),
        ["Microsoft.AspNet.WebApi.OwinSelfHost"] = Remove("Self-host OWIN substituído pelo Kestrel (WebApplication.CreateBuilder)."),
        ["Microsoft.AspNet.WebApi.SelfHost"] = Remove("Self-host substituído pelo Kestrel (WebApplication.CreateBuilder)."),
        ["Microsoft.AspNet.WebApi.Cors"] = Remove("CORS é nativo do ASP.NET Core: builder.Services.AddCors() + app.UseCors()."),
        ["Microsoft.AspNet.Cors"] = Remove("CORS é nativo do ASP.NET Core: builder.Services.AddCors() + app.UseCors()."),
        ["Microsoft.AspNet.WebApi.Tracing"] = Remove("Use ILogger / OpenTelemetry."),
        ["Microsoft.AspNet.WebApi.HelpPage"] = Remove("Help Page não existe no ASP.NET Core. Use OpenAPI (Swashbuckle.AspNetCore ou Microsoft.AspNetCore.OpenApi)."),
        ["Microsoft.AspNet.WebApi.Client"] = Keep("System.Net.Http.Formatting (ReadAsAsync/PostAsJsonAsync). Considere migrar para System.Net.Http.Json (ReadFromJsonAsync), nativo do .NET."),
        ["Microsoft.AspNet.WebApi.Versioning"] = Replace("API mudou de namespace (Asp.Versioning). Reconfigure com builder.Services.AddApiVersioning().AddMvc().", To("Asp.Versioning.Mvc")),
        ["Microsoft.AspNet.OData"] = Replace("OData para ASP.NET Core: configure com AddOData() no AddControllers(); rotas e EDM mudaram.", To("Microsoft.AspNetCore.OData")),
        ["Microsoft.AspNet.WebApi.OData"] = Replace("OData v3 não é suportado; migre para OData v4 com Microsoft.AspNetCore.OData.", To("Microsoft.AspNetCore.OData")),
        ["Microsoft.AspNet.Web.Optimization"] = Remove("Bundling/minificação do System.Web não existe no .NET 10. As views foram reescritas para referenciar os arquivos diretamente; para produção use WebOptimizer, Vite ou esbuild."),
        ["Microsoft.AspNet.Web.Optimization.WebForms"] = Remove(NotNeeded),
        ["WebGrease"] = Remove("Dependência do bundling do System.Web; removido."),
        ["Antlr"] = Remove("Dependência do WebGrease; removido."),
        ["Microsoft.AspNet.TelemetryCorrelation"] = Remove("Correlação de telemetria é nativa (System.Diagnostics.Activity)."),
        ["Microsoft.CodeDom.Providers.DotNetCompilerPlatform"] = Remove(NotNeeded),
        ["Microsoft.Net.Compilers"] = Remove("O compilador vem com o SDK do .NET 10."),
        ["Microsoft.Net.Compilers.Toolset"] = Remove("O compilador vem com o SDK do .NET 10."),
        ["Microsoft.CodeAnalysis.FxCopAnalyzers"] = Remove("Os analisadores .NET já vêm no SDK (Microsoft.CodeAnalysis.NetAnalyzers)."),
        ["WebActivatorEx"] = Remove("Não há PreApplicationStart no ASP.NET Core; o código de inicialização deve ir para o Program.cs."),
        ["Microsoft.AspNet.FriendlyUrls"] = Manual("WebForms não é suportado no .NET 10."),
        ["Microsoft.AspNet.FriendlyUrls.Core"] = Manual("WebForms não é suportado no .NET 10."),
        ["AjaxControlToolkit"] = Manual("WebForms não é suportado no .NET 10. Reescreva as páginas em Razor Pages/MVC/Blazor."),
        ["Microsoft.AspNet.Providers.Core"] = Manual("Universal Providers (Membership/Roles/Profile) não existem no .NET 10. Use ASP.NET Core Identity."),
        ["System.Web.Providers"] = Manual("Universal Providers (Membership/Roles/Profile) não existem no .NET 10. Use ASP.NET Core Identity."),
        ["Microsoft.AspNet.Mvc.Futures"] = Manual("Sem equivalente no ASP.NET Core; revise os usos."),
        ["Microsoft.AspNet.WebHelpers"] = Manual("System.Web.Helpers não existe no .NET 10 (WebGrid, Crypto, Json helpers)."),
        ["MvcSiteMapProvider.MVC5"] = Manual("Sem versão para ASP.NET Core. Avalie SmartBreadcrumbs ou um menu/breadcrumb próprio via ViewComponent."),
        ["PagedList.Mvc"] = Replace("API semelhante (IPagedList); ajuste os helpers de paginação nas views.", To("X.PagedList.Mvc.Core")),
        ["PagedList"] = Replace("API semelhante (ToPagedList).", To("X.PagedList")),

        // Identity / SignalR
        ["Microsoft.AspNet.Identity.Core"] = Manual("ASP.NET Identity 2 depende de System.Web/OWIN. Migre para ASP.NET Core Identity (Microsoft.AspNetCore.Identity.EntityFrameworkCore): o schema das tabelas AspNet* muda (NormalizedUserName, ConcurrencyStamp...) e exige migration; os hashes de senha do Identity 2 continuam válidos (PasswordHasher em modo de compatibilidade)."),
        ["Microsoft.AspNet.Identity.EntityFramework"] = Manual("Migre para Microsoft.AspNetCore.Identity.EntityFrameworkCore (EF Core). O schema muda e exige migration de dados."),
        ["Microsoft.AspNet.Identity.Owin"] = Manual("SignInManager/UserManager do ASP.NET Core Identity substituem o pipeline OWIN."),
        ["Microsoft.AspNet.SignalR"] = Manual("SignalR do ASP.NET Core é nativo (MapHub<T>), mas o protocolo é incompatível com o SignalR clássico: os clientes JavaScript/.NET precisam usar @microsoft/signalr / Microsoft.AspNetCore.SignalR.Client e a API dos hubs muda (sem dynamic, use SendAsync)."),
        ["Microsoft.AspNet.SignalR.Core"] = Manual("Veja Microsoft.AspNet.SignalR: servidor nativo no ASP.NET Core, protocolo e clientes incompatíveis."),
        ["Microsoft.AspNet.SignalR.SystemWeb"] = Remove(BuiltInAspNetCore),
        ["Microsoft.AspNet.SignalR.JS"] = Remove("Cliente JS do SignalR clássico é incompatível; use o pacote npm @microsoft/signalr."),
        ["Microsoft.AspNet.SignalR.Client"] = Replace("Cliente do SignalR clássico é incompatível com o servidor ASP.NET Core.", ToDotNet("Microsoft.AspNetCore.SignalR.Client")),

        // OWIN / Katana
        ["Owin"] = Remove("OWIN não é usado no ASP.NET Core."),
        ["Microsoft.Owin"] = Remove("Pipeline OWIN substituído pelo pipeline de middlewares do ASP.NET Core."),
        ["Microsoft.Owin.Host.SystemWeb"] = Remove("O ASP.NET Core tem host próprio (Kestrel/IIS)."),
        ["Microsoft.Owin.Host.HttpListener"] = Remove("Use Kestrel ou HTTP.sys (UseHttpSys)."),
        ["Microsoft.Owin.Hosting"] = Remove("Use WebApplication.CreateBuilder."),
        ["Microsoft.Owin.Security"] = Remove("Autenticação é nativa do ASP.NET Core (AddAuthentication)."),
        ["Microsoft.Owin.Security.Cookies"] = Remove("Use builder.Services.AddAuthentication().AddCookie() (nativo)."),
        ["Microsoft.Owin.Cors"] = Remove("CORS é nativo do ASP.NET Core: AddCors()/UseCors()."),
        ["Microsoft.Owin.StaticFiles"] = Remove("Use app.UseStaticFiles() (nativo)."),
        ["Microsoft.Owin.FileSystems"] = Remove("Use IFileProvider (nativo)."),
        ["Microsoft.Owin.Diagnostics"] = Remove("Use app.UseDeveloperExceptionPage() (nativo)."),
        ["Microsoft.Owin.Security.OAuth"] = Manual("O servidor de autorização OAuth do OWIN (OAuthAuthorizationServerProvider / UseOAuthBearerTokens) não tem equivalente no ASP.NET Core. Use OpenIddict, Duende IdentityServer ou Microsoft Entra ID; para validar tokens JWT use AddJwtBearer."),
        ["Microsoft.Owin.Security.Jwt"] = Replace("Configure builder.Services.AddAuthentication().AddJwtBearer(...).", ToDotNet("Microsoft.AspNetCore.Authentication.JwtBearer")),
        ["Microsoft.Owin.Security.OpenIdConnect"] = Replace("Configure AddAuthentication().AddOpenIdConnect(...) ou use Microsoft.Identity.Web para Entra ID.", ToDotNet("Microsoft.AspNetCore.Authentication.OpenIdConnect")),
        ["Microsoft.Owin.Security.ActiveDirectory"] = Replace("Use builder.Services.AddMicrosoftIdentityWebApiAuthentication(builder.Configuration).", To("Microsoft.Identity.Web")),
        ["Microsoft.Owin.Security.WsFederation"] = Replace("Configure AddAuthentication().AddWsFederation(...).", ToDotNet("Microsoft.AspNetCore.Authentication.WsFederation")),
        ["Microsoft.Owin.Security.Google"] = Replace("Configure AddAuthentication().AddGoogle(...).", ToDotNet("Microsoft.AspNetCore.Authentication.Google")),
        ["Microsoft.Owin.Security.Facebook"] = Replace("Configure AddAuthentication().AddFacebook(...).", ToDotNet("Microsoft.AspNetCore.Authentication.Facebook")),
        ["Microsoft.Owin.Security.MicrosoftAccount"] = Replace("Configure AddAuthentication().AddMicrosoftAccount(...).", ToDotNet("Microsoft.AspNetCore.Authentication.MicrosoftAccount")),
        ["Microsoft.Owin.Security.Twitter"] = Replace("Configure AddAuthentication().AddTwitter(...).", ToDotNet("Microsoft.AspNetCore.Authentication.Twitter")),

        // OpenAPI
        ["Swashbuckle"] = Replace("Configure builder.Services.AddSwaggerGen() e app.UseSwagger()/UseSwaggerUI() no Program.cs; o SwaggerConfig.cs (App_Start) foi movido para _Legacy.", To("Swashbuckle.AspNetCore", VersionPolicy.Latest("10.2.3"))),
        ["Swashbuckle.Core"] = Replace("Veja Swashbuckle.", To("Swashbuckle.AspNetCore", VersionPolicy.Latest("10.2.3"))),

        // DI containers
        ["Unity"] = DiContainerManual,
        ["Unity.Container"] = DiContainerManual,
        ["Unity.Abstractions"] = DiContainerManual,
        ["Unity.Mvc"] = DiContainerManual,
        ["Unity.Mvc5"] = DiContainerManual,
        ["Unity.Mvc4"] = DiContainerManual,
        ["Unity.WebApi"] = DiContainerManual,
        ["Unity.AspNet.WebApi"] = DiContainerManual,
        ["CommonServiceLocator"] = DiContainerManual,
        ["Ninject"] = DiContainerManual,
        ["Ninject.MVC5"] = DiContainerManual,
        ["Ninject.MVC3"] = DiContainerManual,
        ["Ninject.Web.Common"] = DiContainerManual,
        ["Ninject.Web.Common.WebHost"] = DiContainerManual,
        ["Ninject.Web.WebApi"] = DiContainerManual,
        ["Ninject.Web.WebApi.WebHost"] = DiContainerManual,
        ["StructureMap"] = DiContainerManual,
        ["StructureMap.MVC5"] = DiContainerManual,
        ["structuremap.web"] = DiContainerManual,
        ["Autofac.Mvc5"] = Replace("Use builder.Host.UseServiceProviderFactory(new AutofacServiceProviderFactory()) e builder.Host.ConfigureContainer<ContainerBuilder>(...).", To("Autofac.Extensions.DependencyInjection")),
        ["Autofac.WebApi2"] = Replace("Veja Autofac.Mvc5.", To("Autofac.Extensions.DependencyInjection")),
        ["Autofac.Owin"] = Replace("Veja Autofac.Mvc5.", To("Autofac.Extensions.DependencyInjection")),
        ["Autofac.Mvc5.Owin"] = Replace("Veja Autofac.Mvc5.", To("Autofac.Extensions.DependencyInjection")),
        ["Autofac.WebApi2.Owin"] = Replace("Veja Autofac.Mvc5.", To("Autofac.Extensions.DependencyInjection")),
        ["SimpleInjector.Integration.Web.Mvc"] = Replace("Use services.AddSimpleInjector(container, o => o.AddAspNetCore().AddControllerActivation()).", To("SimpleInjector.Integration.AspNetCore.Mvc")),
        ["SimpleInjector.Integration.WebApi"] = Replace("Veja SimpleInjector.Integration.Web.Mvc.", To("SimpleInjector.Integration.AspNetCore.Mvc")),
        ["SimpleInjector.Integration.Web"] = Replace("Veja SimpleInjector.Integration.Web.Mvc.", To("SimpleInjector.Integration.AspNetCore.Mvc")),
        ["Castle.Windsor"] = Keep("Para integrar com o DI do .NET use Castle.Windsor.Extensions.DependencyInjection.", severity: InventorySeverity.Warning),

        // Data
        ["EntityFramework"] = Keep("EF6 6.5 roda no .NET 10 (mantido para reduzir risco). Pontos de atenção: o EF Designer/EDMX não funciona em projetos SDK-style; DbContext(\"name=X\") não lê o web.config — passe a connection string vinda do IConfiguration; providers configurados em <entityFramework> devem ir para uma classe DbConfiguration. Migrar para EF Core 10 é recomendado numa etapa posterior.",
            VersionPolicy.LatestMajor(6, "6.5.1"), InventorySeverity.Warning),
        ["EntityFramework.SqlServerCompact"] = Manual("SQL Server Compact não é suportado no .NET 10. Use SQLite ou SQL Server LocalDB."),
        ["Microsoft.SqlServer.Compact"] = Manual("SQL Server Compact não é suportado no .NET 10. Use SQLite ou SQL Server LocalDB."),
        ["System.Data.SqlClient"] = Replace("Microsoft.Data.SqlClient é o provider suportado. A partir da v4, Encrypt=true é o padrão: as connection strings migradas receberam Encrypt=False para preservar o comportamento anterior.", To("Microsoft.Data.SqlClient", VersionPolicy.Latest("7.1.1"))),
        ["Oracle.ManagedDataAccess"] = Replace("Versão .NET (Core) do ODP.NET; API compatível.", To("Oracle.ManagedDataAccess.Core")),
        ["Oracle.ManagedDataAccess.EntityFramework"] = Manual("EF6 para Oracle não suporta .NET (Core). Use Oracle.EntityFrameworkCore (EF Core)."),

        // Logging / diagnostics
        ["log4net"] = Keep("log4net no .NET não lê a seção <log4net> do web.config/app.config: ela foi extraída para log4net.config. Configure com XmlConfigurator.Configure(new FileInfo(\"log4net.config\")) ou use Microsoft.Extensions.Logging.Log4Net.AspNetCore.", severity: InventorySeverity.Warning),
        ["NLog.Web"] = Replace("Use builder.Logging.ClearProviders(); builder.Host.UseNLog();", To("NLog.Web.AspNetCore")),
        ["NLog.Config"] = Remove("Pacote de conteúdo (nlog.config); a seção <nlog> foi extraída para nlog.config."),
        ["NLog.Schema"] = Remove("Pacote apenas de schema XSD."),
        ["SerilogWeb.Classic"] = Replace("Use builder.Host.UseSerilog() e app.UseSerilogRequestLogging().", To("Serilog.AspNetCore")),
        ["SerilogWeb.Classic.Mvc"] = Replace("Veja SerilogWeb.Classic.", To("Serilog.AspNetCore")),
        ["SerilogWeb.Classic.WebApi"] = Replace("Veja SerilogWeb.Classic.", To("Serilog.AspNetCore")),
        ["Elmah"] = Manual("ELMAH clássico depende de System.Web. Use logging estruturado (Serilog/Application Insights) ou ElmahCore."),
        ["elmah.corelibrary"] = Manual("ELMAH clássico depende de System.Web. Use logging estruturado ou ElmahCore."),
        ["Elmah.Mvc"] = Manual("ELMAH clássico depende de System.Web. Use logging estruturado ou ElmahCore."),
        ["Elmah.Contrib.WebApi"] = Manual("ELMAH clássico depende de System.Web. Use logging estruturado ou ElmahCore."),
        ["MiniProfiler.Mvc4"] = Replace("Configure AddMiniProfiler() e app.UseMiniProfiler().", To("MiniProfiler.AspNetCore.Mvc")),
        ["MiniProfiler.Mvc5"] = Replace("Configure AddMiniProfiler() e app.UseMiniProfiler().", To("MiniProfiler.AspNetCore.Mvc")),
        ["Microsoft.ApplicationInsights.Web"] = Replace("Use builder.Services.AddApplicationInsightsTelemetry(). Avalie Azure Monitor OpenTelemetry (Azure.Monitor.OpenTelemetry.AspNetCore) para novos desenvolvimentos.", To("Microsoft.ApplicationInsights.AspNetCore")),
        ["Microsoft.ApplicationInsights.WindowsServer"] = Remove("Incluído em Microsoft.ApplicationInsights.AspNetCore / WorkerService."),
        ["Microsoft.ApplicationInsights.WindowsServer.TelemetryChannel"] = Remove("Incluído em Microsoft.ApplicationInsights.AspNetCore / WorkerService."),
        ["Microsoft.ApplicationInsights.DependencyCollector"] = Remove("Incluído em Microsoft.ApplicationInsights.AspNetCore / WorkerService."),
        ["Microsoft.ApplicationInsights.PerfCounterCollector"] = Remove("Incluído em Microsoft.ApplicationInsights.AspNetCore / WorkerService."),
        ["Microsoft.ApplicationInsights.Agent.Intercept"] = Remove("Incluído em Microsoft.ApplicationInsights.AspNetCore / WorkerService."),

        // Licensing-sensitive packages: stay on the last free major
        ["AutoMapper"] = Keep("A partir da v15 o AutoMapper exige licença comercial; a ferramenta mantém versões < 15.", VersionPolicy.SameIfCompatible("15.0.0")),
        ["MediatR"] = Keep("A partir da v13 o MediatR exige licença comercial; a ferramenta mantém versões < 13.", VersionPolicy.SameIfCompatible("13.0.0")),
        ["FluentAssertions"] = Keep("A partir da v8 o FluentAssertions exige licença comercial; a ferramenta mantém versões < 8.", VersionPolicy.SameIfCompatible("8.0.0")),
        ["EPPlus"] = Keep("A partir da v5 o EPPlus usa licença Polyform (comercial); a ferramenta mantém versões < 5. Avalie ClosedXML.", VersionPolicy.SameIfCompatible("5.0.0")),
        ["MassTransit"] = Keep("A partir da v9 o MassTransit exige licença comercial; a ferramenta mantém versões < 9.", VersionPolicy.SameIfCompatible("9.0.0")),
        ["Quartz"] = Keep("Quartz 2.x → 3.x tem API assíncrona (IJob.Execute retorna Task).", VersionPolicy.SameIfCompatible("4.0.0")),
        ["NUnit"] = Keep("NUnit 2 → 3 muda atributos (TestFixtureSetUp → OneTimeSetUp, ExpectedException → Assert.Throws).", VersionPolicy.SameIfCompatible("4.0.0")),
        ["NUnitTestAdapter"] = Replace("Adapter do NUnit 2 substituído pelo NUnit3TestAdapter.", To("NUnit3TestAdapter")),

        // Azure / misc
        ["WindowsAzure.Storage"] = Keep("Pacote descontinuado. Migre para Azure.Storage.Blobs/Queues e Azure.Data.Tables.", severity: InventorySeverity.Warning),
        ["WindowsAzure.ServiceBus"] = Manual("Só suporta .NET Framework. Migre para Azure.Messaging.ServiceBus."),
        ["Microsoft.WindowsAzure.ConfigurationManager"] = Manual("Use IConfiguration (appsettings/variáveis de ambiente/Azure App Configuration)."),
        ["Microsoft.ReportViewer.WebForms"] = Manual("ReportViewer WebForms não existe no .NET 10. Renderize relatórios via API REST do SSRS ou use uma biblioteca de relatórios compatível."),
        ["Microsoft.ReportingServices.ReportViewerControl.WebForms"] = Manual("ReportViewer WebForms não existe no .NET 10. Renderize relatórios via API REST do SSRS ou use uma biblioteca de relatórios compatível."),
        ["Microsoft.ReportingServices.ReportViewerControl.WinForms"] = Keep("Requer Windows; verifique o suporte do fornecedor para .NET 10.", severity: InventorySeverity.Warning),
        ["Microsoft.Web.Xdt"] = Remove(NotNeeded),
        ["MSBuildTasks"] = Keep("Verifique se as tasks usadas funcionam com o MSBuild do .NET SDK.", severity: InventorySeverity.Warning),
    };

    private static readonly HashSet<string> ClientSidePackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "jQuery", "jQuery.Validation", "Microsoft.jQuery.Unobtrusive.Validation", "Microsoft.jQuery.Unobtrusive.Ajax",
        "bootstrap", "Modernizr", "Respond", "popper.js", "jquery.ui.combined", "jQuery.UI.Combined", "knockoutjs",
        "angularjs", "AngularJS.Core", "AngularJS.Route", "Moment.js", "FontAwesome", "font-awesome", "Microsoft.AspNet.SignalR.JS",
        "jquery.datatables", "Select2.js", "toastr", "underscore.js", "lodash", "Bootstrap.Datepicker", "jQuery.Migrate",
        "Microsoft.Web.Optimization.WebForms", "AspNet.ScriptManager.jQuery", "AspNet.ScriptManager.bootstrap",
        "Microsoft.AspNet.ScriptManager.MSAjax", "Microsoft.AspNet.ScriptManager.WebForms"
    };

    private static readonly HashSet<string> InboxPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        "System.Net.Http", "System.ValueTuple", "System.Memory", "System.Buffers", "System.Numerics.Vectors",
        "System.Runtime.CompilerServices.Unsafe", "System.Threading.Tasks.Extensions", "System.Text.Json",
        "System.Text.Encodings.Web", "System.Collections.Immutable", "System.Diagnostics.DiagnosticSource",
        "System.Reflection.Metadata", "System.Runtime.InteropServices.RuntimeInformation", "System.IO.Compression",
        "System.Security.Cryptography.Algorithms", "System.Security.Cryptography.Encoding", "System.Security.Cryptography.Primitives",
        "System.Security.Cryptography.X509Certificates", "System.IO", "System.Runtime", "System.Linq", "System.Console",
        "System.AppContext", "System.Net.Primitives", "System.Net.Sockets", "System.Xml.ReaderWriter", "System.Threading",
        "System.Threading.Thread", "System.Collections", "System.Collections.Concurrent", "System.Diagnostics.Debug",
        "System.Diagnostics.Tools", "System.Diagnostics.Tracing", "System.Globalization", "System.IO.FileSystem",
        "System.IO.FileSystem.Primitives", "System.Linq.Expressions", "System.ObjectModel", "System.Reflection",
        "System.Reflection.Extensions", "System.Reflection.Primitives", "System.Resources.ResourceManager",
        "System.Runtime.Extensions", "System.Runtime.Handles", "System.Runtime.InteropServices", "System.Runtime.Numerics",
        "System.Text.Encoding", "System.Text.Encoding.Extensions", "System.Text.RegularExpressions", "System.Threading.Timer",
        "System.Xml.XDocument", "System.Dynamic.Runtime", "System.ComponentModel.Annotations", "System.Data.Common",
        "System.Runtime.Serialization.Primitives", "System.Threading.Channels", "System.Net.WebHeaderCollection",
        "System.Security.Principal", "System.Security.SecureString", "System.Diagnostics.Process", "Microsoft.CSharp",
        "NETStandard.Library", "Microsoft.NETCore.Platforms", "Microsoft.NETCore.Targets", "Microsoft.Win32.Primitives",
        "Microsoft.Bcl", "Microsoft.Bcl.Build", "Microsoft.Bcl.AsyncInterfaces", "Microsoft.Bcl.HashCode", "Microsoft.Bcl.TimeProvider",
        "System.Net.Http.Json", "System.Formats.Asn1"
    };

    private static readonly HashSet<string> AspNetCoreSharedFramework = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft.AspNetCore", "Microsoft.AspNetCore.All", "Microsoft.AspNetCore.App", "Microsoft.AspNetCore.Mvc",
        "Microsoft.AspNetCore.Mvc.Core", "Microsoft.AspNetCore.Mvc.Abstractions", "Microsoft.AspNetCore.Mvc.Razor",
        "Microsoft.AspNetCore.Mvc.ViewFeatures", "Microsoft.AspNetCore.Mvc.TagHelpers", "Microsoft.AspNetCore.Mvc.Formatters.Json",
        "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Hosting.Abstractions", "Microsoft.AspNetCore.Http",
        "Microsoft.AspNetCore.Http.Abstractions", "Microsoft.AspNetCore.Http.Features", "Microsoft.AspNetCore.Routing",
        "Microsoft.AspNetCore.StaticFiles", "Microsoft.AspNetCore.Authentication", "Microsoft.AspNetCore.Authentication.Cookies",
        "Microsoft.AspNetCore.Authorization", "Microsoft.AspNetCore.Cors", "Microsoft.AspNetCore.Diagnostics",
        "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Server.IISIntegration", "Microsoft.AspNetCore.Server.IIS",
        "Microsoft.AspNetCore.Session", "Microsoft.AspNetCore.DataProtection", "Microsoft.AspNetCore.Identity",
        "Microsoft.AspNetCore.SignalR", "Microsoft.AspNetCore.WebUtilities", "Microsoft.AspNetCore.Antiforgery",
        "Microsoft.AspNetCore.Localization", "Microsoft.AspNetCore.HttpsPolicy", "Microsoft.AspNetCore.Rewrite",
        "Microsoft.AspNetCore.ResponseCaching", "Microsoft.AspNetCore.ResponseCompression", "Microsoft.AspNetCore.Html.Abstractions"
    };

    public static PackageRule? Find(string id)
    {
        if (ById.TryGetValue(id, out var rule)) return rule;
        if (ClientSidePackages.Contains(id)) return Remove(ClientSide);
        if (InboxPackages.Contains(id)) return Remove(Inbox);
        if (AspNetCoreSharedFramework.Contains(id)) return Remove(BuiltInAspNetCore);

        var culture = CultureSuffix().Match(id);
        if (culture.Success && IsFrameworkFamily(id[..culture.Index]))
            return Remove("Pacote de recursos localizados (mensagens de erro traduzidas) do ASP.NET/EF clássico; não é necessário no .NET 10.");

        if (id.StartsWith("Microsoft.Extensions.", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("Microsoft.AspNetCore.", StringComparison.OrdinalIgnoreCase))
            return Keep("Pacote alinhado às versões do .NET; atualizado para a linha 10.0.", VersionPolicy.DotNet);

        if (id.StartsWith("Microsoft.Owin.", StringComparison.OrdinalIgnoreCase)) return OwinManual;
        if (id.StartsWith("Glimpse", StringComparison.OrdinalIgnoreCase))
            return Remove("Glimpse não tem versão para ASP.NET Core. Use MiniProfiler ou Application Insights.");
        if (id.StartsWith("Microsoft.Practices.EnterpriseLibrary", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("EnterpriseLibrary.", StringComparison.OrdinalIgnoreCase))
            return Manual("Enterprise Library foi descontinuada. Substitua cada bloco: Logging → ILogger, Caching → IMemoryCache/IDistributedCache, Data → Dapper/EF, Validation → DataAnnotations/FluentValidation, Exception Handling → middleware.");
        if (id.StartsWith("Microsoft.AspNet.SignalR", StringComparison.OrdinalIgnoreCase))
            return ById["Microsoft.AspNet.SignalR"];
        if (id.StartsWith("Microsoft.AspNet.", StringComparison.OrdinalIgnoreCase))
            return Manual("Componente do ASP.NET clássico (System.Web) sem equivalente direto no ASP.NET Core; avalie o uso e substitua por recurso nativo.");
        if (id.StartsWith("Microsoft.Office.Interop.", StringComparison.OrdinalIgnoreCase))
            return Keep("Interop COM do Office só funciona no Windows com o Office instalado (aviso NU1701 esperado). Para gerar documentos no servidor prefira ClosedXML/Open XML SDK.", VersionPolicy.SameIfCompatible(), InventorySeverity.Warning);

        return null;
    }

    private static bool IsFrameworkFamily(string baseId) =>
        baseId.StartsWith("Microsoft.AspNet.", StringComparison.OrdinalIgnoreCase) ||
        baseId.StartsWith("Microsoft.Owin", StringComparison.OrdinalIgnoreCase) ||
        baseId.StartsWith("Microsoft.Data.", StringComparison.OrdinalIgnoreCase) ||
        baseId.Equals("EntityFramework", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\.(pt-br|pt|es|de|fr|it|ja|ko|ru|zh-hans|zh-hant|cs|pl|tr|pt-pt|es-es)$", RegexOptions.IgnoreCase)]
    private static partial Regex CultureSuffix();
}
