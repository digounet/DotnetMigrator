using System.Web.Mvc;

namespace LegacyShop.Web.Areas.Admin.Controllers
{
    [Authorize(Roles = "Administrador")]
    public class DashboardController : Controller
    {
        public ActionResult Index()
        {
            ViewBag.Autenticado = Request.IsAuthenticated;
            return View();
        }
    }
}
