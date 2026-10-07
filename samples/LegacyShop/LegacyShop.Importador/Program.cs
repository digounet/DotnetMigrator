using System;
using System.Configuration;

namespace LegacyShop.Importador
{
    // Executado pelo Agendador de Tarefas do Windows a cada 10 minutos.
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                var importador = new ImportadorDePedidos(
                    ConfigurationManager.AppSettings["PastaEntrada"],
                    ConfigurationManager.AppSettings["PastaProcessados"],
                    ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString);

                var arquivos = importador.ImportarArquivos();
                var emails = importador.ImportarEmails(
                    ConfigurationManager.AppSettings["ExchangeUrl"],
                    ConfigurationManager.AppSettings["CaixaPostal"],
                    ConfigurationManager.AppSettings["CaixaPostalSenha"]);

                Console.WriteLine("{0:dd/MM/yyyy HH:mm} - {1} arquivo(s) e {2} e-mail(s) importados (protocolo {3}).", DateTime.Now, arquivos, emails, LegacyShop.Comum.Protocolo.Gerar("IMP", DateTime.Now));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Falha na importação: " + ex);
                return 1;
            }
        }
    }
}
