using Migrator.Core.Migration;

namespace Migrator.Tests;

public class CodeTransformerTests
{
    private static readonly CodeTransformOptions Web = new(Log4NetConfigExtracted: false, IsWebProject: true);
    private static readonly CodeTransformOptions Library = new(Log4NetConfigExtracted: false, IsWebProject: false);

    [Fact]
    public void Maps_mvc_usings_and_adds_namespaces_for_types_in_use()
    {
        const string source = """
            using System.Web.Mvc;
            using System.Web.Optimization;

            public class LogFilter : ActionFilterAttribute
            {
                public override void OnActionExecuting(ActionExecutingContext filterContext) { }
            }
            """;

        var (text, changes, facts) = CodeTransformer.Transform(source, Web);

        Assert.Contains("using Microsoft.AspNetCore.Mvc;", text);
        Assert.Contains("using Microsoft.AspNetCore.Mvc.Filters;", text);
        Assert.Contains("// Migrator: namespace inexistente no .NET 10 → using System.Web.Optimization;", text);
        Assert.DoesNotContain("using System.Web.Mvc;", text);
        Assert.True(facts.UsesAspNetCore);
        Assert.Contains(changes, c => c.RuleId == "CS-USING");
    }

    [Fact]
    public void Rewrites_configuration_manager_to_iconfiguration_sections()
    {
        const string source = """
            using System.Configuration;

            public class Settings
            {
                public string Url => ConfigurationManager.AppSettings["ApiBaseUrl"];
                public string Db => ConfigurationManager.ConnectionStrings["Default"].ConnectionString;
                public string Dynamic(string key) => ConfigurationManager.AppSettings[key];
            }
            """;

        var (text, _, facts) = CodeTransformer.Transform(source, Library);

        Assert.Contains("configuration[\"AppSettings:ApiBaseUrl\"]", text);
        Assert.Contains("configuration.GetConnectionString(\"Default\")", text);
        Assert.Contains("configuration[\"AppSettings:\" + key]", text);
        Assert.Contains("using Microsoft.Extensions.Configuration;", text);
        Assert.DoesNotContain("using System.Configuration;", text);
        Assert.True(facts.RewroteConfiguration);
        Assert.False(facts.UsesLegacyConfigurationApi);
    }

    [Fact]
    public void Keeps_system_configuration_when_custom_sections_remain()
    {
        const string source = """
            using System.Configuration;

            public class MySection : ConfigurationSection { }

            public static class Config
            {
                public static string Db => ConfigurationManager.ConnectionStrings["Default"].ConnectionString;
                public static MySection Section => (MySection)ConfigurationManager.GetSection("mySection");
            }
            """;

        var (text, _, facts) = CodeTransformer.Transform(source, Library);

        Assert.Contains("using System.Configuration;", text);
        Assert.Contains("configuration[\"ConnectionStrings:Default\"]", text);
        Assert.True(facts.UsesLegacyConfigurationApi);
    }

    [Fact]
    public void Converts_common_mvc5_results_attributes_and_request_members()
    {
        const string source = """
            using System.Net;
            using System.Web.Mvc;

            public class HomeController : Controller
            {
                [Authorize]
                [OutputCache(Duration = 60, VaryByParam = "id;page")]
                public ActionResult Index(int? id, HttpPostedFileBase file)
                {
                    if (id == null) return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
                    if (!Request.IsAuthenticated) return HttpNotFound();
                    var q = Request.QueryString["q"];
                    return Json(new { q }, JsonRequestBehavior.AllowGet);
                }
            }
            """;

        var (text, _, facts) = CodeTransformer.Transform(source, Web);

        Assert.Contains("new StatusCodeResult((int)HttpStatusCode.BadRequest)", text);
        Assert.Contains("return NotFound();", text);
        Assert.Contains("User.Identity.IsAuthenticated", text);
        Assert.Contains("Request.Query[\"q\"]", text);
        Assert.Contains("return Json(new { q });", text);
        Assert.Contains("IFormFile file", text);
        Assert.Contains("[OutputCache(Duration = 60, VaryByQueryKeys = new[] { \"id\", \"page\" })]", text);
        Assert.Contains("using Microsoft.AspNetCore.Authorization;", text);
        Assert.Contains("using Microsoft.AspNetCore.OutputCaching;", text);
        Assert.Contains("using Microsoft.AspNetCore.Http;", text);
        Assert.True(facts.UsesOutputCache);
    }

    [Fact]
    public void Replaces_system_data_sqlclient_with_microsoft_data_sqlclient()
    {
        const string source = """
            using System.Data.SqlClient;

            public class Repo
            {
                public object Open() => new System.Data.SqlClient.SqlConnection("x");
                public string Provider => "System.Data.SqlClient";
            }
            """;

        var (text, _, facts) = CodeTransformer.Transform(source, Library);

        Assert.Contains("using Microsoft.Data.SqlClient;", text);
        Assert.Contains("new Microsoft.Data.SqlClient.SqlConnection", text);
        Assert.Contains("\"System.Data.SqlClient\"", text);
        Assert.True(facts.UsesSqlClient);
    }

    [Fact]
    public void Points_log4net_configuration_to_extracted_file()
    {
        const string source = """
            [assembly: log4net.Config.XmlConfigurator(Watch = true)]
            public static class Boot { public static void Start() { log4net.Config.XmlConfigurator.Configure(); } }
            """;

        var (text, _, _) = CodeTransformer.Transform(source, new CodeTransformOptions(Log4NetConfigExtracted: true, IsWebProject: false));

        Assert.Contains("XmlConfigurator(ConfigFile = \"log4net.config\", Watch = true)", text);
        Assert.Contains("\"log4net.config\")))", text);
    }

    [Fact]
    public void Preserves_crlf_line_endings_and_removes_duplicate_usings()
    {
        var source = "using System.Web.Mvc;\r\nusing System.Web.Http;\r\n\r\npublic class A { }\r\n";

        var (text, _, _) = CodeTransformer.Transform(source, Web);

        Assert.Equal(1, text.Split("using Microsoft.AspNetCore.Mvc;").Length - 1);
        Assert.DoesNotContain("\n\n\n", text.Replace("\r", ""));
        Assert.Contains("\r\n", text);
    }
}
