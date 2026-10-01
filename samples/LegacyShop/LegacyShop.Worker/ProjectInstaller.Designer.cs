namespace LegacyShop.Worker
{
    partial class ProjectInstaller
    {
        private System.ServiceProcess.ServiceProcessInstaller serviceProcessInstaller1;
        private System.ServiceProcess.ServiceInstaller serviceInstaller1;

        private void InitializeComponent()
        {
            this.serviceProcessInstaller1 = new System.ServiceProcess.ServiceProcessInstaller();
            this.serviceInstaller1 = new System.ServiceProcess.ServiceInstaller();
            this.serviceProcessInstaller1.Account = System.ServiceProcess.ServiceAccount.LocalSystem;
            this.serviceInstaller1.ServiceName = "LegacyShopSincronizacao";
            this.serviceInstaller1.DisplayName = "LegacyShop - Sincronização de estoque";
            this.serviceInstaller1.StartType = System.ServiceProcess.ServiceStartMode.Automatic;
            this.Installers.AddRange(new System.Configuration.Install.Installer[] { this.serviceProcessInstaller1, this.serviceInstaller1 });
        }
    }
}
