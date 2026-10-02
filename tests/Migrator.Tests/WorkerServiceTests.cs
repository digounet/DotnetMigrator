using Migrator.Core.Migration;

namespace Migrator.Tests;

public class WorkerServiceTests
{
    private const string Service = """
        using System;
        using System.ServiceProcess;
        using System.Threading;

        namespace Loja.Worker
        {
            public partial class SincronizacaoService : ServiceBase
            {
                private Thread _trabalhador;

                public SincronizacaoService()
                {
                    InitializeComponent();
                    this.CanStop = true;
                }

                protected override void OnStart(string[] args)
                {
                    Console.WriteLine("iniciando com " + args.Length + " argumento(s)");
                    _trabalhador = new Thread(Executar) { IsBackground = true };
                    _trabalhador.Start();
                }

                protected override void OnStop()
                {
                    _trabalhador.Abort();
                }

                protected override void OnPause()
                {
                }

                private void Executar() { }
            }
        }
        """;

    [Fact]
    public void Converts_service_base_to_background_service()
    {
        var (text, changes, rewriter) = WorkerServiceRewriter.Rewrite(Service);

        Assert.Contains("class SincronizacaoService : BackgroundService", text);
        Assert.Contains("protected override Task ExecuteAsync(CancellationToken stoppingToken)", text);
        Assert.Contains("var args = Environment.GetCommandLineArgs()", text);          // OnStart used args
        Assert.Contains("return Task.CompletedTask;", text);
        Assert.Contains("public override Task StopAsync(CancellationToken cancellationToken)", text);
        Assert.Contains("return base.StopAsync(cancellationToken);", text);
        Assert.DoesNotContain("InitializeComponent();", text);
        Assert.DoesNotContain("this.CanStop = true;", text);
        Assert.DoesNotContain("using System.ServiceProcess;", text);
        Assert.Contains("using Microsoft.Extensions.Hosting;", text);
        Assert.Contains("using System.Threading.Tasks;", text);
        Assert.Contains("protected void OnPause()", text);                             // kept, no longer override
        Assert.Contains("TODO Migrator (Worker): OnPause", text);
        Assert.Single(rewriter.Warnings);
        Assert.Contains(changes, c => c.RuleId == "CS-WORKER");
        Assert.Contains(changes, c => c.RuleId == "CS-WORKER-DESIGNER");
    }

    [Fact]
    public void Leaves_files_without_services_untouched()
    {
        const string plain = "namespace X { public class Helper { public void M() { } } }";
        var (text, changes, _) = WorkerServiceRewriter.Rewrite(plain);
        Assert.Equal(plain, text);
        Assert.Empty(changes);
    }

    [Fact]
    public void Discovers_services_and_generates_a_generic_host_program()
    {
        const string designer = """
            namespace Loja.Worker
            {
                partial class SincronizacaoService
                {
                    private void InitializeComponent() { this.ServiceName = "LojaSincronizacao"; }
                }
            }
            """;
        var info = WorkerServiceRewriter.Discover([("SincronizacaoService.cs", Service), ("SincronizacaoService.Designer.cs", designer), ("Program.cs", "class Program { static void Main() { ServiceBase.Run(new SincronizacaoService()); } }")]);

        Assert.Single(info.Services);
        Assert.Equal(("SincronizacaoService", "Loja.Worker"), info.Services[0]);
        Assert.Equal("LojaSincronizacao", info.ServiceNames["SincronizacaoService"]);

        var program = WorkerServiceRewriter.GenerateProgram("Loja.Worker", info);
        Assert.Contains("using Loja.Worker;", program);
        Assert.Contains("Host.CreateApplicationBuilder(args)", program);
        Assert.Contains("AddWindowsService(options => options.ServiceName = \"LojaSincronizacao\")", program);
        Assert.Contains("AddHostedService<SincronizacaoService>()", program);
        Assert.Contains("host.Run();", program);
    }
}
