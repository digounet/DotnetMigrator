namespace LegacyShop.Worker.Properties
{
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.VisualStudio.Editors.SettingsDesigner.SettingsSingleFileGenerator", "15.9.0.0")]
    internal sealed partial class Settings : global::System.Configuration.ApplicationSettingsBase
    {
        private static Settings defaultInstance = ((Settings)(global::System.Configuration.ApplicationSettingsBase.Synchronized(new Settings())));

        public static Settings Default
        {
            get { return defaultInstance; }
        }

        [global::System.Configuration.ApplicationScopedSettingAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("C:\\Exportacao")]
        public string PastaExportacao
        {
            get { return ((string)(this["PastaExportacao"])); }
        }

        [global::System.Configuration.ApplicationScopedSettingAttribute()]
        [global::System.Configuration.DefaultSettingValueAttribute("False")]
        public bool NotificarFalhas
        {
            get { return ((bool)(this["NotificarFalhas"])); }
        }
    }
}
