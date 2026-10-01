using System.Text.Json.Nodes;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

public sealed class ConfigMigratorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("migrator-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private ProjectInfo Project(ProjectKind kind, string config, params (string Name, string Content)[] transforms)
    {
        var path = Path.Combine(_dir, kind == ProjectKind.Web ? "Web.config" : "App.config");
        File.WriteAllText(path, config);
        var project = new ProjectInfo { ProjectPath = Path.Combine(_dir, "App.csproj"), Name = "App", Kind = kind, ConfigFilePath = path };
        foreach (var (name, content) in transforms)
        {
            var transformPath = Path.Combine(_dir, name);
            File.WriteAllText(transformPath, content);
            project.ConfigTransformFiles.Add(transformPath);
        }
        return project;
    }

    [Fact]
    public void Web_config_becomes_appsettings_with_hints_and_environment_overrides()
    {
        const string config = """
            <configuration>
              <appSettings>
                <add key="webpages:Version" value="3.0.0.0" />
                <add key="ApiUrl" value="http://localhost/api" />
              </appSettings>
              <connectionStrings>
                <add name="Default" connectionString="Data Source=.;Initial Catalog=Shop;Integrated Security=True" providerName="System.Data.SqlClient" />
              </connectionStrings>
              <system.web>
                <authentication mode="Forms"><forms loginUrl="~/Account/Login" timeout="45" /></authentication>
                <authorization><deny users="?" /></authorization>
                <sessionState mode="InProc" timeout="25" />
                <globalization culture="pt-BR" uiCulture="pt-BR" />
                <httpRuntime maxRequestLength="10240" />
              </system.web>
              <system.webServer>
                <modules><add name="Custom" type="App.Modules.CustomModule, App" /></modules>
                <rewrite><rules><rule name="https" /></rules></rewrite>
              </system.webServer>
              <log4net><root><level value="INFO" /></root></log4net>
            </configuration>
            """;
        const string release = """
            <configuration xmlns:xdt="http://schemas.microsoft.com/XML-Document-Transform">
              <connectionStrings>
                <add name="Default" connectionString="Data Source=prod;Initial Catalog=Shop;Integrated Security=True" xdt:Transform="SetAttributes" xdt:Locator="Match(name)" />
              </connectionStrings>
            </configuration>
            """;

        var result = ConfigMigrator.Migrate(Project(ProjectKind.Web, config, ("Web.Release.config", release)), preserveSqlEncryptionBehavior: true);

        var json = JsonNode.Parse(result.AppSettingsJson!)!;
        Assert.Equal("http://localhost/api", (string?)json["AppSettings"]!["ApiUrl"]);
        Assert.Null(json["AppSettings"]!["webpages:Version"]);
        Assert.EndsWith(";Encrypt=False", (string?)json["ConnectionStrings"]!["Default"]);

        var production = JsonNode.Parse(result.EnvironmentJson["Production"])!;
        Assert.Equal("Data Source=prod;Initial Catalog=Shop;Integrated Security=True;Encrypt=False", (string?)production["ConnectionStrings"]!["Default"]);
        Assert.True(result.EnvironmentJson.ContainsKey("Development"));

        Assert.Equal(new FormsAuthHint("/Account/Login", 45, null, null, false), result.Hints.FormsAuth);
        Assert.True(result.Hints.GlobalDenyAnonymous);
        Assert.Equal(new SessionHint("InProc", 25), result.Hints.Session);
        Assert.Equal("pt-BR", result.Hints.Culture);
        Assert.Equal(10240L * 1024, result.Hints.MaxRequestBodyBytes);

        Assert.True(result.Log4NetExtracted);
        Assert.Contains("<log4net>", result.ExtraFiles["log4net.config"]);
        Assert.Contains("<rewrite>", result.ExtraFiles["web.config"]);
        Assert.DoesNotContain("<modules>", result.ExtraFiles["web.config"]);
        Assert.Contains(result.Items, i => i.RuleId == "CFG-MODULE" && i.Severity == InventorySeverity.Breaking);
    }

    [Fact]
    public void Custom_sections_wcf_clients_and_settings_are_converted()
    {
        const string config = """
            <configuration>
              <configSections>
                <section name="shop" type="App.ShopSection, App" />
                <sectionGroup name="applicationSettings">
                  <section name="App.Properties.Settings" type="System.Configuration.ClientSettingsSection, System" />
                </sectionGroup>
              </configSections>
              <shop name="Loja" currency="BRL">
                <shipping freeAbove="199.90" />
                <categories><add key="books" value="Livros" /></categories>
              </shop>
              <applicationSettings>
                <App.Properties.Settings>
                  <setting name="ExportFolder" serializeAs="String"><value>C:\Export</value></setting>
                </App.Properties.Settings>
              </applicationSettings>
              <system.serviceModel>
                <client>
                  <endpoint name="Stock" address="http://erp/stock.svc" binding="basicHttpBinding" contract="Erp.IStock" />
                </client>
              </system.serviceModel>
            </configuration>
            """;

        var result = ConfigMigrator.Migrate(Project(ProjectKind.Console, config), preserveSqlEncryptionBehavior: false);

        var json = JsonNode.Parse(result.AppSettingsJson!)!;
        Assert.Equal("Loja", (string?)json["shop"]!["name"]);
        Assert.Equal("199.90", (string?)json["shop"]!["shipping"]!["freeAbove"]);
        Assert.Equal("Livros", (string?)json["shop"]!["categories"]!["books"]);
        Assert.Equal(@"C:\Export", (string?)json["ApplicationSettings"]!["App.Properties.Settings"]!["ExportFolder"]);
        Assert.Equal("http://erp/stock.svc", (string?)json["WcfClient"]!["Endpoints"]!["Stock"]!["Address"]);
        Assert.Null(json["AllowedHosts"]);
        Assert.True(result.NeedsSystemConfiguration);
    }
}
