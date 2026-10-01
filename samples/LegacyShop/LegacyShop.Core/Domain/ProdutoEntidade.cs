using System;
using Newtonsoft.Json;

namespace LegacyShop.Core.Domain
{
    [Serializable]
    public class ProdutoEntidade
    {
        public int Id { get; set; }

        [JsonProperty("nome_produto")]
        public string Nome { get; set; }

        public string Descricao { get; set; }
        public decimal Preco { get; set; }
        public string Categoria { get; set; }

        [JsonIgnore]
        public bool Destaque { get; set; }
    }
}
