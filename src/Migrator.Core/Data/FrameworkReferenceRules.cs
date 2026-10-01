using Migrator.Core.Models;

namespace Migrator.Core.Data;

public enum FrameworkRefAction { Ignore, Package, Report }

public sealed record FrameworkRefRule(FrameworkRefAction Action, string? PackageId = null, InventorySeverity Severity = InventorySeverity.Info, string Guidance = "");

public static class FrameworkReferenceRules
{
    private static FrameworkRefRule Pkg(string id) => new(FrameworkRefAction.Package, id);
    private static FrameworkRefRule Breaking(string guidance) => new(FrameworkRefAction.Report, null, InventorySeverity.Breaking, guidance);
    private static FrameworkRefRule Warn(string guidance) => new(FrameworkRefAction.Report, null, InventorySeverity.Warning, guidance);

    private static readonly FrameworkRefRule IgnoreRule = new(FrameworkRefAction.Ignore);

    private static readonly Dictionary<string, FrameworkRefRule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System.Management"] = Pkg("System.Management"),
        ["System.DirectoryServices"] = Pkg("System.DirectoryServices"),
        ["System.DirectoryServices.AccountManagement"] = Pkg("System.DirectoryServices.AccountManagement"),
        ["System.DirectoryServices.Protocols"] = Pkg("System.DirectoryServices.Protocols"),
        ["System.Runtime.Caching"] = Pkg("System.Runtime.Caching"),
        ["System.ComponentModel.Composition"] = Pkg("System.ComponentModel.Composition"),
        ["System.ServiceProcess"] = Pkg("System.ServiceProcess.ServiceController"),
        ["System.Speech"] = Pkg("System.Speech"),
        ["System.Data.Odbc"] = Pkg("System.Data.Odbc"),
        ["System.Data.OleDb"] = Pkg("System.Data.OleDb"),
        ["Microsoft.SqlServer.Smo"] = Pkg("Microsoft.SqlServer.SqlManagementObjects"),
        ["Microsoft.SqlServer.ConnectionInfo"] = Pkg("Microsoft.SqlServer.SqlManagementObjects"),
        ["Microsoft.SqlServer.Management.Sdk.Sfc"] = Pkg("Microsoft.SqlServer.SqlManagementObjects"),

        ["System.Configuration.Install"] = Breaking("Instaladores (Installer/RunInstaller/installutil) não existem no .NET 10. Registre o serviço com sc.exe create / New-Service ou use Microsoft.Extensions.Hosting.WindowsServices."),
        ["System.Data.Entity"] = Breaking("ObjectContext/LINQ to Entities do .NET 4 (System.Data.Entity.dll) não existe no .NET 10. Migre para EF6 6.5 (pacote EntityFramework) ou EF Core."),
        ["System.Data.Linq"] = Breaking("LINQ to SQL não existe no .NET 10. Migre para EF Core ou Dapper."),
        ["System.Data.OracleClient"] = Breaking("System.Data.OracleClient não existe no .NET 10. Use Oracle.ManagedDataAccess.Core."),
        ["System.Messaging"] = Breaking("MSMQ (System.Messaging) não existe no .NET 10. Migre para Azure Service Bus / RabbitMQ ou use o pacote comunitário MSMQ.Messaging."),
        ["System.Runtime.Remoting"] = Breaking(".NET Remoting não existe no .NET 10. Use gRPC, HTTP APIs ou named pipes (StreamJsonRpc)."),
        ["System.EnterpriseServices"] = Breaking("COM+ (System.EnterpriseServices) não é suportado. Reestruture usando TransactionScope / serviços HTTP."),
        ["System.Activities"] = Breaking("Windows Workflow Foundation não existe no .NET 10. Avalie CoreWF (comunitário), Elsa Workflows ou Durable Functions."),
        ["System.Workflow.Activities"] = Breaking("Windows Workflow Foundation não existe no .NET 10."),
        ["System.Workflow.ComponentModel"] = Breaking("Windows Workflow Foundation não existe no .NET 10."),
        ["System.Workflow.Runtime"] = Breaking("Windows Workflow Foundation não existe no .NET 10."),
        ["System.IdentityModel"] = Breaking("WIF (System.IdentityModel) não existe no .NET 10. Use Microsoft.IdentityModel.* / Microsoft.Identity.Web / AddWsFederation."),
        ["System.IdentityModel.Services"] = Breaking("WIF (System.IdentityModel.Services) não existe no .NET 10. Use AddWsFederation / Microsoft.Identity.Web."),
        ["System.Web.Services"] = Breaking("ASMX e Web References (SoapHttpClientProtocol) não existem no .NET 10. Serviços: reescreva como Web API ou CoreWCF. Clientes: regenere o proxy com dotnet-svcutil (System.ServiceModel.Http)."),
        ["System.Data.Services"] = Breaking("WCF Data Services não existe no .NET 10. Use Microsoft.AspNetCore.OData."),
        ["System.Data.Services.Client"] = Breaking("WCF Data Services Client não existe no .NET 10. Use Microsoft.OData.Client."),
        ["System.ServiceModel.Web"] = Breaking("Serviços REST do WCF (WebHttpBinding) não são suportados. Reescreva como Web API ou use CoreWCF.WebHttp."),
        ["System.ServiceModel.Activation"] = Breaking("Hospedagem WCF no IIS (.svc) não existe no .NET 10. Use CoreWCF ou gRPC."),
        ["System.Deployment"] = Breaking("ClickOnce (ApplicationDeployment) não está disponível para apps .NET 10 da mesma forma. Revise a estratégia de deploy (MSIX/ClickOnce do .NET)."),
        ["Microsoft.VisualBasic.Compatibility"] = Breaking("Microsoft.VisualBasic.Compatibility não existe no .NET 10."),
        ["Oracle.DataAccess"] = Breaking("ODP.NET não gerenciado (GAC) não suporta .NET 10. Use o pacote Oracle.ManagedDataAccess.Core."),
        ["CrystalDecisions.CrystalReports.Engine"] = Breaking("Crystal Reports não suporta .NET (Core). Avalie migrar os relatórios (SSRS, FastReport, QuestPDF) ou isolar a geração num serviço .NET Framework."),
        ["CrystalDecisions.Shared"] = Breaking("Crystal Reports não suporta .NET (Core)."),
        ["CrystalDecisions.Web"] = Breaking("Crystal Reports não suporta .NET (Core)."),
        ["CrystalDecisions.ReportSource"] = Breaking("Crystal Reports não suporta .NET (Core)."),
        ["Microsoft.ReportViewer.WebForms"] = Breaking("ReportViewer WebForms não existe no .NET 10."),
        ["Microsoft.ReportViewer.Common"] = Breaking("ReportViewer não existe no .NET 10 para web; para WinForms verifique o pacote Microsoft.ReportingServices.ReportViewerControl.WinForms."),

