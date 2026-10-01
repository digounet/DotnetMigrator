using System.Web;

namespace LegacyShop.Web.Handlers
{
    public class Thumbnail : IHttpHandler
    {
        public void ProcessRequest(HttpContext context)
        {
            var arquivo = context.Server.MapPath("~/Uploads/" + context.Request.QueryString["f"]);
            context.Response.ContentType = "image/png";
            context.Response.WriteFile(arquivo);
        }

        public bool IsReusable => true;
    }
}
