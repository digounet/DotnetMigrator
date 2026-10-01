using Migrator.Core.Migration;

namespace Migrator.Tests;

public class ControllerRewriterTests
{
    private static (string Text, List<CodeChange> Changes, ControllerRewriter Rewriter) Rewrite(string source, string? area = null, string template = "api/[controller]")
    {
        var catalog = ControllerCatalog.Build([source]);
        return ControllerRewriter.Rewrite(source, catalog, template, area);
    }

    [Fact]
    public void Convention_routed_web_api_controller_gets_attribute_routing()
    {
        const string source = """
            using System.Web.Http;

            public class ProductsController : ApiController
            {
                public IEnumerable<string> Get() => null;
                public string Get(int id) => null;
                public void Post([FromBody] string value) { }
                public void Delete(int id) { }
                public void Recalcular() { }
            }
            """;

        var (text, _, _) = Rewrite(source);

        Assert.Contains("[Route(\"api/[controller]\")]", text);
        Assert.Contains("[ApiController]", text);
        Assert.Contains(": ControllerBase", text);
        Assert.Contains("[HttpGet]\n    public IEnumerable<string> Get()", text.Replace("\r", ""));
        Assert.Contains("[HttpGet(\"{id}\")]", text);
        Assert.Contains("[HttpDelete(\"{id}\")]", text);
        Assert.Contains("[HttpPost]\n    public void Recalcular()", text.Replace("\r", ""));
    }

    [Fact]
    public void Attribute_routed_controller_keeps_its_templates()
    {
        const string source = """
            [RoutePrefix("api/orders")]
            public class OrdersController : ApiController
            {
                [Route("{id:int}")]
                public string GetById(int id) => null;

                [HttpPost, Route("")]
                public void Create(Order order) { }
            }
            """;

        var (text, _, _) = Rewrite(source);

        Assert.DoesNotContain("[Route(\"api/[controller]\")]", text);
        Assert.Contains("[HttpGet]", text);
        Assert.Contains("[Route(\"{id:int}\")]", text);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"HttpPost"));
    }

    [Fact]
    public void Http_response_message_actions_return_iactionresult()
    {
        const string source = """
            public class ItemsController : ApiController
            {
                public HttpResponseMessage Post(Item item) => Request.CreateResponse(HttpStatusCode.Created, item);
                public async Task<HttpResponseMessage> Put(int id) { await Task.Yield(); return Request.CreateResponse(HttpStatusCode.OK); }
                public HttpResponseMessage Download() { return new HttpResponseMessage(HttpStatusCode.OK); }
            }
            """;

        var (text, _, rewriter) = Rewrite(source);

        Assert.Contains("public IActionResult Post(", text);
        Assert.Contains("public async Task<IActionResult> Put(", text);
        Assert.Contains("public HttpResponseMessage Download()", text);
        Assert.Contains("Download", rewriter.UnconvertedHttpResponseMessage);
    }

    [Fact]
    public void Controllers_derived_from_a_custom_api_base_are_detected()
    {
        const string source = """
            public abstract class BaseApi : ApiController { }
            public class CustomersController : BaseApi
            {
                public string Get() => HttpContext.Current.User.Identity.Name;
                public IHttpActionResult Export() => Json(new { ok = true });
            }
            """;

        var (text, _, _) = Rewrite(source);

        Assert.Contains("[ApiController]\npublic abstract class BaseApi : ControllerBase", text.Replace("\r", ""));
        Assert.Contains("[Route(\"api/[controller]\")]\npublic class CustomersController", text.Replace("\r", ""));
        Assert.Contains("=> HttpContext.User.Identity.Name", text);
        Assert.Contains("new JsonResult(new { ok = true })", text);
    }

    [Fact]
    public void Area_controllers_receive_area_attribute()
    {
        const string source = """
            public class DashboardController : Controller
            {
                public ActionResult Index() => View();
            }
            """;

        var (text, _, _) = Rewrite(source, area: "Admin");

        Assert.Contains("[Area(\"Admin\")]", text);
        Assert.DoesNotContain("HttpGet", text);
    }

    [Fact]
    public void Ambiguous_convention_routes_are_reported()
    {
        const string source = """
            public class SearchController : ApiController
            {
                public string Get() => null;
                public string GetByName(string name) => null;
            }
            """;

        var (_, _, rewriter) = Rewrite(source);

        var warning = Assert.Single(rewriter.RouteWarnings);
        Assert.Equal(["Get", "GetByName"], warning.Actions);
    }

    [Fact]
    public void Non_controller_code_is_left_untouched()
    {
        const string source = "public class Service { public string Name => HttpContext.Current.User.Identity.Name; }";

        var (text, changes, _) = Rewrite(source);

        Assert.Equal(source, text);
        Assert.Empty(changes);
    }
}