        ["System.Drawing"] = IgnoreRule,
        ["System.Configuration"] = IgnoreRule,
        ["System.Web.Extensions"] = IgnoreRule,
        ["Microsoft.VisualStudio.QualityTools.UnitTestFramework"] = IgnoreRule,
        ["Microsoft.VisualStudio.TestPlatform.TestFramework"] = IgnoreRule,
        ["Microsoft.VisualStudio.TestPlatform.TestFramework.Extensions"] = IgnoreRule,
        ["stdole"] = IgnoreRule,
    };

    private static readonly HashSet<string> Inbox = new(StringComparer.OrdinalIgnoreCase)
    {
        "mscorlib", "netstandard", "System", "System.Core", "System.Data", "System.Data.DataSetExtensions", "System.Xml",
        "System.Xml.Linq", "System.Net", "System.Net.Http", "System.Net.Http.WebRequest", "System.Numerics",
        "System.Runtime.Serialization", "System.ComponentModel.DataAnnotations", "Microsoft.CSharp", "Microsoft.VisualBasic",
        "System.Transactions", "System.IO.Compression", "System.IO.Compression.FileSystem", "System.Security",
        "System.ServiceModel", "System.ServiceModel.Primitives", "System.Runtime.InteropServices.RuntimeInformation",
        "System.Xaml", "WindowsBase", "PresentationCore", "PresentationFramework", "System.Windows.Forms",
        "System.Printing", "ReachFramework", "UIAutomationClient", "UIAutomationTypes", "WindowsFormsIntegration",
        "System.Windows.Forms.DataVisualization", "System.Design", "System.Drawing.Design", "Accessibility",
        "System.Runtime.Serialization.Formatters.Soap", "System.Xml.Serialization", "System.Diagnostics.Tracing",
        "System.ValueTuple", "System.Reflection.Context", "System.Net.Http.Formatting"
    };

    public static FrameworkRefRule Find(string name, ProjectKind kind)
    {
        if (name.StartsWith("System.Web", StringComparison.OrdinalIgnoreCase) && !Rules.ContainsKey(name))
            return IgnoreRule;
        if (Inbox.Contains(name)) return IgnoreRule;
        if (name.Equals("System.Drawing", StringComparison.OrdinalIgnoreCase) && kind == ProjectKind.Desktop) return IgnoreRule;
        if (Rules.TryGetValue(name, out var rule)) return rule;
        if (name.StartsWith("CrystalDecisions.", StringComparison.OrdinalIgnoreCase)) return Rules["CrystalDecisions.Shared"];
        if (name.StartsWith("Microsoft.Office.Interop.", StringComparison.OrdinalIgnoreCase) || name.Equals("office", StringComparison.OrdinalIgnoreCase))
            return Warn("Interop COM do Office (PIA do GAC). Funciona só no Windows com Office instalado: referencie via <COMReference> (build com MSBuild do Visual Studio) ou pacote de PIAs. Para gerar documentos no servidor prefira ClosedXML/Open XML SDK.");
        if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase))
            return new FrameworkRefRule(FrameworkRefAction.Report, null, InventorySeverity.Info,
                "Referência de framework sem mapeamento conhecido; o build de verificação indicará se algum tipo deixou de existir.");
        return Warn("Assembly referenciado do GAC (sem HintPath). Obtenha uma versão compatível com .NET 10 (pacote NuGet do fornecedor) ou adicione a DLL com HintPath, sabendo que DLLs compiladas para .NET Framework podem falhar em tempo de execução.");
    }
}
