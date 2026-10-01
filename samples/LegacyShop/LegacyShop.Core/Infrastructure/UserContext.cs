using System.Web;

namespace LegacyShop.Core.Infrastructure
{
    public static class UserContext
    {
        public static string UsuarioAtual
        {
            get
            {
                var contexto = HttpContext.Current;
                return contexto != null && contexto.User.Identity.IsAuthenticated
                    ? contexto.User.Identity.Name
                    : "anonimo";
            }
        }

        public static string Ip => HttpContext.Current?.Request.UserHostAddress;
    }
}
