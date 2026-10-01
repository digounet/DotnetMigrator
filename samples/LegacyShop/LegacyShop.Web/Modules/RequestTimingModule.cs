using System;
using System.Diagnostics;
using System.Web;

namespace LegacyShop.Web.Modules
{
    public class RequestTimingModule : IHttpModule
    {
        public void Init(HttpApplication context)
        {
            context.BeginRequest += (s, e) => HttpContext.Current.Items["inicio"] = Stopwatch.StartNew();
            context.EndRequest += (s, e) =>
            {
                var sw = (Stopwatch)HttpContext.Current.Items["inicio"];
                HttpContext.Current.Response.AddHeader("X-Tempo-Ms", sw.ElapsedMilliseconds.ToString());
            };
        }

        public void Dispose()
        {
        }
    }
}
