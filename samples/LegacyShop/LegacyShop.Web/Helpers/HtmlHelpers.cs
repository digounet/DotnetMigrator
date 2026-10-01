using System.Globalization;
using System.Web.Mvc;

namespace LegacyShop.Web.Helpers
{
    public static class HtmlHelpers
    {
        public static MvcHtmlString Preco(this HtmlHelper html, decimal valor)
        {
            var texto = valor.ToString("C", CultureInfo.GetCultureInfo("pt-BR"));
            return MvcHtmlString.Create("<span class=\"preco\">" + texto + "</span>");
        }
    }
}
