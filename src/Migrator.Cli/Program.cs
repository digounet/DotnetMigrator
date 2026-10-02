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
Option<string> Cloud() => new("--cloud") { Description = "Nuvem de destino para a proposta de arquitetura e os Dockerfiles: aws (padrão) ou none.", DefaultValueFactory = _ => "aws" };
static Migrator.Core.Models.CloudTarget ParseCloud(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "none" or "nenhuma" or "off" => Migrator.Core.Models.CloudTarget.None,
    _ => Migrator.Core.Models.CloudTarget.Aws
};

var analyzeInput = Input();
var analyzeReport = Report();
var analyzeOffline = Offline();
var analyzeCloud = Cloud();
var analyze = new Command("analyze", "Analisa a aplicação e gera o inventário de migração, as sugestões de modernização e a arquitetura alvo, sem gravar código.")
{
    analyzeInput, analyzeReport, analyzeOffline, analyzeCloud
};
analyze.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(analyzeInput)!,
    ReportDir = parse.GetValue(analyzeReport),
    Offline = parse.GetValue(analyzeOffline),
    Cloud = ParseCloud(parse.GetValue(analyzeCloud)),
    DryRun = true,
    VerifyBuild = false
}, ct));

var migrateInput = Input();
var migrateReport = Report();
var migrateOffline = Offline();
var migrateCloud = Cloud();
var output = new Option<string?>("--output", "-o") { Description = "Pasta de saída da aplicação migrada (padrão: <pasta-da-solução>.net10, ao lado da original)." };
var force = new Option<bool>("--force") { Description = "Substitui uma saída gerada anteriormente pelo Migrator." };
var noBuild = new Option<bool>("--no-build") { Description = "Não executa o build de verificação após a migração." };
var timeout = new Option<int>("--build-timeout") { Description = "Tempo máximo do build de verificação, em minutos.", DefaultValueFactory = _ => 30 };
var migrate = new Command("migrate", "Gera uma cópia migrada para .NET 10 (com Dockerfiles), compila a saída e gera o inventário, as sugestões de modernização e a arquitetura alvo.")
{
    migrateInput, output, migrateReport, migrateOffline, migrateCloud, force, noBuild, timeout
};
migrate.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(migrateInput)!,
    OutputDir = parse.GetValue(output),
    ReportDir = parse.GetValue(migrateReport),
    Offline = parse.GetValue(migrateOffline),
    Cloud = ParseCloud(parse.GetValue(migrateCloud)),
    Force = parse.GetValue(force),
    VerifyBuild = !parse.GetValue(noBuild),
    BuildTimeout = TimeSpan.FromMinutes(Math.Max(1, parse.GetValue(timeout)))
}, ct));

var root = new RootCommand("Migrator — moderniza aplicações .NET Framework (MVC, Web API, console, Windows Service, bibliotecas) para .NET 10.")
{
    analyze, migrate
};
return await root.Parse(args).InvokeAsync();
