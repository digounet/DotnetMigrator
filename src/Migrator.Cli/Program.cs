using System.CommandLine;
using System.Text;
using Migrator.Cli;

Console.OutputEncoding = Encoding.UTF8;

Argument<string> Input() => new("entrada")
{
    Description = "Caminho da solução (.sln/.slnx), de um projeto (.csproj) ou de um diretório com projetos."
};
Option<string?> Report() => new("--report", "-r") { Description = "Pasta onde gravar os relatórios (HTML, Markdown, CSV e Excel)." };
Option<bool> Offline() => new("--offline") { Description = "Não consultar o feed NuGet (a compatibilidade dos pacotes não é verificada)." };
Option<string?> NuGetConfig() => new("--nuget-config") { Description = "nuget.config do feed privado (Artifactory/Nexus). Copiado para a raiz da saída e usado na checagem de compatibilidade e no restore do build de verificação. Alternativa: variável MIGRATOR_NUGET_CONFIG." };
Option<string?> NuGetSource() => new("--nuget-source") { Description = "URL do service index v3 do feed (…/index.json) para a checagem de compatibilidade; gera um nuget.config mínimo quando não houver um." };
Option<string> Cloud() => new("--cloud") { Description = "Nuvem de destino para a proposta de arquitetura e os Dockerfiles: aws (padrão) ou none.", DefaultValueFactory = _ => "aws" };
Option<string> Target() => new("--target") { Description = "Destino: framework (padrão, lift-and-shift: não altera o código, só eleva os projetos para .NET Framework 4.8.1 e hospeda em EC2 Windows) ou net10 (reescreve o código para .NET 10 e hospeda em ECS Fargate).", DefaultValueFactory = _ => "framework" };
Option<bool> Serverless() => new("--serverless") { Description = "Recomenda AWS Lambda (e gera o handler/gatilhos) para automações orientadas a evento. Sem a opção, o foco é lift-and-shift: consoles e serviços mantêm o Main() e rodam como tarefa ECS agendada ou worker." };
Option<string?> Iac() => new("--iac") { Description = "Infraestrutura como código gerada no migrate: cloudformation (padrão para framework; layout da plataforma: infra/service.yml + infra/{dev,hom,prod}/parameters.json) ou terraform (padrão para net10)." };
static Migrator.Core.Models.MigrationTarget ParseTarget(string? value) => value?.Trim().ToLowerInvariant() switch
{
    "net10" or "net10.0" or "modern" or "core" => Migrator.Core.Models.MigrationTarget.Net10,
    _ => Migrator.Core.Models.MigrationTarget.NetFramework
};
static Migrator.Core.Models.IacTool? ParseIac(string? value) => value?.Trim().ToLowerInvariant() switch
{
    null or "" => null,
    "cloudformation" or "cfn" or "cf" => Migrator.Core.Models.IacTool.CloudFormation,
    _ => Migrator.Core.Models.IacTool.Terraform
};
Option<string> Llm() => new("--llm") { Description = "Assistência por LLM (opcional): none (padrão), ollama (local) ou api (LLM corporativa via API com client credentials). Com uma LLM, a ferramenta tenta corrigir os erros do build de verificação, rascunha conversões de código legado e escreve o resumo executivo da arquitetura.", DefaultValueFactory = _ => "none" };
Option<string?> LlmModel() => new("--llm-model") { Description = "Modelo do provedor (ollama: padrão qwen2.5-coder:3b; api: nome enviado no corpo, ou MIGRATOR_LLM_MODEL)." };
Option<string?> LlmEndpoint() => new("--llm-endpoint") { Description = "Endpoint do provedor (ollama: padrão http://localhost:11434; api: a URL do gateway é fixa no código, esta opção só sobrepõe para testes)." };
Option<string?> LlmClientId() => new("--llm-client-id") { Description = "api: client_id do OAuth2 (ou MIGRATOR_LLM_CLIENT_ID)." };
Option<string?> LlmClientSecret() => new("--llm-client-secret") { Description = "api: client_secret do OAuth2 (prefira a variável MIGRATOR_LLM_CLIENT_SECRET para não ficar no histórico do shell)." };
Option<string?> LlmTokenUrl() => new("--llm-token-url") { Description = "api: sobrepõe o endpoint de token OAuth2 fixo no código (ou MIGRATOR_LLM_TOKEN_URL)." };
Option<string?> LlmScope() => new("--llm-scope") { Description = "api: escopo do token (ou MIGRATOR_LLM_SCOPE)." };
Option<int> LlmRounds() => new("--llm-rounds") { Description = "Máximo de rodadas build → correção → build com a LLM.", DefaultValueFactory = _ => 3 };
Option<bool> LlmNoCache() => new("--llm-no-cache") { Description = "Não usar o cache de respostas da LLM (~/.migrator/llm-cache)." };
Option<int> LlmTimeout() => new("--llm-timeout") { Description = "Tempo máximo de cada chamada à LLM, em minutos (modelos locais podem ser lentos).", DefaultValueFactory = _ => 6 };
static Migrator.Core.Llm.LlmOptions BuildLlm(string? provider, string? model, string? endpoint, int rounds, bool noCache, int timeoutMinutes, string? clientId = null, string? clientSecret = null, string? tokenUrl = null, string? scope = null) => new()
{
    Provider = string.IsNullOrWhiteSpace(provider) ? "none" : provider,
    Model = model,
    Endpoint = endpoint,
    ClientId = clientId,
    ClientSecret = clientSecret,
    TokenUrl = tokenUrl,
    Scope = scope,
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
var analyzeCloud = Cloud(); var analyzeTarget = Target(); var analyzeServerless = Serverless();
var analyzeLlm = Llm(); var analyzeLlmModel = LlmModel(); var analyzeLlmEndpoint = LlmEndpoint(); var analyzeLlmNoCache = LlmNoCache(); var analyzeLlmTimeout = LlmTimeout();
var analyzeLlmClientId = LlmClientId(); var analyzeLlmClientSecret = LlmClientSecret(); var analyzeLlmTokenUrl = LlmTokenUrl(); var analyzeLlmScope = LlmScope();
var analyzeNuGetConfig = NuGetConfig(); var analyzeNuGetSource = NuGetSource();
var analyze = new Command("analyze", "Analisa a aplicação e gera o inventário de migração, as sugestões de modernização e a arquitetura alvo, sem gravar código.")
{
    analyzeInput, analyzeReport, analyzeOffline, analyzeCloud, analyzeTarget, analyzeServerless, analyzeNuGetConfig, analyzeNuGetSource, analyzeLlm, analyzeLlmModel, analyzeLlmEndpoint, analyzeLlmNoCache, analyzeLlmTimeout, analyzeLlmClientId, analyzeLlmClientSecret, analyzeLlmTokenUrl, analyzeLlmScope
};
analyze.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(analyzeInput)!,
    ReportDir = parse.GetValue(analyzeReport),
    Offline = parse.GetValue(analyzeOffline),
    Cloud = ParseCloud(parse.GetValue(analyzeCloud)),
    Target = ParseTarget(parse.GetValue(analyzeTarget)),
    Serverless = parse.GetValue(analyzeServerless),
    NuGetConfigPath = parse.GetValue(analyzeNuGetConfig),
    NuGetSourceUrl = parse.GetValue(analyzeNuGetSource),
    Llm = BuildLlm(parse.GetValue(analyzeLlm), parse.GetValue(analyzeLlmModel), parse.GetValue(analyzeLlmEndpoint), 0, parse.GetValue(analyzeLlmNoCache), parse.GetValue(analyzeLlmTimeout), parse.GetValue(analyzeLlmClientId), parse.GetValue(analyzeLlmClientSecret), parse.GetValue(analyzeLlmTokenUrl), parse.GetValue(analyzeLlmScope)),
    DryRun = true,
    VerifyBuild = false
}, ct));

