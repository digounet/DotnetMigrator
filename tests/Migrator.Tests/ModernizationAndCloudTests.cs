using System.Xml.Linq;
using Migrator.Core.Analysis;
using Migrator.Core.Cloud;
using Migrator.Core.Data;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Tests;

public class ModernizationAndCloudTests
{
    private static ProjectInfo Project(string name, ProjectKind kind, params string[] packages)
    {
        var info = new ProjectInfo { ProjectPath = Path.Combine(Path.GetTempPath(), "migrator-tests", name, name + ".csproj"), Name = name, Kind = kind, OutputType = kind is ProjectKind.Web or ProjectKind.ClassLibrary ? "Library" : "Exe" };
        foreach (var p in packages) info.Packages.Add(new PackageInfo(p, "1.0.0"));
        return info;
    }

    private static ApplicationProfile Profile(ProjectInfo project, string code, string? config = null) =>
        ApplicationProfiler.Analyze(project, [("Program.cs", code)], config == null ? null : XElement.Parse(config));

    // ------------------------------------------------------------------ package rules

    [Theory]
    [InlineData("AutoMapper", ModernizationKind.License)]
    [InlineData("MediatR", ModernizationKind.License)]
    [InlineData("FluentAssertions", ModernizationKind.License)]
    [InlineData("EPPlus", ModernizationKind.License)]
    [InlineData("MassTransit", ModernizationKind.License)]
    [InlineData("iTextSharp", ModernizationKind.License)]
    [InlineData("Telerik.UI.for.AspNet.Mvc5", ModernizationKind.License)]
    [InlineData("Topshelf", ModernizationKind.Deprecated)]
    [InlineData("CrystalDecisions.CrystalReports.Engine", ModernizationKind.Deprecated)]
    [InlineData("Rotativa", ModernizationKind.Deprecated)]
    [InlineData("WindowsAzure.Storage", ModernizationKind.Cloud)]
    [InlineData("Azure.Messaging.ServiceBus", ModernizationKind.Cloud)]
    [InlineData("RabbitMQ.Client", ModernizationKind.Cloud)]
    [InlineData("Quartz", ModernizationKind.Cloud)]
    [InlineData("Newtonsoft.Json", ModernizationKind.Modernize)]
    [InlineData("EntityFramework", ModernizationKind.Modernize)]
    public void Package_rules_classify_licensing_deprecation_and_cloud_moves(string id, ModernizationKind expected) =>
        Assert.Equal(expected, ModernizationRules.Find(id)!.Kind);

    [Fact]
    public void Unknown_packages_have_no_modernization_rule() => Assert.Null(ModernizationRules.Find("Empresa.Integracao.Erp"));

    [Fact]
    public void License_rules_name_a_free_alternative_and_an_aws_equivalent_when_relevant()
    {
        Assert.Contains("Mapperly", ModernizationRules.Find("AutoMapper")!.Proposal);
        Assert.Contains("Mediator", ModernizationRules.Find("MediatR")!.Proposal);
        Assert.Contains("AWS.Messaging", ModernizationRules.Find("MassTransit")!.Proposal);
        Assert.Equal("Amazon SQS / SNS", ModernizationRules.Find("MassTransit")!.AwsService);
    }

    // ------------------------------------------------------------------ C# code rules

