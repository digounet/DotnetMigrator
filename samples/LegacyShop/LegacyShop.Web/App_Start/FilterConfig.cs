using System.Web.Mvc;
using LegacyShop.Web.Filters;

namespace LegacyShop.Web
{
    public class FilterConfig
    {
        public static void RegisterGlobalFilters(GlobalFilterCollection filters)
        {
            filters.Add(new HandleErrorAttribute());
            filters.Add(new LogActionFilter());
            filters.Add(new RequireHttpsAttribute());
        }
    }
}
