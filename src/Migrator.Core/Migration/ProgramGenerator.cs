using System.Text;
using System.Text.Json.Nodes;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

public sealed record WebProgramInput(
    ProjectInfo Project,
    StartupPlan Startup,
    ProgramHints Hints,
    bool HasApiControllers,
    bool HasMvcControllers,
    bool UsesHttpContextAccessor,
    bool KeepNewtonsoft,
    bool HasSwagger,
    bool UsesOutputCache,
    bool Log4NetConfigFile,
    bool CloudReady = true);

public static class ProgramGenerator
{
    public static string GenerateWeb(WebProgramInput input)
    {
        var (project, startup, hints) = (input.Project, input.Startup, input.Hints);
        var usings = new SortedSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.AspNetCore.Builder", "Microsoft.AspNetCore.Hosting", "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting"
        };
        var sb = new StringBuilder();
        var services = new StringBuilder();
        var pipeline = new StringBuilder();

        var views = input.HasMvcControllers || project.HasMvc || !input.HasApiControllers;
        var mvc = new StringBuilder(views ? "builder.Services.AddControllersWithViews(" : "builder.Services.AddControllers(");
        var filters = new List<string>();
        if (startup.RequireHttps) filters.Add("    options.Filters.Add(new RequireHttpsAttribute());");
        filters.AddRange(startup.CustomGlobalFilters.Select(f => $"    // TODO Migrator: portar o filtro para ASP.NET Core e descomentar: options.Filters.Add({f});"));
        if (filters.Count > 0)
        {
            usings.Add("Microsoft.AspNetCore.Mvc");
            mvc.Append("options =>\n{\n").Append(string.Join("\n", filters)).Append("\n})");
        }
        else mvc.Append(')');

        if (input.KeepNewtonsoft)
        {
            usings.Add("Newtonsoft.Json.Serialization");
            mvc.Append(startup.ApiCamelCase
                ? "\n    .AddNewtonsoftJson(o => o.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver())"
                : "\n    .AddNewtonsoftJson(o => o.SerializerSettings.ContractResolver = new DefaultContractResolver())");
        }
        else if (views)
            mvc.Append("\n    .AddJsonOptions(o => o.JsonSerializerOptions.PropertyNamingPolicy = null)");
        if (input.HasApiControllers && startup.HasWebApiConfig && !startup.ApiRemovesXml)
            mvc.Append("\n    .AddXmlDataContractSerializerFormatters()");
        services.AppendLine(mvc.Append(';').ToString());

        if (input.UsesHttpContextAccessor) services.AppendLine("builder.Services.AddHttpContextAccessor();");
        if (input.CloudReady)
        {
            usings.Add("Microsoft.AspNetCore.HttpOverrides");
            services.AppendLine("builder.Services.AddHealthChecks(); // GET /health para o target group do ALB / health check do ECS");
            services.AppendLine("builder.Services.Configure<ForwardedHeadersOptions>(o =>");
            services.AppendLine("{");
            services.AppendLine("    // Atrás do Application Load Balancer: IP do cliente e esquema (https) vêm em X-Forwarded-*");
            services.AppendLine("    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;");
            services.AppendLine("    o.KnownNetworks.Clear();");
            services.AppendLine("    o.KnownProxies.Clear();");
            services.AppendLine("});");
        }
        if (hints.Culture != null)
        {
            usings.Add("System.Globalization");
            services.AppendLine($"CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(\"{hints.Culture}\"); // threads fora de requisição (containers sem LANG usam cultura invariante)");
        }
        if (input.UsesOutputCache) services.AppendLine("builder.Services.AddOutputCache();");
        if (startup.LowercaseUrls) services.AppendLine("builder.Services.AddRouting(o => o.LowercaseUrls = true);");
        if (input.HasSwagger)
        {
            services.AppendLine("builder.Services.AddEndpointsApiExplorer();");
            services.AppendLine("builder.Services.AddSwaggerGen();");
        }

        var hasAuthentication = false;
        if (hints.FormsAuth is { } forms)
        {
            usings.Add("Microsoft.AspNetCore.Authentication.Cookies");
            usings.Add("System");
            services.AppendLine("builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)");
            services.AppendLine("    .AddCookie(options =>");
            services.AppendLine("    {");
            if (forms.LoginPath != null) services.AppendLine($"        options.LoginPath = \"{forms.LoginPath}\";");
            if (forms.TimeoutMinutes != null) services.AppendLine($"        options.ExpireTimeSpan = TimeSpan.FromMinutes({forms.TimeoutMinutes});");
            if (forms.SlidingExpiration != null) services.AppendLine($"        options.SlidingExpiration = {forms.SlidingExpiration.Value.ToString().ToLowerInvariant()};");
            if (forms.CookieName != null) services.AppendLine($"        options.Cookie.Name = \"{forms.CookieName}\";");
            if (forms.RequireSsl) services.AppendLine("        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always;");
            services.AppendLine("    });");
            hasAuthentication = true;
        }
        else if (hints.WindowsAuth)
        {
            usings.Add("Microsoft.AspNetCore.Authentication.Negotiate");
            services.AppendLine("builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();");
            hasAuthentication = true;
        }

