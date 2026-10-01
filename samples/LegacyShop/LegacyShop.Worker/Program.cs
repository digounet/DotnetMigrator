using System.ServiceProcess;

namespace LegacyShop.Worker
{
    internal static class Program
    {
        private static void Main()
        {
            ServiceBase.Run(new ServiceBase[] { new SincronizacaoService() });
        }
    }
}