    [Fact]
    public void Code_rules_flag_behaviour_changes_that_still_compile()
    {
        const string code = """
            using System;
            using System.Text;
            using System.Transactions;
            public class Legado
            {
                public void Executar()
                {
                    var enc = Encoding.GetEncoding(1252);
                    var txt = Encoding.Default.GetString(new byte[0]);
                    using (var scope = new TransactionScope()) { }
                    System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("pt-BR");
                    var valor = decimal.Parse("1.234,50");
                    var quando = DateTime.Now;
                    var caminho = System.IO.Path.Combine("C:\\Relatorios", "saida\\arquivo.txt");
                    var hash = "chave".GetHashCode();
                    var client = new System.Net.Http.HttpClient();
                    var r = client.GetStringAsync("http://x").Result;
                    var md5 = System.Security.Cryptography.MD5.Create();
                }
            }
            """;
        var project = Project("Lib", ProjectKind.ClassLibrary);
        var items = ModernizationAdvisor.Analyze(project, Profile(project, code), [("Legado.cs", code)], CloudTarget.Aws);
        var ids = items.Select(i => i.RuleId).ToHashSet();

        foreach (var expected in new[] { "MOD-CS-CODEPAGES", "MOD-CS-ENCODING-DEFAULT", "MOD-CS-DTC", "MOD-CS-CULTURE-THREAD", "MOD-CS-PARSE-CULTURE",
                     "MOD-CS-DATETIME-NOW", "MOD-CS-PATH-BACKSLASH", "MOD-CS-HASHCODE-PERSISTED", "MOD-CS-HTTPCLIENT-NEW", "MOD-CS-SYNC-OVER-ASYNC", "MOD-CS-WEAK-CRYPTO" })
            Assert.Contains(expected, ids);

        var codepages = items.Single(i => i.RuleId == "MOD-CS-CODEPAGES");
        Assert.Equal(Impact.High, codepages.Impact);
        Assert.Contains("Legado.cs:8", codepages.Evidence);
        Assert.Contains("CodePagesEncodingProvider", codepages.Proposal);
    }

    [Fact]
    public void Code_rules_respect_project_kind_and_skip_comments()
    {
        const string code = """
            public class Svc
            {
                // var t = new System.Timers.Timer(1000);  <- comentário não conta
                public static readonly System.Collections.Generic.List<int> Cache = new();
                public void Run() { System.Threading.Thread.Sleep(1000); System.Console.WriteLine("x"); }
            }
            """;
        var library = Project("Lib", ProjectKind.ClassLibrary);
        var libraryItems = ModernizationAdvisor.Analyze(library, Profile(library, code), [("Svc.cs", code)], CloudTarget.Aws);
        Assert.DoesNotContain(libraryItems, i => i.RuleId == "MOD-CS-TIMERS");        // only workers
        Assert.DoesNotContain(libraryItems, i => i.RuleId == "MOD-CS-STATIC-STATE");  // only web/services
        Assert.DoesNotContain(libraryItems, i => i.RuleId == "MOD-CS-CONSOLE-LOG");

        var service = Project("Svc", ProjectKind.WindowsService);
        var serviceItems = ModernizationAdvisor.Analyze(service, Profile(service, code), [("Svc.cs", code)], CloudTarget.Aws);
        var timers = serviceItems.Single(i => i.RuleId == "MOD-CS-TIMERS");
        Assert.Equal(1, timers.Occurrences); // Thread.Sleep only; the commented Timer is ignored
        Assert.Contains(serviceItems, i => i.RuleId == "MOD-CS-STATIC-STATE");
        Assert.Contains(serviceItems, i => i.RuleId == "MOD-CS-CONSOLE-LOG");
    }

    [Fact]
    public void Cloud_items_are_suppressed_when_no_cloud_target()
    {
        const string code = "public class A { void M() { var d = System.DateTime.Now; var e = System.Text.Encoding.Default; } }";
        var project = Project("Web", ProjectKind.Web);
        var items = ModernizationAdvisor.Analyze(project, Profile(project, code), [("A.cs", code)], CloudTarget.None);
        Assert.DoesNotContain(items, i => i.Kind == ModernizationKind.Cloud);
        Assert.Contains(items, i => i.RuleId == "MOD-CS-ENCODING-DEFAULT");
    }

    // ------------------------------------------------------------------ profiler

