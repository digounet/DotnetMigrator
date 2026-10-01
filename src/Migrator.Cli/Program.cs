using System.CommandLine;
using System.Text;
using Migrator.Cli;

Console.OutputEncoding = Encoding.UTF8;

Argument<string> Input() => new("entrada")
{
    Description = "Caminho da solução (.sln/.slnx), de um projeto (.csproj) ou de um diretório com projetos."
};
Option<string?> Report() => new("--report", "-r") { Description = "Pasta onde gravar os relatórios (HTML, Markdown, CSV e Excel)." };
Option<bool> Offline() => new("--offline") { Description = "Não consultar o nuget.org (a compatibilidade dos pacotes não é verificada)." };

var analyzeInput = Input();
var analyzeReport = Report();
var analyzeOffline = Offline();
var analyze = new Command("analyze", "Analisa a aplicação e gera o inventário de migração, sem gravar código.")
{
    analyzeInput, analyzeReport, analyzeOffline
};
analyze.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(analyzeInput)!,
    ReportDir = parse.GetValue(analyzeReport),
    Offline = parse.GetValue(analyzeOffline),
    DryRun = true,
    VerifyBuild = false
}, ct));

var migrateInput = Input();
var migrateReport = Report();
var migrateOffline = Offline();
var output = new Option<string?>("--output", "-o") { Description = "Pasta de saída da aplicação migrada (padrão: <pasta-da-solução>.net10, ao lado da original)." };
var force = new Option<bool>("--force") { Description = "Substitui uma saída gerada anteriormente pelo Migrator." };
var noBuild = new Option<bool>("--no-build") { Description = "Não executa o build de verificação após a migração." };
var timeout = new Option<int>("--build-timeout") { Description = "Tempo máximo do build de verificação, em minutos.", DefaultValueFactory = _ => 30 };
var migrate = new Command("migrate", "Gera uma cópia migrada para .NET 10, compila a saída e gera o inventário do que precisa de ação manual.")
{
    migrateInput, output, migrateReport, migrateOffline, force, noBuild, timeout
};
migrate.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(migrateInput)!,
    OutputDir = parse.GetValue(output),
    ReportDir = parse.GetValue(migrateReport),
    Offline = parse.GetValue(migrateOffline),
    Force = parse.GetValue(force),
    VerifyBuild = !parse.GetValue(noBuild),
    BuildTimeout = TimeSpan.FromMinutes(Math.Max(1, parse.GetValue(timeout)))
}, ct));

var root = new RootCommand("Migrator — moderniza aplicações .NET Framework (MVC, Web API, console, Windows Service, bibliotecas) para .NET 10.")
{
    analyze, migrate
};
return await root.Parse(args).InvokeAsync();
