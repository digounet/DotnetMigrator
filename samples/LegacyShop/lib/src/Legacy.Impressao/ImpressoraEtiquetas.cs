using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using Microsoft.Win32;

namespace Legacy.Impressao
{
    /// <summary>
    /// Biblioteca interna de impressao de etiquetas ZPL (2012). Le a impressora padrao do Registro,
    /// envia o fluxo bruto via spooler do Windows (winspool.drv) e registra falhas no Event Log.
    /// </summary>
    public static class ImpressoraEtiquetas
    {
        private const string ChaveRegistro = @"SOFTWARE\LegacyShop\Impressao";
        private const string CacheLayouts = @"C:\LegacyShop\cache\layouts.bin";

        public static string ImpressoraPadrao()
        {
            using (var chave = Registry.LocalMachine.OpenSubKey(ChaveRegistro))
            {
                var nome = chave == null ? null : chave.GetValue("ImpressoraEtiquetas") as string;
                return nome ?? ConfigurationManager.AppSettings["ImpressoraEtiquetas"] ?? "ZEBRA-EXPEDICAO";
            }
        }

        public static bool Imprimir(string zpl)
        {
            var impressora = ImpressoraPadrao();
            IntPtr handle;
            if (!OpenPrinter(impressora, out handle, IntPtr.Zero))
            {
                RegistrarFalha("Nao foi possivel abrir a impressora " + impressora);
                return false;
            }
            try
            {
                var dados = Encoding.GetEncoding(1252).GetBytes(zpl);
                var buffer = Marshal.AllocCoTaskMem(dados.Length);
                Marshal.Copy(dados, 0, buffer, dados.Length);
                int escritos;
                var ok = WritePrinter(handle, buffer, dados.Length, out escritos);
                Marshal.FreeCoTaskMem(buffer);
                return ok;
            }
            finally
            {
                ClosePrinter(handle);
            }
        }

        public static LayoutEtiqueta CarregarLayout(string nome)
        {
            if (!File.Exists(CacheLayouts)) return new LayoutEtiqueta { Nome = nome };
            using (var stream = File.OpenRead(CacheLayouts))
                return (LayoutEtiqueta)new BinaryFormatter().Deserialize(stream);
        }

        private static void RegistrarFalha(string mensagem)
        {
            if (!EventLog.SourceExists("LegacyShop")) EventLog.CreateEventSource("LegacyShop", "Application");
            EventLog.WriteEntry("LegacyShop", mensagem, EventLogEntryType.Error);
        }

        [DllImport("winspool.drv", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool OpenPrinter(string nome, out IntPtr handle, IntPtr padrao);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr handle, IntPtr buffer, int tamanho, out int escritos);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr handle);
    }

    [Serializable]
    public class LayoutEtiqueta
    {
        public string Nome { get; set; }
        public int Largura { get; set; }
        public int Altura { get; set; }
    }
}
