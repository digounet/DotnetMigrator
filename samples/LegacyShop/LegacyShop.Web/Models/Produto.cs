using System.ComponentModel.DataAnnotations;
using System.Web.Mvc;

namespace LegacyShop.Web.Models
{
    public class Produto
    {
        public int Id { get; set; }

        [Required(ErrorMessage = "Informe o nome")]
        [StringLength(120)]
        public string Nome { get; set; }

        [AllowHtml]
        public string Descricao { get; set; }

        [Range(0.01, 999999)]
        public decimal Preco { get; set; }

        public string Categoria { get; set; }
    }
}
