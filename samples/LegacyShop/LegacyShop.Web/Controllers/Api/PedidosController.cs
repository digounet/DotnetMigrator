using System.Collections.Generic;
using System.Web.Http;
using System.Web.Http.Description;
using LegacyShop.Web.Models;

namespace LegacyShop.Web.Controllers.Api
{
    [RoutePrefix("api/pedidos")]
    [Authorize]
    public class PedidosController : ApiController
    {
        private static readonly List<Pedido> Pedidos = new List<Pedido>();

        [Route("")]
        public IEnumerable<Pedido> GetTodos()
        {
            return Pedidos;
        }

        [Route("{id:int}")]
        [ResponseType(typeof(Pedido))]
        public IHttpActionResult GetPorId(int id)
        {
            var pedido = Pedidos.Find(p => p.Id == id);
            if (pedido == null)
            {
                throw new HttpResponseException(System.Net.HttpStatusCode.NotFound);
            }
            return Ok(pedido);
        }

        [HttpPost]
        [Route("")]
        public IHttpActionResult Criar(Pedido pedido)
        {
            pedido.Usuario = User.Identity.Name;
            Pedidos.Add(pedido);
            return CreatedAtRoute("DefaultApi", new { id = pedido.Id }, pedido);
        }
    }
}
