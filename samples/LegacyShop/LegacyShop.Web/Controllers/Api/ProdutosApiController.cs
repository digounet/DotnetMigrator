using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Web.Http;
using System.Web.Http.Description;
using LegacyShop.Core.Domain;
using LegacyShop.Core.Services;

namespace LegacyShop.Web.Controllers.Api
{
    /// <summary>API pública de produtos.</summary>
    public class ProdutosApiController : ApiController
    {
        private readonly IProdutoService _produtos;

        public ProdutosApiController(IProdutoService produtos)
        {
            _produtos = produtos;
        }

        public IEnumerable<ProdutoEntidade> Get()
        {
            return _produtos.Listar(1);
        }

        public IEnumerable<ProdutoEntidade> GetPorCategoria([FromUri] string categoria)
        {
            return _produtos.Buscar(categoria);
        }

        [ResponseType(typeof(ProdutoEntidade))]
        public IHttpActionResult Get(int id)
        {
            var produto = _produtos.Obter(id);
            if (produto == null)
            {
                return NotFound();
            }
            return Ok(produto);
        }

        public HttpResponseMessage Post(ProdutoEntidade produto)
        {
            if (!ModelState.IsValid)
            {
                return Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState);
            }
            _produtos.Salvar(produto);
            return Request.CreateResponse(HttpStatusCode.Created, produto);
        }

        public IHttpActionResult Put(int id, ProdutoEntidade produto)
        {
            if (id != produto.Id)
            {
                return BadRequest();
            }
            _produtos.Salvar(produto);
            return StatusCode(HttpStatusCode.NoContent);
        }

        public IHttpActionResult Delete(int id)
        {
            _produtos.Remover(id);
            return Ok();
        }

        public IHttpActionResult Exportar()
        {
            return Json(_produtos.Listar(1));
        }
    }
}
