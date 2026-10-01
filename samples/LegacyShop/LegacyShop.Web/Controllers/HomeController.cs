using System.Linq;
using System.Net;
using System.Web;
using System.Web.Mvc;
using LegacyShop.Core.Services;

namespace LegacyShop.Web.Controllers
{
    public class HomeController : Controller
    {
        private readonly IProdutoService _produtos;

        public HomeController(IProdutoService produtos)
        {
            _produtos = produtos;
        }

        [AllowAnonymous]
        public ActionResult Index()
        {
            ViewBag.Usuario = HttpContext.Current.User.Identity.Name;
            var destaques = _produtos.ListarDestaques().Take(8).ToList();
            return View(destaques);
        }

        public ActionResult Sobre()
        {
            if (Request.IsAjaxRequest())
            {
                return PartialView();
            }
            ViewBag.Ip = Request.UserHostAddress;
            return View();
        }

        public JsonResult Buscar(string termo)
        {
            var itens = _produtos.Buscar(termo ?? Request.QueryString["q"]);
            return Json(itens, JsonRequestBehavior.AllowGet);
        }

        public ActionResult Detalhe(int? id)
        {
            if (id == null)
            {
                return new HttpStatusCodeResult(HttpStatusCode.BadRequest);
            }
            var produto = _produtos.Obter(id.Value);
            if (produto == null)
            {
                return HttpNotFound();
            }
            return View(produto);
        }

        [AllowAnonymous]
        public ActionResult Error()
        {
            return View();
        }
    }
}
