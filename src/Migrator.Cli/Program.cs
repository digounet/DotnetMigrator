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
Option<string> Llm() => new("--llm") { Description = "Assistência por LLM (opcional): none (padrão) ou ollama. Com uma LLM, a ferramenta tenta corrigir os erros do build de verificação, rascunha conversões de código legado e escreve o resumo executivo da arquitetura.", DefaultValueFactory = _ => "none" };
Option<string?> LlmModel() => new("--llm-model") { Description = "Modelo do provedor (ollama: padrão qwen2.5-coder:3b)." };
Option<string?> LlmEndpoint() => new("--llm-endpoint") { Description = "Endpoint do provedor (ollama: padrão http://localhost:11434)." };
Option<int> LlmRounds() => new("--llm-rounds") { Description = "Máximo de rodadas build → correção → build com a LLM.", DefaultValueFactory = _ => 3 };
Option<bool> LlmNoCache() => new("--llm-no-cache") { Description = "Não usar o cache de respostas da LLM (~/.migrator/llm-cache)." };
Option<int> LlmTimeout() => new("--llm-timeout") { Description = "Tempo máximo de cada chamada à LLM, em minutos (modelos locais podem ser lentos).", DefaultValueFactory = _ => 6 };
static Migrator.Core.Llm.LlmOptions BuildLlm(string? provider, string? model, string? endpoint, int rounds, bool noCache, int timeoutMinutes) => new()
{
    Provider = string.IsNullOrWhiteSpace(provider) ? "none" : provider,
    Model = model,
    Endpoint = endpoint,
    MaxFixRounds = Math.Max(0, rounds),
    CacheDir = noCache ? null : Migrator.Core.Llm.LlmOptions.DefaultCacheDir,
    Timeout = TimeSpan.FromMinutes(Math.Max(1, timeoutMinutes))
};
static Migrator.Core.Models.CloudTarget ParseCloud(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "none" or "nenhuma" or "off" => Migrator.Core.Models.CloudTarget.None,
    _ => Migrator.Core.Models.CloudTarget.Aws
};

var analyzeInput = Input();
var analyzeReport = Report();
var analyzeOffline = Offline();
var analyzeCloud = Cloud();
var analyzeLlm = Llm(); var analyzeLlmModel = LlmModel(); var analyzeLlmEndpoint = LlmEndpoint(); var analyzeLlmNoCache = LlmNoCache(); var analyzeLlmTimeout = LlmTimeout();
var analyze = new Command("analyze", "Analisa a aplicação e gera o inventário de migração, as sugestões de modernização e a arquitetura alvo, sem gravar código.")
{
    analyzeInput, analyzeReport, analyzeOffline, analyzeCloud, analyzeLlm, analyzeLlmModel, analyzeLlmEndpoint, analyzeLlmNoCache, analyzeLlmTimeout
};
analyze.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(analyzeInput)!,
    ReportDir = parse.GetValue(analyzeReport),
    Offline = parse.GetValue(analyzeOffline),
    Cloud = ParseCloud(parse.GetValue(analyzeCloud)),
    Llm = BuildLlm(parse.GetValue(analyzeLlm), parse.GetValue(analyzeLlmModel), parse.GetValue(analyzeLlmEndpoint), 0, parse.GetValue(analyzeLlmNoCache), parse.GetValue(analyzeLlmTimeout)),
    DryRun = true,
    VerifyBuild = false
}, ct));

var migrateInput = Input();
var migrateReport = Report();
var migrateOffline = Offline();
var migrateCloud = Cloud();
var migrateLlm = Llm(); var migrateLlmModel = LlmModel(); var migrateLlmEndpoint = LlmEndpoint(); var migrateLlmRounds = LlmRounds(); var migrateLlmNoCache = LlmNoCache(); var migrateLlmTimeout = LlmTimeout();
var output = new Option<string?>("--output", "-o") { Description = "Pasta de saída da aplicação migrada (padrão: <pasta-da-solução>.net10, ao lado da original)." };
var force = new Option<bool>("--force") { Description = "Substitui uma saída gerada anteriormente pelo Migrator." };
var keepSecrets = new Option<bool>("--keep-secrets") { Description = "Mantém senhas e chaves dentro do appsettings*.json gerado em vez de movê-las para _secrets/ (não recomendado)." };
var noBuild = new Option<bool>("--no-build") { Description = "Não executa o build de verificação após a migração." };
var timeout = new Option<int>("--build-timeout") { Description = "Tempo máximo do build de verificação, em minutos.", DefaultValueFactory = _ => 30 };
var migrate = new Command("migrate", "Gera uma cópia migrada para .NET 10 (com Dockerfiles), compila a saída e gera o inventário, as sugestões de modernização e a arquitetura alvo.")
{
    migrateInput, output, migrateReport, migrateOffline, migrateCloud, force, noBuild, timeout, keepSecrets,
    migrateLlm, migrateLlmModel, migrateLlmEndpoint, migrateLlmRounds, migrateLlmNoCache, migrateLlmTimeout
};
migrate.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(migrateInput)!,
    OutputDir = parse.GetValue(output),
    ReportDir = parse.GetValue(migrateReport),
    Offline = parse.GetValue(migrateOffline),
    Cloud = ParseCloud(parse.GetValue(migrateCloud)),
    Llm = BuildLlm(parse.GetValue(migrateLlm), parse.GetValue(migrateLlmModel), parse.GetValue(migrateLlmEndpoint), parse.GetValue(migrateLlmRounds), parse.GetValue(migrateLlmNoCache), parse.GetValue(migrateLlmTimeout)),
    Force = parse.GetValue(force),
    KeepSecrets = parse.GetValue(keepSecrets),
    VerifyBuild = !parse.GetValue(noBuild),
    BuildTimeout = TimeSpan.FromMinutes(Math.Max(1, parse.GetValue(timeout)))
}, ct));

var root = new RootCommand("Migrator — moderniza aplicações .NET Framework (MVC, Web API, console, Windows Service, bibliotecas) para .NET 10.")
{
    analyze, migrate
};
return await root.Parse(args).InvokeAsync();