var migrateInput = Input();
var migrateReport = Report();
var migrateOffline = Offline();
var migrateCloud = Cloud(); var migrateTarget = Target(); var migrateIac = Iac(); var migrateServerless = Serverless();
var migrateLlm = Llm(); var migrateLlmModel = LlmModel(); var migrateLlmEndpoint = LlmEndpoint(); var migrateLlmRounds = LlmRounds(); var migrateLlmNoCache = LlmNoCache(); var migrateLlmTimeout = LlmTimeout();
var migrateLlmClientId = LlmClientId(); var migrateLlmClientSecret = LlmClientSecret(); var migrateLlmTokenUrl = LlmTokenUrl(); var migrateLlmScope = LlmScope();
var migrateNuGetConfig = NuGetConfig(); var migrateNuGetSource = NuGetSource();
var output = new Option<string?>("--output", "-o") { Description = "Pasta de saída da aplicação migrada (padrão: <pasta-da-solução>.net481, ou .net10 com --target net10, ao lado da original)." };
var force = new Option<bool>("--force") { Description = "Substitui uma saída gerada anteriormente pelo Migrator." };
var keepSecrets = new Option<bool>("--keep-secrets") { Description = "Mantém senhas e chaves dentro do appsettings*.json gerado em vez de movê-las para _secrets/ (não recomendado)." };
var noTests = new Option<bool>("--no-tests") { Description = "Não executa os projetos de teste migrados após o build de verificação." };
var noSmoke = new Option<bool>("--no-smoke") { Description = "Não sobe as aplicações web migradas para testar /health após o build de verificação." };
var verifyDocker = new Option<bool>("--verify-docker") { Description = "Constrói as imagens dos Dockerfiles gerados com o Docker local (lento)." };
var noInfra = new Option<bool>("--no-infra") { Description = "Não gera infra/ (Terraform ou CloudFormation) nem .github/workflows/deploy.yml." };
var noBuild = new Option<bool>("--no-build") { Description = "Não executa o build de verificação após a migração." };
var timeout = new Option<int>("--build-timeout") { Description = "Tempo máximo do build de verificação, em minutos.", DefaultValueFactory = _ => 30 };
var migrate = new Command("migrate", "Lift-and-shift (padrão): copia a aplicação sem alterar o código em .NET Framework 4.8.1, externaliza URLs/e-mails/senhas para configuração e Secrets Manager e gera a infra EC2 Windows em CloudFormation. Com --target net10, reescreve o código para .NET 10 (ECS Fargate), compila a saída e gera Dockerfiles.")
{
    migrateInput, output, migrateReport, migrateOffline, migrateCloud, migrateTarget, migrateIac, migrateServerless, migrateNuGetConfig, migrateNuGetSource, force, noBuild, timeout, keepSecrets, noTests, noSmoke, verifyDocker, noInfra,
    migrateLlm, migrateLlmModel, migrateLlmEndpoint, migrateLlmRounds, migrateLlmNoCache, migrateLlmTimeout, migrateLlmClientId, migrateLlmClientSecret, migrateLlmTokenUrl, migrateLlmScope
};
migrate.SetAction((parse, ct) => RunCommand.ExecuteAsync(new Migrator.Core.Models.MigrationOptions
{
    InputPath = parse.GetValue(migrateInput)!,
    OutputDir = parse.GetValue(output),
    ReportDir = parse.GetValue(migrateReport),
    Offline = parse.GetValue(migrateOffline),
    Cloud = ParseCloud(parse.GetValue(migrateCloud)),
    Target = ParseTarget(parse.GetValue(migrateTarget)),
    Iac = ParseIac(parse.GetValue(migrateIac)),
    Serverless = parse.GetValue(migrateServerless),
    NuGetConfigPath = parse.GetValue(migrateNuGetConfig),
    NuGetSourceUrl = parse.GetValue(migrateNuGetSource),
    Llm = BuildLlm(parse.GetValue(migrateLlm), parse.GetValue(migrateLlmModel), parse.GetValue(migrateLlmEndpoint), parse.GetValue(migrateLlmRounds), parse.GetValue(migrateLlmNoCache), parse.GetValue(migrateLlmTimeout), parse.GetValue(migrateLlmClientId), parse.GetValue(migrateLlmClientSecret), parse.GetValue(migrateLlmTokenUrl), parse.GetValue(migrateLlmScope)),
    Force = parse.GetValue(force),
    KeepSecrets = parse.GetValue(keepSecrets),
    RunTests = !parse.GetValue(noTests),
    SmokeTest = !parse.GetValue(noSmoke),
    VerifyDocker = parse.GetValue(verifyDocker),
    GenerateInfrastructure = !parse.GetValue(noInfra),
    VerifyBuild = !parse.GetValue(noBuild),
    BuildTimeout = TimeSpan.FromMinutes(Math.Max(1, parse.GetValue(timeout)))
}, ct));

