using System.Diagnostics;
using System.Web.Mvc;
using log4net;

namespace LegacyShop.Web.Filters
{
    public class LogActionFilter : ActionFilterAttribute
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(LogActionFilter));

        public override void OnActionExecuting(ActionExecutingContext filterContext)
        {
            filterContext.HttpContext.Items["cronometro"] = Stopwatch.StartNew();
            base.OnActionExecuting(filterContext);
        }

        public override void OnActionExecuted(ActionExecutedContext filterContext)
        {
            var cronometro = (Stopwatch)filterContext.HttpContext.Items["cronometro"];
            Log.InfoFormat("{0} executado em {1} ms ({2})",
                filterContext.ActionDescriptor.ActionName,
                cronometro.ElapsedMilliseconds,
                filterContext.HttpContext.Request.RawUrl);
            base.OnActionExecuted(filterContext);
        }
    }
}
