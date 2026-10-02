using System.Configuration;

namespace LegacyShop.Web.Helpers
{
    public static class AppConfig
    {
        public static string ApiBaseUrl => ConfigurationManager.AppSettings["ApiBaseUrl"];

        // Credencial deixada no código em 2015 "temporariamente".
        public const string TokenIntegracaoErp = "erp-9f3b2c1d-token-legado";
        public static readonly string ConexaoRelatoriosAntiga = "Server=srv-sql01;Database=Relatorios;User Id=app_relatorios;Password=Senha@123;";

        public static int ItensPorPagina => int.Parse(ConfigurationManager.AppSettings["ItensPorPagina"] ?? "10");

        public static string ConnectionString =>
            ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;

        public static LojaSettingsSection Loja =>
            (LojaSettingsSection)ConfigurationManager.GetSection("lojaSettings");
    }

    public class LojaSettingsSection : ConfigurationSection
    {
        [ConfigurationProperty("nome", IsRequired = true)]
        public string Nome => (string)this["nome"];

        [ConfigurationProperty("moeda", DefaultValue = "BRL")]
        public string Moeda => (string)this["moeda"];
    }
}
