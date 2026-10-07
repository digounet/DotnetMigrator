using Migrator.Core.Analysis;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

/// <summary>appsettings*.json of projects already on .NET: databases, secrets, parameters per environment, UNC paths; and {CONST} holes in interpolated SQL.</summary>
public sealed class AppSettingsAnalyzerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "migrator-appsettings-" + Guid.NewGuid().ToString("N"));

    public AppSettingsAnalyzerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ProjectInfo Project()
    {
        File.WriteAllText(Path.Combine(_dir, "Robo.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(_dir, "appsettings.json"), """
            {
              "SSI": { "DiretorioZip": "\\\\fswcorp\\CEIC\\Zip" },
              "ConnectionStrings": { "DBSH281": "Data Source=SQNPRC009;Initial Catalog=Protocolos;User ID=robo;Password=R0b0!" },
              "TokenRobo": "15GvAsvWpKIoDujk",
              "Caixa_Email": "protocolo@exemplo.com.br",
              "Account": "26697512881",
              "AccountPw": "25234312",
              "AccountPwExchange": "25234312",
              "UrlExchangeWebService": "https://mail.exemplo.com.br/EWS/Exchange.asmx",
              "UrlCentralApi": "http://localhost:5080/api"
            }
            """);
        File.WriteAllText(Path.Combine(_dir, "appsettings.Production.json"), """{ "UrlCentralApi": "https://central.exemplo.com.br/api", "Nova:Url": "https://nova.exemplo.com.br" }""");
        File.WriteAllText(Path.Combine(_dir, "appsettings.Secrets.json"), """{ "TokenRobo": "nao-deve-ser-lido" }""");
        return new ProjectInfo { ProjectPath = Path.Combine(_dir, "Robo.csproj"), Name = "Robo", Kind = ProjectKind.Console, IsSdkStyle = true, IsAlreadyModern = true, TargetFramework = "net8.0" };
    }

    [Fact]
    public void Reads_databases_secrets_settings_and_paths_from_appsettings()
    {
        var project = Project();
        var files = AppSettingsAnalyzer.Load(project)!;
        Assert.Equal(["Production"], files.EnvironmentFiles.Keys);                        // *.Secrets.json is never an environment

        var profile = new ApplicationProfile { Project = "Robo", Kind = ProjectKind.Console };
        AppSettingsAnalyzer.Profile(profile, project, files);
        var db = Assert.Single(profile.Databases);
        Assert.Equal(("SQL Server", "SQNPRC009", "Protocolos", "DBSH281"), (db.Provider, db.Server, db.Database, db.Name));
        Assert.True(profile.Has(Signal.SqlServer));
        Assert.True(profile.Has(Signal.UncPaths));
        Assert.True(profile.Has(Signal.SecretsInConfig));
        Assert.Contains("https://mail.exemplo.com.br", profile.ExternalEndpoints);

        var settings = AppSettingsAnalyzer.Settings(project, files);
        var central = Assert.Single(settings, s => s.Key == "UrlCentralApi");
        Assert.Equal("https://central.exemplo.com.br/api", central.ValueFor("Production"));
        Assert.Equal("http://localhost:5080/api", central.ValueFor("Development"));
        Assert.Contains(settings, s => s.Key == "Caixa_Email" && s.Kind == SettingKind.Email);
        Assert.Contains(settings, s => s.Key == "Nova:Url" && s.Location == "appsettings.Production.json");
        Assert.DoesNotContain(settings, s => s.Key.StartsWith("ConnectionStrings"));

        var secrets = SecretsExtractor.Extract(files.Config, "LegacyShop", "Robo");
        var paths = secrets.Secrets.Select(s => s.ConfigPath).ToList();
        Assert.Equal(["ConnectionStrings:DBSH281", "TokenRobo", "AccountPw", "AccountPwExchange"], paths);
        Assert.Contains("<secret: legacyshop/robo/TokenRobo>", files.Config.AppSettingsJson);
        Assert.Contains("\"Account\": \"26697512881\"", files.Config.AppSettingsJson);   // an account number is not a credential
        Assert.Equal((1, 3, 4, 1), AppSettingsAnalyzer.Summary(files));   // Nova:Url only exists in the Production file
    }

    [Fact]
    public void Interpolated_sql_resolves_constants_declared_in_the_project()
    {
        var project = new ProjectInfo { ProjectPath = Path.Combine(_dir, "Robo.csproj"), Name = "Robo", Kind = ProjectKind.Console };
        var scan = DataAccessAnalyzer.Scan(project,
        [
            ("Tabelas.cs", "public static class Tabelas { public const string Protocolo = \"dbo.Protocolo\"; }"),
            ("Repo.cs", """
                class Repo { private const string TABLE_NAME = "dbo.Protocolo";
                  void A() { var sql = @$"INSERT INTO {TABLE_NAME} (Protocolo, Arquivo) OUTPUT Inserted.IdProtocolo VALUES (@p, @a)"; conn.Execute(sql); }
                  void B() { conn.Query($"SELECT Protocolo FROM {Tabelas.Protocolo} WHERE IdStatus = {status}"); }
                  void C() { conn.Query($"SELECT Nome FROM {tabelaDinamica}"); } }
                """)
        ]);
        var protocolo = Assert.Single(scan.Tables, t => t.Name == "Protocolo");
        Assert.Equal("dbo", protocolo.Schema);
        Assert.Contains("INSERT", protocolo.Operations);
        Assert.Contains("Arquivo", protocolo.Columns);
        Assert.DoesNotContain(scan.Tables, t => t.Name.Contains("@p") || t.Name.Contains("tabelaDinamica"));
    }

    [Fact]
    public void Settings_helper_and_package_insertion()
    {
        var helper = ProjectMigrator.SettingsHelper(new ProjectInfo { ProjectPath = "/x/Robo.csproj", Name = "LegacyShop.Robo", RootNamespace = "LegacyShop.Robo" });
        Assert.Contains("namespace LegacyShop.Robo\n{", helper);
        Assert.Contains("internal static class MigratorSettings", helper);
        Assert.DoesNotContain("string?", helper);                                        // compiles on LangVersion 7.3 too
        var csproj = ProjectMigrator.EnsureConfigurationPackages("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>netstandard2.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        Assert.Contains("Microsoft.Extensions.Configuration.EnvironmentVariables\" Version=\"8.0.1\"", csproj);
        Assert.Contains("Microsoft.Extensions.Configuration.Json\" Version=\"10.0.0\"", ProjectMigrator.EnsureConfigurationPackages("<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>"));
        var hosting = "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"Microsoft.Extensions.Hosting\" Version=\"8.0.0\" /></ItemGroup></Project>";
        Assert.Equal(hosting, ProjectMigrator.EnsureConfigurationPackages(hosting));
    }
}
