using System.Collections.Generic;
using System.IO;
using System.Web;
using System.Web.Mvc;
using LegacyShop.Core.Services;
using LegacyShop.Web.Models;

namespace LegacyShop.Web.Controllers
{
    public class ProdutosController : Controller
    {
        private readonly IProdutoService _produtos;

        public ProdutosController(IProdutoService produtos)
        {
            _produtos = produtos;
        }

        [OutputCache(Duration = 60, VaryByParam = "pagina")]
        public ActionResult Index(int pagina = 1)
        {
            return View(_produtos.Listar(pagina));
        }

        [ChildActionOnly]
        public ActionResult Menu()
        {
            return PartialView(_produtos.ListarCategorias());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public ActionResult Upload(HttpPostedFileBase arquivo)
        {
            if (arquivo != null && arquivo.ContentLength > 0)
            {
                var destino = Path.Combine(Server.MapPath("~/Uploads"), Path.GetFileName(arquivo.FileName));
                arquivo.SaveAs(destino);
            }
            return RedirectToAction("Index");
        }

        [HttpPost]
        public ActionResult AdicionarAoCarrinho(int produtoId)
        {
            var carrinho = Session["Carrinho"] as List<int> ?? new List<int>();
            carrinho.Add(produtoId);
            Session["Carrinho"] = carrinho;
            return RedirectToAction("Index");
        }

        [HttpPost]
        public ActionResult Salvar([Bind(Include = "Id,Nome,Descricao,Preco")] Produto produto)
        {
            if (!ModelState.IsValid)
            {
                return View("Editar", produto);
            }
            return RedirectToAction("Index");
        }
    }
}
