using Migrator.Core.Migration;

namespace Migrator.Tests;

public class ConfigurationInjectorTests
{
    [Fact]
    public void Console_main_class_gets_a_static_configuration_built_from_appsettings_and_environment()
    {
        const string source = """
            using System;

            namespace Loja.Importador
            {
                internal static class Program
                {
                    private static int Main(string[] args)
                    {
                        var pasta = configuration["AppSettings:PastaEntrada"];
                        var cs = configuration.GetConnectionString("DefaultConnection");
                        return 0;
                    }
                }
            }
            """;
        var (text, changes) = ConfigurationInjector.Inject(source);

        Assert.Contains("private static readonly IConfiguration configuration = new ConfigurationBuilder()", text);
        Assert.Contains(".AddJsonFile(\"appsettings.json\", optional: true)", text);
        Assert.Contains("DOTNET_ENVIRONMENT", text);
        Assert.Contains(".AddEnvironmentVariables()", text);
        Assert.Contains("using Microsoft.Extensions.Configuration;", text);
        Assert.Contains(changes, c => c.RuleId == "CS-CONFIG-STATIC");
        Assert.True(text.IndexOf("IConfiguration configuration", StringComparison.Ordinal) < text.IndexOf("Main(", StringComparison.Ordinal));
    }

    [Fact]
    public void Background_service_gets_constructor_injection_and_field_initializers_move_into_it()
    {
        const string source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Hosting;

            namespace Loja.Worker
            {
                public partial class SincronizacaoService : BackgroundService
                {
                    private Thread _trabalhador;
                    private readonly int _intervalo = int.Parse(configuration["AppSettings:IntervaloMinutos"]);

                    public SincronizacaoService()
                    {
                    }

                    protected override Task ExecuteAsync(CancellationToken stoppingToken)
                    {
                        var conexao = configuration.GetConnectionString("DefaultConnection");
                        return Task.CompletedTask;
                    }
                }
            }
            """;
        var (text, changes) = ConfigurationInjector.Inject(source);

        Assert.Contains("private readonly IConfiguration configuration;", text);
        Assert.Contains("public SincronizacaoService(IConfiguration configuration)", text);
        Assert.Contains("this.configuration = configuration;", text);
        Assert.Contains("_intervalo = int.Parse(configuration[\"AppSettings:IntervaloMinutos\"]);", text);
        Assert.Contains("private readonly int _intervalo;", text);          // initializer moved
        Assert.Contains(changes, c => c.RuleId == "CS-CONFIG-INJECT" && c.Description.Contains("inicializadores"));
    }

    [Fact]
    public void Background_service_without_constructor_gets_one()
    {
        const string source = """
            public class Worker : BackgroundService
            {
                protected override Task ExecuteAsync(CancellationToken t) { var x = configuration["A"]; return Task.CompletedTask; }
            }
            """;
        var (text, _) = ConfigurationInjector.Inject(source);
        Assert.Contains("public Worker(IConfiguration configuration)", text);
        Assert.Contains("this.configuration = configuration;", text);
    }

    [Fact]
    public void Leaves_other_classes_and_already_declared_configuration_alone()
    {
        const string library = "public class Repositorio { public string Cs => configuration.GetConnectionString(\"Db\"); }";
        Assert.Equal(library, ConfigurationInjector.Inject(library).Text);

        const string declared = "class Program { static IConfiguration configuration; static void Main() { var x = configuration[\"A\"]; } }";
        Assert.Equal(declared, ConfigurationInjector.Inject(declared).Text);

        const string none = "class Program { static void Main() { } }";
        Assert.Equal(none, ConfigurationInjector.Inject(none).Text);
    }
}
