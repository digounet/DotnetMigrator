using System;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.Linq;
using CsvHelper;
using Microsoft.Exchange.WebServices.Data;

namespace LegacyShop.Importador
{
    public class ImportadorDePedidos
    {
        private readonly string _pastaEntrada;
        private readonly string _pastaProcessados;
        private readonly string _connectionString;

        public ImportadorDePedidos(string pastaEntrada, string pastaProcessados, string connectionString)
        {
            _pastaEntrada = pastaEntrada;
            _pastaProcessados = pastaProcessados;
            _connectionString = connectionString;
        }

        // Varre a pasta de rede onde o ERP deposita planilhas CSV de pedidos.
        public int ImportarArquivos()
        {
            var total = 0;
            foreach (var arquivo in Directory.GetFiles(_pastaEntrada, "*.csv"))
            {
                using (var reader = new StreamReader(arquivo))
                using (var csv = new CsvReader(reader))
                {
                    csv.Configuration.Delimiter = ";";
                    foreach (var pedido in csv.GetRecords<PedidoCsv>())
                    {
                        Gravar(pedido.Numero, pedido.Cliente, decimal.Parse(pedido.Valor), DateTime.Parse(pedido.Data));
                        total++;
                    }
                }
                File.Move(arquivo, Path.Combine(_pastaProcessados, Path.GetFileName(arquivo)));
            }
            return total;
        }

        // Lê a caixa postal "pedidos@" e importa os anexos CSV.
        public int ImportarEmails(string url, string caixa, string senha)
        {
            var service = new ExchangeService(ExchangeVersion.Exchange2010_SP2)
            {
                Credentials = new WebCredentials(caixa, senha),
                Url = new Uri(url)
            };

            var total = 0;
            var itens = service.FindItems(WellKnownFolderName.Inbox, new ItemView(50));
            foreach (var item in itens.Items.OfType<EmailMessage>())
            {
                item.Load(new PropertySet(BasePropertySet.FirstClassProperties, ItemSchema.Attachments));
                foreach (var anexo in item.Attachments.OfType<FileAttachment>().Where(a => a.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
                {
                    var destino = Path.Combine(_pastaEntrada, anexo.Name);
                    anexo.Load(destino);
                    total++;
                }
                item.IsRead = true;
                item.Update(ConflictResolutionMode.AutoResolve);
            }
            return total;
        }

        private void Gravar(string numero, string cliente, decimal valor, DateTime data)
        {
            using (var conexao = new SqlConnection(_connectionString))
            using (var comando = new SqlCommand("INSERT INTO PedidoImportado (Numero, Cliente, Valor, Data) VALUES (@n, @c, @v, @d)", conexao))
            {
                comando.Parameters.AddWithValue("@n", numero);
                comando.Parameters.AddWithValue("@c", cliente);
                comando.Parameters.AddWithValue("@v", valor);
                comando.Parameters.AddWithValue("@d", data);
                conexao.Open();
                comando.ExecuteNonQuery();
            }
        }
    }

    public class PedidoCsv
    {
        public string Numero { get; set; }
        public string Cliente { get; set; }
        public string Valor { get; set; }
        public string Data { get; set; }
    }
}
