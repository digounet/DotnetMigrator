using System.Web.Http;
using LegacyShop.Web;
using Swashbuckle.Application;
using WebActivatorEx;

[assembly: PreApplicationStartMethod(typeof(SwaggerConfig), "Register")]

namespace LegacyShop.Web
{
    public class SwaggerConfig
    {
        public static void Register()
        {
            GlobalConfiguration.Configuration
                .EnableSwagger(c => c.SingleApiVersion("v1", "LegacyShop API"))
                .EnableSwaggerUi();
        }
    }
}