    [Fact]
    public void Profiler_reads_config_and_code_signals()
    {
        const string config = """
            <configuration>
              <connectionStrings>
                <add name="Db" connectionString="Data Source=srv-sql01;Initial Catalog=Loja;Integrated Security=True" providerName="System.Data.SqlClient" />
                <add name="Ora" connectionString="Data Source=ORCL;User Id=app;Password=x" providerName="Oracle.ManagedDataAccess.Client" />
              </connectionStrings>
              <appSettings>
                <add key="ErpUrl" value="http://erp.interno:8080/api" />
                <add key="PastaSaida" value="\\arquivos\exportacao" />
                <add key="ApiKey" value="sk_live_123" />
              </appSettings>
              <system.web>
                <sessionState mode="InProc" timeout="20" />
                <authentication mode="Windows" />
                <globalization culture="pt-BR" />
                <httpRuntime maxRequestLength="102400" executionTimeout="600" />
              </system.web>
              <system.net><mailSettings><smtp><network host="smtp.interno" /></smtp></mailSettings></system.net>
              <log4net><appender name="f" type="log4net.Appender.RollingFileAppender" /></log4net>
            </configuration>
            """;
        const string code = """
            using System.Messaging;
            public class Worker
            {
                void Run()
                {
                    var q = new MessageQueue(@".\private$\pedidos");
                    System.IO.File.WriteAllText(@"\\arquivos\exportacao\x.txt", "");
                    var img = System.Drawing.Image.FromFile("a.png");
                    System.Diagnostics.EventLog.WriteEntry("App", "x");
                }
            }
            """;
        var project = Project("Web", ProjectKind.Web);
        project.FrameworkReferences.Add("System.Messaging");
        project.FrameworkReferences.Add("System.EnterpriseServices"); // referenced but never used
        var profile = Profile(project, code, config);

        Assert.Equal(2, profile.Databases.Count);
        Assert.Contains(profile.Databases, d => d.Provider == "SQL Server" && d.Server == "srv-sql01" && d.IntegratedSecurity);
        Assert.Contains(profile.Databases, d => d.Provider == "Oracle");
        Assert.True(profile.Has(Signal.Oracle));
        Assert.Contains("http://erp.interno:8080", profile.ExternalEndpoints);
        Assert.True(profile.Has(Signal.UncPaths));
        Assert.Contains("arquivos", profile.Get(Signal.UncPaths)!.Details);
        Assert.True(profile.Has(Signal.SecretsInConfig));
        Assert.True(profile.Has(Signal.InProcSession));
        Assert.True(profile.Has(Signal.WindowsAuth));
        Assert.Equal("pt-BR", profile.Culture);
        Assert.True(profile.Has(Signal.LargeUploads));
        Assert.True(profile.Has(Signal.LongRequests));
        Assert.True(profile.Has(Signal.Smtp));
        Assert.True(profile.Has(Signal.FileLogging));
        Assert.True(profile.Has(Signal.Msmq));
        Assert.True(profile.Has(Signal.FileSystemWrites));
        Assert.True(profile.Has(Signal.SystemDrawing));
        Assert.True(profile.Has(Signal.EventLog));
        Assert.False(profile.Has(Signal.ComPlus), "unused GAC references must not count");
        Assert.Contains("Program.cs:6", profile.Get(Signal.Msmq)!.Locations);
    }

    [Fact]
    public void Profiler_finds_credentials_in_any_config_section_and_in_code()
    {
        const string config = """
            <configuration>
              <system.web>
                <identity impersonate="true" userName="DOMINIO\svc" password="Imp3rs0na!" />
                <sessionState mode="SQLServer" sqlConnectionString="Data Source=srv;User Id=sess;Password=S3ss!" />
                <machineKey validationKey="A1B2C3D4E5F6" decryptionKey="F6E5D4C3B2A1" validation="SHA1" />
              </system.web>
              <system.net><mailSettings><smtp><network host="smtp.interno" userName="loja" password="SmtpSenha!" /></smtp></mailSettings></system.net>
              <pagamentos gateway="X" apiKey="sk_live_abc" chaveSecreta="segredo-123" />
            </configuration>
            """;
        const string code = """
            public static class Integracao
            {
                public const string TokenErp = "erp-9f3b2c1d-token-legado";
                private static readonly string Conexao = "Server=srv;Database=db;User Id=app;Password=Senha@123;";
                private const string AwsKey = "AKIAIOSFODNN7EXAMPLE";
                private const string Modelo = "Password={0}"; // template, não é segredo
                public string Senha { get; set; } // propriedade, não é literal
            }
            """;
        var project = Project("Web", ProjectKind.Web);
        var profile = Profile(project, code, config);

        var inConfig = profile.Get(Signal.SecretsInConfig)!;
        foreach (var expected in new[] { "identity/@password", "sessionState/@sqlConnectionString", "machineKey/@validationKey", "network/@password", "pagamentos/@apiKey", "pagamentos/@chaveSecreta" })
            Assert.Contains(expected, inConfig.Details);

        var inCode = profile.Get(Signal.SecretsInCode)!;
        Assert.Equal(3, inCode.Count);
        Assert.Contains("TokenErp", inCode.Details);
        Assert.Contains("AKIAIOSFODNN7EXAMPLE", inCode.Details);

        var items = ModernizationAdvisor.Analyze(project, profile, [("Integracao.cs", code)], CloudTarget.Aws);
        var item = Assert.Single(items, i => i.RuleId == "MOD-SEC-SECRETS-CODE");
        Assert.Equal(ModernizationKind.Security, item.Kind);
        Assert.Equal("AWS Secrets Manager", item.AwsService);
        Assert.Contains(items, i => i.RuleId == "MOD-SEC-SECRETS");

        var rec = AwsArchitect.Recommend(project, profile);
        Assert.Contains(rec.Prerequisites, p => p.Contains("credenciais embutidas no código"));
    }