var portfolioInput = new Argument<string>("pasta") { Description = "Pasta que contém as soluções (.sln/.slnx) ou projetos das aplicações; cada solução vira uma aplicação do portfólio." };
var portfolioReport = Report(); var portfolioOffline = Offline(); var portfolioCloud = Cloud(); var portfolioTarget = Target(); var portfolioServerless = Serverless();
var portfolioLlm = Llm(); var portfolioLlmModel = LlmModel(); var portfolioLlmEndpoint = LlmEndpoint(); var portfolioLlmNoCache = LlmNoCache(); var portfolioLlmTimeout = LlmTimeout();
var portfolioLlmClientId = LlmClientId(); var portfolioLlmClientSecret = LlmClientSecret(); var portfolioLlmTokenUrl = LlmTokenUrl(); var portfolioLlmScope = LlmScope();
var portfolioNuGetConfig = NuGetConfig(); var portfolioNuGetSource = NuGetSource();
var baseline = new Option<string?>("--baseline") { Description = "portfolio.json de uma execução anterior para comparar a evolução (bloqueantes, atenção, impacto alto, esforço por aplicação)." };
var portfolio = new Command("portfolio", "Analisa todas as aplicações de uma pasta e consolida: ranking de esforço, gaps mais frequentes, bancos/hosts compartilhados, ondas de migração e serviços AWS.")
{
    portfolioInput, portfolioReport, portfolioOffline, portfolioCloud, portfolioTarget, portfolioServerless, portfolioNuGetConfig, portfolioNuGetSource, baseline, portfolioLlm, portfolioLlmModel, portfolioLlmEndpoint, portfolioLlmNoCache, portfolioLlmTimeout, portfolioLlmClientId, portfolioLlmClientSecret, portfolioLlmTokenUrl, portfolioLlmScope
};
portfolio.SetAction((parse, ct) => PortfolioCommand.ExecuteAsync(new Migrator.Core.Portfolio.PortfolioOptions
{
    RootDir = parse.GetValue(portfolioInput)!,
    ReportDir = parse.GetValue(portfolioReport),
    Offline = parse.GetValue(portfolioOffline),
    Cloud = ParseCloud(parse.GetValue(portfolioCloud)),
    Target = ParseTarget(parse.GetValue(portfolioTarget)),
    Serverless = parse.GetValue(portfolioServerless),
    BaselinePath = parse.GetValue(baseline),
    NuGetConfigPath = parse.GetValue(portfolioNuGetConfig),
    NuGetSourceUrl = parse.GetValue(portfolioNuGetSource),
    Llm = BuildLlm(parse.GetValue(portfolioLlm), parse.GetValue(portfolioLlmModel), parse.GetValue(portfolioLlmEndpoint), 0, parse.GetValue(portfolioLlmNoCache), parse.GetValue(portfolioLlmTimeout), parse.GetValue(portfolioLlmClientId), parse.GetValue(portfolioLlmClientSecret), parse.GetValue(portfolioLlmTokenUrl), parse.GetValue(portfolioLlmScope))
}, ct));

var root = new RootCommand("Migrator — moderniza aplicações .NET Framework (MVC, Web API, console, Windows Service, bibliotecas) para .NET 10.")
{
    analyze, migrate, portfolio
};
return await root.Parse(args).InvokeAsync();
