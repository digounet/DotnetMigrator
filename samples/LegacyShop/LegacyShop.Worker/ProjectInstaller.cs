using System.ComponentModel;
using System.Configuration.Install;

namespace LegacyShop.Worker
{
    [RunInstaller(true)]
    public partial class ProjectInstaller : Installer
    {
        public ProjectInstaller()
        {
            InitializeComponent();
        }
    }
}