    [Fact]
    public void Profiler_parses_ef6_entity_connection_strings()
    {
        var db = ApplicationProfiler.ParseConnectionString("Entities",
            "metadata=res://*/Model.csdl|res://*/Model.ssdl|res://*/Model.msl;provider=System.Data.SqlClient;provider connection string=&quot;data source=SQL01\\INST;initial catalog=Vendas;integrated security=True;MultipleActiveResultSets=True&quot;",
            "System.Data.EntityClient", "P");
        Assert.Equal("SQL Server", db.Provider);
        Assert.Equal(@"SQL01\INST", db.Server);
        Assert.Equal("Vendas", db.Database);
        Assert.True(db.IntegratedSecurity);
    }

    [Theory]
    [InlineData("srv-sql01", true)]
    [InlineData("erp.interno", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("192.168.0.10", true)]
    [InlineData("api.exemplo.com.br", false)]
    [InlineData("localhost", false)]
    [InlineData("(LocalDb)\\MSSQLLocalDB", false)]
    public void Internal_hosts_are_recognised(string host, bool expected) => Assert.Equal(expected, ModernizationAdvisor.IsInternalHost(host));

    // ------------------------------------------------------------------ hosting recommendation

    [Fact]
    public void Web_without_windows_dependencies_goes_to_fargate_linux_with_prerequisites()
    {
        const string code = """
            public class HomeController : Controller
            {
                public ActionResult Index() { Session["x"] = 1; return View(); }
            }
            """;
        var project = Project("Web", ProjectKind.Web);
        var profile = Profile(project, code, "<configuration><system.web><authentication mode=\"Forms\"/><sessionState mode=\"InProc\"/></system.web></configuration>");
        var rec = AwsArchitect.Recommend(project, profile);

        Assert.Equal(AwsHosting.EcsFargate, rec.Primary);
        Assert.Empty(rec.HardWindowsDependencies);
        Assert.Contains(rec.Prerequisites, p => p.Contains("ElastiCache"));
        Assert.Contains(rec.Prerequisites, p => p.Contains("Data Protection"));
        Assert.Contains(rec.Alternatives, a => a.Contains("App Runner"));
        Assert.DoesNotContain(rec.Alternatives, a => a.Contains("Lambda")); // has views/session: not API-only
    }

    [Fact]
    public void Api_only_web_gets_lambda_as_alternative()
    {
        const string code = """
            [ApiController]
            public class PedidosController : ControllerBase { [HttpGet] public IActionResult Get() => Ok(); }
            """;
        var project = Project("Api", ProjectKind.Web);
        var rec = AwsArchitect.Recommend(project, Profile(project, code));
        Assert.Equal(AwsHosting.EcsFargate, rec.Primary);
        Assert.Contains(rec.Alternatives, a => a.Contains("Lambda"));
    }

    [Fact]
    public void Hard_windows_dependency_forces_windows_containers_and_propagates_through_project_references()
    {
        const string libCode = """
            public class Impressao
            {
                void Imprimir() { var excel = Microsoft.Office.Interop.Excel.Application; Microsoft.Win32.Registry.LocalMachine.OpenSubKey("x"); }
            }
            """;
        var lib = Project("Lib", ProjectKind.ClassLibrary);
        var libProfile = Profile(lib, libCode);
        var libRec = AwsArchitect.Recommend(lib, libProfile);
        Assert.Equal(AwsHosting.NotDeployable, libRec.Primary);
        Assert.Contains(libRec.Rationale, r => r.Contains("propagam"));

        var web = Project("Web", ProjectKind.Web);
        var merged = Profile(web, "public class HomeController : Controller { }").MergeWith([libProfile]);
        var rec = AwsArchitect.Recommend(web, merged);
        Assert.Equal(AwsHosting.EcsWindows, rec.Primary);
        Assert.True(rec.RequiresWindows);
        Assert.Contains(rec.HardWindowsDependencies, d => d.Contains("Office Interop"));
        Assert.Contains(rec.HardWindowsDependencies, d => d.Contains("Registro"));
        Assert.Contains("Lib", merged.MergedFrom);
    }

    [Fact]
    public void Soft_windows_dependencies_keep_linux_but_list_prerequisites()
    {
        const string code = """public class T { void M() { var b = new System.Drawing.Bitmap(1, 1); System.Diagnostics.EventLog.WriteEntry("a", "b"); } }""";
        var web = Project("Web", ProjectKind.Web);
        var rec = AwsArchitect.Recommend(web, Profile(web, code));
        Assert.Equal(AwsHosting.EcsFargate, rec.Primary);
        Assert.Equal(2, rec.SoftWindowsDependencies.Count);
        Assert.Contains(rec.Rationale, r => r.Contains("MOD-WIN-"));
    }

    [Fact]
    public void Queue_driven_service_becomes_fargate_worker_and_timer_service_becomes_scheduled_task()
    {
        var consumer = Project("Consumer", ProjectKind.WindowsService);
        consumer.FrameworkReferences.Add("System.Messaging");
        var consumerRec = AwsArchitect.Recommend(consumer, Profile(consumer, "using System.Messaging; class S : ServiceBase { MessageQueue q; }"));
        Assert.Equal(AwsHosting.EcsFargateWorker, consumerRec.Primary);
        Assert.Contains(consumerRec.Alternatives, a => a.Contains("Lambda"));
        Assert.Contains(consumerRec.Prerequisites, p => p.Contains("SQS"));

        var timer = Project("Timer", ProjectKind.WindowsService);
        var timerRec = AwsArchitect.Recommend(timer, Profile(timer, "class S : ServiceBase { void Run() { while (true) { System.Threading.Thread.Sleep(System.TimeSpan.FromMinutes(5)); } } }"));
        Assert.Equal(AwsHosting.EcsScheduledTask, timerRec.Primary);
        Assert.Contains(timerRec.Prerequisites, p => p.Contains("BackgroundService"));

        var tests = Project("Tests", ProjectKind.Test);
        Assert.Equal(AwsHosting.NotDeployable, AwsArchitect.Recommend(tests, Profile(tests, "")).Primary);
        var desktop = Project("Desk", ProjectKind.Desktop);
        desktop.UsesWinForms = true;
        Assert.Equal(AwsHosting.Desktop, AwsArchitect.Recommend(desktop, Profile(desktop, "")).Primary);
    }

    [Fact]
    public void Event_driven_automation_without_windows_dependencies_becomes_lambda()
    {
        const string code = """
            using Microsoft.Exchange.WebServices.Data;
            using CsvHelper;
            class Importador
            {
                static void Main()
                {
                    var service = new ExchangeService();
                    var itens = service.FindItems(WellKnownFolderName.Inbox, new ItemView(10));
                    foreach (var f in System.IO.Directory.GetFiles(@"\\arquivos\entrada", "*.csv")) { var csv = new CsvReader(new System.IO.StreamReader(f)); System.IO.File.Move(f, f + ".ok"); }
                    System.Diagnostics.EventLog.WriteEntry("Importador", "ok");
                }
            }
            """;
        var console = Project("Importador", ProjectKind.Console, "Microsoft.Exchange.WebServices", "CsvHelper");
        var profile = Profile(console, code);
        Assert.True(profile.Has(Signal.MailboxReading));
        Assert.True(profile.Has(Signal.SpreadsheetFiles));

        var rec = AwsArchitect.Recommend(console, profile);
        Assert.Equal(AwsHosting.Lambda, rec.Primary);                       // EventLog is tolerated: it must go anyway
        Assert.Contains(rec.Rationale, r => r.Contains("SES") && r.Contains("S3 Event Notifications"));
        Assert.Contains(rec.Prerequisites, p => p.Contains("handler Lambda"));
        Assert.Contains(rec.Prerequisites, p => p.Contains("EWS"));
        Assert.Contains(rec.Alternatives, a => a.Contains("ECS Fargate agendada"));

        // Same automation with Quartz inside → scheduled ECS task, with a hint that the event trigger would be better
        var withQuartz = Project("Importador", ProjectKind.Console, "Microsoft.Exchange.WebServices", "CsvHelper", "Quartz");
        var scheduled = AwsArchitect.Recommend(withQuartz, Profile(withQuartz, code + "\nclass J { Quartz.IScheduler s; }"));
        Assert.Equal(AwsHosting.EcsScheduledTask, scheduled.Primary);
        Assert.Contains(scheduled.Rationale, r => r.Contains("agendador embutido"));

        // ACE OLE DB (Excel via Jet) is a hard Windows dependency
        var oledb = Project("Planilhas", ProjectKind.Console);
        var oledbRec = AwsArchitect.Recommend(oledb, Profile(oledb, """class P { void M() { var c = new System.Data.OleDb.OleDbConnection("Provider=Microsoft.ACE.OLEDB.12.0;Data Source=x.xlsx"); } }"""));
        Assert.Equal(AwsHosting.EcsWindows, oledbRec.Primary);
        Assert.Contains(oledbRec.HardWindowsDependencies, d => d.Contains("ACE/Jet"));
    }

    [Fact]
    public void Visual_basic_sources_are_profiled_case_insensitively_and_web_forms_are_counted()
    {
        const string vb = """
            Imports System.Data.SqlClient
            ' Dim comentado As New MessageQueue(".\private$\x")  <- comentário VB não conta
            Public Class Gerador
                Public Sub Gerar()
                    Using conexao As New sqlconnection("Server=s;Password=p")
                        Dim pasta = "\\arquivos\relatorios"
                        Session("Carrinho") = 1
                        Dim excel As New Microsoft.Office.Interop.Excel.Application()
                    End Using
                End Sub
            End Class
            """;
        var project = Project("Relatorios", ProjectKind.Web);
        project.Language = "VB";
        project.Items.Add(new ProjectItem { ItemType = "Content", FullPath = Path.Combine(project.ProjectDir, "Vendas.aspx") });
        project.Items.Add(new ProjectItem { ItemType = "Content", FullPath = Path.Combine(project.ProjectDir, "Site.master") });
        var profile = ApplicationProfiler.Analyze(project, [("Gerador.vb", vb)], null);

        Assert.True(profile.Has(Signal.SqlServer));          // lowercase `sqlconnection` matched
        Assert.True(profile.Has(Signal.UncPaths));
        Assert.True(profile.Has(Signal.InProcSession));      // Session("...") VB syntax
        Assert.True(profile.Has(Signal.OfficeInterop));
        Assert.False(profile.Has(Signal.Msmq));              // VB comment skipped
        Assert.Equal(2, profile.Count(Signal.WebForms));

        var rec = AwsArchitect.Recommend(project, profile);
        Assert.Equal(AwsHosting.Ec2Windows, rec.Primary);    // pure Web Forms app: IIS until rewritten
        Assert.Contains(rec.Prerequisites, p => p.Contains("VB.NET"));
        Assert.Contains(rec.Alternatives, a => a.Contains("Razor Pages"));
    }

    [Fact]
    public void Binding_redirect_public_key_tokens_are_not_secrets()
    {
        const string config = """
            <configuration>
              <runtime>
                <assemblyBinding xmlns="urn:schemas-microsoft-com:asm.v1">
                  <dependentAssembly><assemblyIdentity name="Newtonsoft.Json" publicKeyToken="30ad4fe6b2a6aeed" /><bindingRedirect oldVersion="0.0.0.0-12.0.0.0" newVersion="12.0.0.0" /></dependentAssembly>
                </assemblyBinding>
              </runtime>
            </configuration>
            """;
        var project = Project("Web", ProjectKind.Web);
        Assert.False(Profile(project, "class A { }", config).Has(Signal.SecretsInConfig));
    }

    // ------------------------------------------------------------------ Dockerfile

    [Fact]
    public void Dockerfile_for_linux_web_sets_port_culture_timezone_and_non_root_user()
    {
        var web = Project("Loja.Web", ProjectKind.Web);
        var profile = Profile(web, "class C { void M() { var d = System.DateTime.Now; } }", "<configuration><system.web><globalization culture=\"pt-BR\"/></system.web></configuration>");
        var rec = AwsArchitect.Recommend(web, profile);
        var dockerfile = AwsArchitect.Dockerfile(web, rec, profile, @"Loja.Web\Loja.Web.csproj", [@"Loja.Core\Loja.Core.csproj"]);

        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final", dockerfile);
        Assert.Contains("COPY [\"Loja.Core/Loja.Core.csproj\", \"Loja.Core/\"]", dockerfile);
        Assert.Contains("RUN dotnet restore \"Loja.Web/Loja.Web.csproj\"", dockerfile);
        Assert.Contains("ASPNETCORE_HTTP_PORTS=8080", dockerfile);
        Assert.Contains("LANG=pt_BR.UTF-8", dockerfile);
        Assert.Contains("TZ=America/Sao_Paulo", dockerfile);
        Assert.Contains("USER $APP_UID", dockerfile);
        Assert.Contains("ENTRYPOINT [\"dotnet\", \"Loja.Web.dll\"]", dockerfile);
    }

    [Fact]
    public void Dockerfile_for_windows_worker_uses_windows_images_and_runtime_base()
    {
        var svc = Project("Svc", ProjectKind.WindowsService);
        var profile = Profile(svc, "class S : ServiceBase { void M() { Microsoft.Win32.Registry.LocalMachine.OpenSubKey(\"x\"); } }");
        var rec = AwsArchitect.Recommend(svc, profile);
        Assert.Equal(AwsHosting.EcsWindows, rec.Primary);
        var dockerfile = AwsArchitect.Dockerfile(svc, rec, profile, @"Svc\Svc.csproj", []);
        Assert.Contains("sdk:10.0-windowsservercore-ltsc2022", dockerfile);
        Assert.Contains("mcr.microsoft.com/dotnet/runtime:10.0-windowsservercore-ltsc2022", dockerfile);
        Assert.DoesNotContain("USER $APP_UID", dockerfile);
        Assert.DoesNotContain("EXPOSE", dockerfile);
    }

    // ------------------------------------------------------------------ Program.cs

    [Fact]
    public void Generated_program_has_health_endpoint_forwarded_headers_and_default_culture()
    {
        var project = Project("Web", ProjectKind.Web);
        var hints = new ProgramHints { Culture = "pt-BR", GlobalDenyAnonymous = true };
        var program = ProgramGenerator.GenerateWeb(new WebProgramInput(project, new StartupPlan(), hints, HasApiControllers: true, HasMvcControllers: true,
            UsesHttpContextAccessor: false, KeepNewtonsoft: false, HasSwagger: false, UsesOutputCache: false, Log4NetConfigFile: false, CloudReady: true));

        Assert.Contains("builder.Services.AddHealthChecks();", program);
        Assert.Contains("app.MapHealthChecks(\"/health\").AllowAnonymous();", program);
        Assert.Contains("ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto", program);
        Assert.Contains("CultureInfo.DefaultThreadCurrentCulture", program);
        Assert.True(program.IndexOf("app.UseForwardedHeaders();", StringComparison.Ordinal) < program.IndexOf("app.UseHttpsRedirection();", StringComparison.Ordinal));

        var plain = ProgramGenerator.GenerateWeb(new WebProgramInput(project, new StartupPlan(), new ProgramHints(), true, true, false, false, false, false, false, CloudReady: false));
        Assert.DoesNotContain("MapHealthChecks", plain);
        Assert.DoesNotContain("ForwardedHeaders", plain);
    }
}