        if (hints.GlobalDenyAnonymous || startup.GlobalAuthorize)
        {
            usings.Add("Microsoft.AspNetCore.Authorization");
            services.AppendLine("builder.Services.AddAuthorization(options =>");
            services.AppendLine("    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());");
        }

        if (hints.Session is { } session)
        {
            usings.Add("System");
            services.AppendLine("builder.Services.AddDistributedMemoryCache();");
            services.AppendLine(session.TimeoutMinutes is { } minutes
                ? $"builder.Services.AddSession(o => o.IdleTimeout = TimeSpan.FromMinutes({minutes}));"
                : "builder.Services.AddSession();");
            if (!session.Mode.Equals("InProc", StringComparison.OrdinalIgnoreCase))
                services.AppendLine($"// TODO Migrator: o sessionState usava mode={session.Mode}; troque AddDistributedMemoryCache por AddStackExchangeRedisCache/AddDistributedSqlServerCache.");
        }

        if (startup.Cors is { } cors)
        {
            if (cors.Origins.Length > 0)
            {
                services.AppendLine("builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy");
                services.AppendLine(cors.Origins == "*" ? "    .AllowAnyOrigin()" : $"    .WithOrigins({string.Join(", ", cors.Origins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(o => $"\"{o}\""))})");
                services.AppendLine(cors.Headers == "*" ? "    .AllowAnyHeader()" : $"    .WithHeaders({string.Join(", ", cors.Headers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => $"\"{h}\""))})");
                services.AppendLine(cors.Methods == "*" ? "    .AllowAnyMethod()));" : $"    .WithMethods({string.Join(", ", cors.Methods.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(m => $"\"{m}\""))})));");
            }
            else
            {
                services.AppendLine("builder.Services.AddCors();");
                services.AppendLine("// TODO Migrator: o Web API habilitava CORS por atributo; defina policies com AddCors(o => o.AddPolicy(\"Nome\", ...)) e use [EnableCors(\"Nome\")].");
            }
        }

        if (hints.MaxRequestBodyBytes is { } maxBytes)
        {
            usings.Add("Microsoft.AspNetCore.Server.Kestrel.Core");
            usings.Add("Microsoft.AspNetCore.Http.Features");
            services.AppendLine($"builder.Services.Configure<KestrelServerOptions>(o => o.Limits.MaxRequestBodySize = {maxBytes});");
            services.AppendLine($"builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = {maxBytes});");
            services.AppendLine($"builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = {maxBytes});");
        }

        if (hints.CookiesRequireSsl || hints.CookiesHttpOnly)
        {
            usings.Add("Microsoft.AspNetCore.CookiePolicy");
            usings.Add("Microsoft.AspNetCore.Http");
            services.AppendLine("builder.Services.Configure<CookiePolicyOptions>(o =>");
            services.AppendLine("{");
            if (hints.CookiesRequireSsl) services.AppendLine("    o.Secure = CookieSecurePolicy.Always;");
            if (hints.CookiesHttpOnly) services.AppendLine("    o.HttpOnly = HttpOnlyPolicy.Always;");
            services.AppendLine("});");
        }

        if (startup.DiRegistrations.Count > 0)
        {
            foreach (var ns in startup.DiNamespaces) usings.Add(ns);
            services.AppendLine();
            services.AppendLine("// Registros convertidos de Unity/Ninject (revise os tempos de vida)");
            foreach (var registration in startup.DiRegistrations.Distinct()) services.AppendLine(registration);
        }

        var pending = startup.PendingStatements.Select(p => p.Statement).ToList();
        if (input.Log4NetConfigFile && pending.RemoveAll(s => s.Contains("XmlConfigurator.Configure(", StringComparison.Ordinal)) > 0)
        {
            services.AppendLine();
            services.AppendLine("log4net.Config.XmlConfigurator.Configure(");
            services.AppendLine("    log4net.LogManager.GetRepository(System.Reflection.Assembly.GetEntryAssembly()),");
            services.AppendLine("    new System.IO.FileInfo(System.IO.Path.Combine(builder.Environment.ContentRootPath, \"log4net.config\")));");
        }
        if (pending.Count > 0)
        {
            services.AppendLine();
            services.AppendLine("// TODO Migrator: instruções do Global.asax.cs (Application_Start) a portar:");
            foreach (var statement in pending) services.AppendLine($"//   {statement}");
        }

        if (input.CloudReady) pipeline.AppendLine("app.UseForwardedHeaders();");
        pipeline.AppendLine("if (!app.Environment.IsDevelopment())");
        pipeline.AppendLine("{");
        pipeline.AppendLine($"    app.UseExceptionHandler(\"{hints.ErrorRedirect ?? (views ? "/Home/Error" : "/error")}\");");
        pipeline.AppendLine("    app.UseHsts();");
        pipeline.AppendLine("}");
        if (input.HasSwagger)
        {
            pipeline.AppendLine("else");
            pipeline.AppendLine("{");
            pipeline.AppendLine("    app.UseSwagger();");
            pipeline.AppendLine("    app.UseSwaggerUI();");
            pipeline.AppendLine("}");
        }
        pipeline.AppendLine();
        if (hints.Culture != null) pipeline.AppendLine($"app.UseRequestLocalization(\"{hints.Culture}\");");
        pipeline.AppendLine("app.UseHttpsRedirection();");
        pipeline.AppendLine("app.UseStaticFiles();");
        if (hints.CookiesRequireSsl || hints.CookiesHttpOnly) pipeline.AppendLine("app.UseCookiePolicy();");
        pipeline.AppendLine("app.UseRouting();");
        if (startup.Cors != null) pipeline.AppendLine("app.UseCors();");
        if (hasAuthentication) pipeline.AppendLine("app.UseAuthentication();");
        pipeline.AppendLine("app.UseAuthorization();");
        if (hints.Session != null) pipeline.AppendLine("app.UseSession();");
        if (input.UsesOutputCache) pipeline.AppendLine("app.UseOutputCache();");
        pipeline.AppendLine();

        if (views)
        {
            if (startup.HasAreas && startup.Routes.All(r => r.Area == null))
                pipeline.AppendLine("app.MapControllerRoute(name: \"areas\", pattern: \"{area:exists}/{controller=Home}/{action=Index}/{id?}\");");
            foreach (var route in startup.Routes.Where(r => r.Area != null))
                pipeline.AppendLine($"app.MapAreaControllerRoute(name: \"{route.Name}\", areaName: \"{route.Area}\", pattern: \"{route.Pattern}\"{Extra(route)});");
            var mainRoutes = startup.Routes.Where(r => r.Area == null).ToList();
            if (mainRoutes.Count == 0)
                pipeline.AppendLine("app.MapControllerRoute(name: \"default\", pattern: \"{controller=Home}/{action=Index}/{id?}\");");
            foreach (var route in mainRoutes)
                pipeline.AppendLine($"app.MapControllerRoute(name: \"{route.Name}\", pattern: \"{route.Pattern}\"{Extra(route)});");
        }
        pipeline.AppendLine("app.MapControllers();");
        if (input.CloudReady) pipeline.AppendLine("app.MapHealthChecks(\"/health\").AllowAnonymous();");

        sb.Append(string.Concat(usings.Select(u => $"using {u};\n")));
        sb.Append('\n');
        sb.Append($"// Gerado pelo Migrator a partir de {project.Name} ({project.TargetFramework}). Revise os itens marcados com TODO.\n");
        sb.Append("var builder = WebApplication.CreateBuilder(args);\n\n");
        sb.Append(services);
        sb.Append("\nvar app = builder.Build();\n\n");
        sb.Append(pipeline);
        sb.Append("\napp.Run();\n");
        return sb.ToString().Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    public static string GenerateLaunchSettings(ProjectInfo project)
    {
        var httpUrl = "http://localhost:5000";
        if (Uri.TryCreate(project.IisUrl, UriKind.Absolute, out var iis))
            httpUrl = $"{(iis.Scheme == "https" ? "http" : iis.Scheme)}://localhost:{iis.Port}";
        var profiles = new JsonObject
        {
            ["http"] = new JsonObject
            {
                ["commandName"] = "Project",
                ["launchBrowser"] = true,
                ["applicationUrl"] = int.TryParse(project.IisSslPort, out var ssl) && ssl > 0 ? $"https://localhost:{ssl};{httpUrl}" : httpUrl,
                ["environmentVariables"] = new JsonObject { ["ASPNETCORE_ENVIRONMENT"] = "Development" }
            }
        };
        return new JsonObject { ["profiles"] = profiles }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string Extra(MvcRoute route) =>
        (route.Defaults != null ? $", defaults: {route.Defaults}" : "") +
        (route.Constraints != null ? $", constraints: {route.Constraints}" : "");
}
