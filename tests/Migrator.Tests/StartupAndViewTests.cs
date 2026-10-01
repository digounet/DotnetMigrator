using Migrator.Core.Analysis;
using Migrator.Core.Migration;

namespace Migrator.Tests;

public class StartupAndViewTests
{
    [Fact]
    public void Route_config_routes_become_endpoint_patterns()
    {
        const string routeConfig = """
            public class RouteConfig
            {
                public static void RegisterRoutes(RouteCollection routes)
                {
                    routes.IgnoreRoute("{resource}.axd/{*pathInfo}");
                    routes.LowercaseUrls = true;
                    routes.MapRoute(
                        name: "Produto",
                        url: "produto/{id}/{slug}",
                        defaults: new { controller = "Produtos", action = "Detalhe", slug = UrlParameter.Optional },
                        constraints: new { id = @"\d+" });
                    routes.MapRoute("Default", "{controller}/{action}/{id}", new { controller = "Home", action = "Index", id = UrlParameter.Optional });
                }
            }
            """;

        var plan = StartupAnalyzer.Analyze([("App_Start/RouteConfig.cs", routeConfig)], "Web");

        Assert.True(plan.LowercaseUrls);
        Assert.Collection(plan.Routes,
            r =>
            {
                Assert.Equal("produto/{id}/{slug?}", r.Pattern);
                Assert.Equal("new { controller = \"Produtos\", action = \"Detalhe\" }", r.Defaults);
                Assert.Equal("new { id = @\"^(\\d+)$\" }", r.Constraints);
            },
            r => Assert.Equal("{controller=Home}/{action=Index}/{id?}", r.Pattern));
    }

    [Fact]
    public void Web_api_config_area_registration_and_global_asax_are_understood()
    {
        const string webApi = """
            public static class WebApiConfig
            {
                public static void Register(HttpConfiguration config)
                {
                    config.EnableCors(new EnableCorsAttribute("https://a.com,https://b.com", "*", "GET,POST"));
                    config.Routes.MapHttpRoute("DefaultApi", "api/v1/{controller}/{id}", new { id = RouteParameter.Optional });
                    config.Formatters.Remove(config.Formatters.XmlFormatter);
                }
            }
            """;
        const string area = """
            public class AdminAreaRegistration : AreaRegistration
            {
                public override string AreaName => "Admin";
                public override void RegisterArea(AreaRegistrationContext context)
                {
                    context.MapRoute("Admin_default", "Admin/{controller}/{action}/{id}", new { action = "Index", id = UrlParameter.Optional });
                }
            }
            """;
        const string global = """
            public class MvcApplication : System.Web.HttpApplication
            {
                protected void Application_Start()
                {
                    AreaRegistration.RegisterAllAreas();
                    RouteConfig.RegisterRoutes(RouteTable.Routes);
                    AutoMapperConfig.Initialize();
                }
                protected void Application_Error() { Log(Server.GetLastError()); }
                protected void Session_Start() { }
            }
            """;

        var plan = StartupAnalyzer.Analyze([("App_Start/WebApiConfig.cs", webApi), ("Areas/Admin/AdminAreaRegistration.cs", area), ("Global.asax.cs", global)], "Web");

        Assert.Equal("api/v1/[controller]", plan.ApiRouteTemplate);
        Assert.Equal(new CorsPlan("https://a.com,https://b.com", "*", "GET,POST"), plan.Cors);
        Assert.True(plan.ApiRemovesXml);
        Assert.True(plan.HasAreas);
        var areaRoute = Assert.Single(plan.Routes);
        Assert.Equal(("Admin", "Admin/{controller}/{action=Index}/{id?}"), (areaRoute.Area, areaRoute.Pattern));
        Assert.Equal(["AutoMapperConfig.Initialize();"], plan.PendingStatements.Select(p => p.Statement));
        Assert.Contains(plan.Items, i => i.Title.Contains("Application_Error"));
        Assert.DoesNotContain(plan.Items, i => i.Title.Contains("Session_Start"));
    }

    [Fact]
    public void Unity_and_ninject_registrations_are_converted_with_lifetimes()
    {
        const string unity = """
            using MyApp.Services;
            using Microsoft.Practices.Unity;
            public static class UnityConfig
            {
                public static void RegisterComponents()
                {
                    var container = new UnityContainer();
                    container.RegisterType<IOrders, Orders>(new HierarchicalLifetimeManager());
                    container.RegisterType<IClock, Clock>(new ContainerControlledLifetimeManager());
                    container.RegisterType<IMailer, Mailer>();
                    kernel.Bind<IRepo>().To<Repo>().InRequestScope();
                }
            }
            """;

        var plan = StartupAnalyzer.Analyze([("App_Start/UnityConfig.cs", unity)], "Web");

        Assert.Equal(
        [
            "builder.Services.AddScoped<IOrders, Orders>();",
            "builder.Services.AddSingleton<IClock, Clock>();",
            "builder.Services.AddTransient<IMailer, Mailer>();",
            "builder.Services.AddScoped<IRepo, Repo>();"
        ], plan.DiRegistrations);
        Assert.Contains("MyApp.Services", plan.DiNamespaces);
        Assert.DoesNotContain("Microsoft.Practices.Unity", plan.DiNamespaces);
    }

    [Fact]
    public void Bundles_expand_to_files_honoring_version_wildcards_and_ignore_list()
    {
        const string bundleConfig = """
            bundles.Add(new ScriptBundle("~/bundles/jquery").Include("~/Scripts/jquery-{version}.js"));
            bundles.Add(new ScriptBundle("~/bundles/val").Include("~/Scripts/jquery.validate*"));
            bundles.Add(new StyleBundle("~/Content/css").Include("~/Content/bootstrap.css", "~/Content/site.css"));
            """;
        string[] files =
        [
            "Scripts/jquery-3.4.1.js", "Scripts/jquery-3.4.1.min.js", "Scripts/jquery-3.4.1.intellisense.js",
            "Scripts/jquery.validate.js", "Scripts/jquery.validate.min.js", "Scripts/jquery.validate.unobtrusive.js",
            "Content/bootstrap.css", "Content/site.css"
        ];

        var bundles = BundleConfigParser.Parse(bundleConfig, files);

        Assert.Equal(["~/Scripts/jquery-3.4.1.js"], bundles["~/bundles/jquery"]);
        Assert.Equal(["~/Scripts/jquery.validate.js", "~/Scripts/jquery.validate.unobtrusive.js"], bundles["~/bundles/val"]);

        var (view, changes) = RazorTransformer.Transform("""
                <head>
                    @Styles.Render("~/Content/css")
                    @Scripts.Render("~/bundles/unknown")
                </head>
                @Html.Partial("_Login")
                """, bundles);

        Assert.Contains("    <link rel=\"stylesheet\" href=\"~/Content/bootstrap.css\" asp-append-version=\"true\" />", view);
        Assert.Contains("@Scripts.Render(\"~/bundles/unknown\")", view);
        Assert.Contains("@await Html.PartialAsync(\"_Login\")", view);
        Assert.Contains(changes, c => c.RuleId == "VW-BUNDLE");
    }

    [Fact]
    public void View_imports_skip_system_web_namespaces()
    {
        var imports = RazorTransformer.BuildViewImports(["System.Web.Mvc", "System.Web.Optimization", "MyApp", "MyApp.Models"]);

        Assert.Contains("@using MyApp.Models", imports);
        Assert.DoesNotContain("System.Web", imports);
        Assert.Contains("@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers", imports);
    }
}
