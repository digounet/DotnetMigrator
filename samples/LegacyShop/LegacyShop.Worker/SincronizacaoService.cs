using System;
using System.Configuration;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using LegacyShop.Core.Services;
using LegacyShop.Worker.Properties;

namespace LegacyShop.Worker
{
    // Serviço de sincronização de estoque com o ERP (versão 2014).
    public partial class SincronizacaoService : ServiceBase
    {
        private Thread _trabalhador;
        private readonly int _intervalo = int.Parse(ConfigurationManager.AppSettings["IntervaloMinutos"]);

        public SincronizacaoService()
        {
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            EventLog.WriteEntry("LegacyShop.Worker", "Serviço iniciado. Exportação em " + Settings.Default.PastaExportacao);
            _trabalhador = new Thread(Executar) { IsBackground = true };
            _trabalhador.Start();
        }

        protected override void OnStop()
        {
            _trabalhador.Abort();
        }

        private void Executar()
        {
            var cache = new CacheService();
            while (true)
            {
                var conexao = ConfigurationManager.ConnectionStrings["DefaultConnection"].ConnectionString;
                Debug.WriteLine("Sincronizando com " + ConfigurationManager.AppSettings["ErpEndpoint"] + " usando " + conexao);
                Thread.Sleep(TimeSpan.FromMinutes(_intervalo));
            }
        }
    }
}