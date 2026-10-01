using System;
using System.Collections.Generic;

namespace LegacyShop.Web.Models
{
    public class Pedido
    {
        public int Id { get; set; }
        public string Usuario { get; set; }
        public DateTime Data { get; set; } = DateTime.Now;
        public List<int> ProdutoIds { get; set; } = new List<int>();
        public decimal Total { get; set; }
    }
}
