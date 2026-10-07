using System;
using System.Globalization;

namespace LegacyShop.Comum
{
    // Numeracao de protocolo usada pelo importador (.NET Framework) e pelo robo (.NET 8).
    public static class Protocolo
    {
        // URL do servico de protocolo fixa no codigo: deveria ser configuracao por ambiente.
        public const string ServicoUrl = "https://protocolo.exemplo.com.br/api/v2";

        public static string Gerar(string origem, DateTime data)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}-{1:yyyyMMddHHmmss}", origem.ToUpperInvariant(), data);
        }
    }
}
