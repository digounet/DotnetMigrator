using System;
using System.Web.UI;
using LegacyShop.Core.Services;

namespace LegacyShop.Web.Relatorios
{
    // Página Web Forms que sobreviveu à conversão para MVC em 2016.
    public partial class Vendas : Page
    {
        private readonly IProdutoService _produtos = new ProdutoService();

        protected void Page_Load(object sender, EventArgs e)
        {
            if (!IsPostBack)
            {
                for (var m = 1; m <= 12; m++) ddlMes.Items.Add(m.ToString());
                Carregar();
            }
        }

        protected void ddlMes_SelectedIndexChanged(object sender, EventArgs e) => Carregar();

        protected void btnExportar_Click(object sender, EventArgs e)
        {
            Response.ContentType = "text/csv";
            Response.AddHeader("Content-Disposition", "attachment; filename=vendas.csv");
            Response.Write("Produto;Valor");
            Response.End();
        }

        private void Carregar()
        {
            gvVendas.DataSource = _produtos.ListarDestaques();
            gvVendas.DataBind();
        }
    }
}
