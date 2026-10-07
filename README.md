# Migrator — aplicações .NET Framework para a AWS

> Site e relatório de demonstração: **https://digounet.github.io/DotnetMigrator/**

CLI em .NET 10 que lê uma aplicação .NET Framework inteira (`.sln`, `.slnx`, `.csproj` ou pasta) e gera, **ao lado da original**, um repositório pronto para a esteira da plataforma: o código em `app/src`, a infraestrutura em `infra/` (CloudFormation, um arquivo de parâmetros por ambiente), o descritor da esteira, os testes de aceitação e os relatórios. O código original nunca é alterado.

Há dois destinos:

| | `--target framework` (padrão) | `--target net10` |
|---|---|---|
| **Objetivo** | Lift-and-shift: tirar a aplicação do datacenter agora, com o mínimo de mudanças | Modernizar: reescrever para .NET 10 e rodar em container Linux |
| **Código** | Intocado; só os projetos sobem para .NET Framework 4.8.1 | Convertido (csproj SDK, `appsettings.json`, `Program.cs`, controllers com Roslyn, Worker Service...) e compilado |
| **Proteção mínima** | URLs e e-mails fixos viram parâmetros por ambiente; senhas e tokens viram referências ao Secrets Manager | Idem |
| **Hospedagem** | EC2 Windows (IIS, serviço Windows, Agendador) com Auto Scaling, ALB e CodeDeploy | ECS Fargate (web, workers, tarefas agendadas); Lambda só com `--serverless` |
| **Infra** | CloudFormation no layout da plataforma (`infra/service.yml` + `infra/{dev,hom,prod}/parameters.json`) | Igual; Terraform com `--iac terraform` |
| **Saída padrão** | `<nome>.net481` | `<nome>.net10` |

Nos dois casos a ferramenta entrega o **inventário** (o que ainda exige ação manual, com sugestão), os **dados acessados** (banco, tecnologia, tabelas e campos), as **sugestões de modernização** (bibliotecas pagas ou descontinuadas, código que muda de comportamento) e a **arquitetura alvo na AWS** (diagrama, serviços, fases, riscos, custo). Foi pensada para programas de migração em lote: dezenas de aplicações, mesma esteira, mesma arquitetura de referência.

Tipos de projeto: ASP.NET MVC 5, Web API 2, Web Forms (só no destino framework; no .NET 10 vai para `_Legacy/`), console, Windows Service, bibliotecas, testes (MSTest/NUnit/xUnit), WinForms/WPF (só o `.csproj`) e VB.NET (copiado no destino framework; só inventariado no .NET 10).

- [1. Início rápido](#1-início-rápido)
- [2. O que sai de uma migração](#2-o-que-sai-de-uma-migração)
- [3. Destino .NET Framework 4.8.1 (padrão)](#3-destino-net-framework-481-padrão)
- [4. Destino .NET 10](#4-destino-net-10)
- [5. URLs, e-mails e credenciais viram configuração](#5-urls-e-mails-e-credenciais-viram-configuração)
- [6. Dados acessados](#6-dados-acessados)
- [7. Modernização e arquitetura AWS](#7-modernização-e-arquitetura-aws)
- [8. Infraestrutura como código e esteira](#8-infraestrutura-como-código-e-esteira)
- [9. Assistência por LLM](#9-assistência-por-llm)
- [10. Modo portfólio](#10-modo-portfólio)
- [11. Feed NuGet privado](#11-feed-nuget-privado)
- [12. Relatórios e inventário](#12-relatórios-e-inventário)
- [13. Referência da linha de comando](#13-referência-da-linha-de-comando)
- [14. Fluxo recomendado](#14-fluxo-recomendado)
- [15. Decisões de projeto e limitações](#15-decisões-de-projeto-e-limitações)
- [16. Estrutura do código e como estender](#16-estrutura-do-código-e-como-estender)

---

## 1. Início rápido

Requisitos: SDK do .NET 10. Acesso ao nuget.org (ou a um feed privado, veja a [seção 11](#11-feed-nuget-privado)) para a checagem de pacotes e o build de verificação; sem acesso, use `--offline`. O build de verificação do destino framework exige MSBuild no Windows e por isso é feito pelo workflow gerado, não pela ferramenta.

```powershell
dotnet build Migrator.slnx

# só análise: inventário, dados acessados, modernização e arquitetura, sem gravar código
dotnet run --project src/Migrator.Cli -- analyze C:\src\Loja\Loja.sln

# lift-and-shift (padrão): gera C:\src\Loja.net481 no layout da plataforma
dotnet run --project src/Migrator.Cli -- migrate C:\src\Loja\Loja.sln

# modernização: gera C:\src\Loja.net10, compila, testa e sobe as apps para testar /health
dotnet run --project src/Migrator.Cli -- migrate C:\src\Loja\Loja.sln --target net10

# portfólio: todas as soluções de uma pasta, com ranking de esforço e ondas
dotnet run --project src/Migrator.Cli -- portfolio C:\src\aplicacoes --report C:\src\portfolio
```

Como ferramenta global (comando `migrator`):

```powershell
dotnet pack src/Migrator.Cli -o nupkg
dotnet tool install -g Migrator.NetFramework --add-source nupkg
migrator migrate C:\src\Loja\Loja.sln
```

A solução de exemplo `samples/LegacyShop` (MVC 5 + Web API 2 + EF6, Windows Service com Quartz, console que lê pasta de rede e caixa postal via EWS, projeto VB.NET, testes MSTest) serve para experimentar: `migrate samples/LegacyShop/LegacyShop.sln --offline`.

---

## 2. O que sai de uma migração

A saída reproduz o repositório padrão da plataforma, nos dois destinos e com qualquer IaC:

```
<nome>.net481/  (ou .net10)
├── app/src/                    a solução (sln/slnx, projetos, nuget.config, Dockerfiles no .NET 10); working-directory da esteira
├── infra/
│   ├── service.yml             1º projeto publicável; os demais em service-<microservico>.yml
│   ├── lambda-<micro>.yml      só com --serverless
│   ├── data.yml                o que é da aplicação: RDS, S3, filas, FSx, bucket de artefatos, nomes dos segredos
│   ├── dev/ hom/ prod/         parameters*.json ({"Parameters": {...}}): VPC, subnets, tamanhos, tags e os valores por ambiente
│   ├── codedeploy/<micro>/     (framework) appspec.yml + scripts PowerShell do CodeDeploy
│   ├── deploy.sh / deploy.ps1  aws cloudformation deploy na ordem data → serviços → lambdas
│   └── README.md               ordem de execução e checklist
├── tests/testspec-dev.yml      specs dos testes de aceitação (TAAC) da esteira; -hom.yml é idêntico
├── .github/workflows/deploy.yml  framework: MSBuild em runner Windows → zip → CodeDeploy; net10: buildx linux/arm64 → ECR → ECS
├── .iupipes.yml                descritor da esteira (language, build em ./app/src, infra.cloudformation, contas por ambiente, sonar, fortify)
├── .gitattributes, .gitignore, README.md
├── _secrets/<projeto>/         valores reais das credenciais retiradas; fora do git e do Docker
└── _migration-report/          relatórios (seção 12)
```

Contas, sigla, e-mails e os valores dos segredos ficam como placeholders (`PREENCHER`) para serem completados uma vez, no primeiro commit. `--no-infra` desliga `infra/`, `.iupipes.yml`, `tests/` e o workflow; `--output` muda a pasta.

---

## 3. Destino .NET Framework 4.8.1 (padrão)

`migrator migrate App.sln` serve para o caso em que a decisão é sair do datacenter **sem reescrever**.

- **Código intocado.** Cada projeto (C# ou VB.NET, formato antigo ou SDK) é copiado como está; só o `TargetFrameworkVersion` sobe para `v4.8.1` (`net481` em SDK-style) e os marcadores de runtime dos configs (`supportedRuntime`, `httpRuntime`, `compilation targetFramework`) acompanham. `packages.config`, `Global.asax`, Web Forms e `ServiceBase` permanecem; o `.sln` original é copiado para `app/src`.
- **Proteção mínima.** URLs, e-mails e credenciais fixos no código e nos configs saem para configuração e Secrets Manager ([seção 5](#5-urls-e-mails-e-credenciais-viram-configuração)). Nada mais muda.
- **Análise completa.** Perfil, dados acessados, modernização (sem os itens `MOD-CS-*`/`MOD-WIN-*`, que só valem para .NET 10/Linux) e arquitetura rodam sobre o código original e sobre os metadados das DLLs locais (`PRJ-DLL` informativo: o que cada DLL impediria na modernização).
- **Hospedagem EC2 Windows** para todo projeto publicável: IIS para web, serviço Windows, Agendador de Tarefas para consoles. O relatório traz os pré-requisitos do lift-and-shift (health check para o ALB, `machineKey`, sessão InProc, pastas de rede → FSx, Integrated Security → AD, EWS em desligamento) e, na coluna "Alternativas", a hospedagem que o projeto teria após migrar para .NET 10. Serviços: EC2 + Auto Scaling, ALB, RDS, FSx for Windows ou S3 via File Gateway, Managed AD, Secrets Manager, Systems Manager, CloudWatch agent, CodeDeploy, AWS Backup, VPN.
- **Sem build de verificação** (`BUILD-FX-SKIPPED`): exige MSBuild no Windows, e o workflow gerado faz isso no runner `windows-latest`.

Itens do inventário: `PRJ-FX-UPGRADE`, `PRJ-FX-CURRENT`, `PRJ-FX-MODERN`, `CFG-FX-RUNTIME`, `CFG-FX-SECRETS`, `WEB-WEBFORMS-KEPT`, `BUILD-FX-SKIPPED`. A última fase do plano gerado é rodar o Migrator de novo com `--target net10`.

---

## 4. Destino .NET 10

`analyze` e `migrate --target net10` executam o mesmo pipeline; `analyze` roda em memória e só grava os relatórios.

```
 1. Leitura ..................... .sln/.slnx/.csproj/pasta → projetos (WorkspaceLoader, ProjectLoader)
    por projeto:
 2. Classificação dos arquivos .. código, legado, views, estáticos, configuração, cópia
 3. Inicialização ............... Global.asax, App_Start, Startup OWIN → plano para o Program.cs
 4. Configuração ................ web.config/app.config → appsettings*.json
 5. Código C# ................... Roslyn (controllers, Windows Service) + regras de reescrita + regras de detecção
 6. Views Razor ................. bundles, partials, _ViewImports
 7. Pacotes NuGet ............... regras + consulta ao feed
 8. .csproj e Program.cs
    para a solução:
 9. Alinhamento de pacotes ...... versões coerentes entre projetos (evita NU1605)
10. Gravação da saída ........... app/src + raiz do repositório                 (só migrate)
11. Build, testes e smoke ....... dotnet build/test por projeto, GET /health     (só migrate)
12. Relatórios
    em paralelo: perfil da aplicação, dados acessados, modernização, arquitetura AWS, infra
```

**1. Leitura.** `.sln` (linhas `Project(...)`), `.slnx` (XML), um `.csproj` ou uma pasta (`*.csproj` recursivo, ignorando `bin`, `obj`, `packages`, `node_modules` e saídas anteriores). Do `.csproj` saem propriedades, itens com metadados, `Reference`/`ProjectReference`/`COMReference`, imports, targets, build events, `packages.config` e a porta do IIS Express. Tipo do projeto, na ordem: testes (MSTest/NUnit/xUnit ou GUID) → web (GUID ou `web.config` + Global.asax/MVC/Web API/`.aspx`) → desktop (WinForms/WPF) → Windows Service (`Exe` + `ServiceBase`) → console (`Exe`) → biblioteca. Projetos já SDK-style com `netstandard`/`netcoreapp`/`net5+` só têm o `.csproj` atualizado para `net10.0` (`PRJ-MODERN`). VB.NET, F#, `.sqlproj`, Web Sites: inventariados como não migrados (`PRJ-VB`, `SLN-SKIPPED`).

**2. Classificação.** Só os itens do `.csproj` entram (arquivos esquecidos no disco quebrariam o build SDK-style; são listados no inventário).

| Papel | Arquivos | Destino |
|---|---|---|
| Código | `.cs` | transformado (etapa 5) |
| Legado | `Global.asax.cs`, `App_Start/*.cs` com System.Web/OWIN/DI, Startup OWIN, `AreaRegistration`, code-behind, instaladores | `_Legacy/` (fora do build) |
| Web Forms | `.aspx`, `.ascx`, `.master`, `.ashx`, `.asmx`, `.svc` | `_Legacy/` + `WEB-WEBFORMS` com estimativa de reescrita |
| Views | `.cshtml` | transformadas (etapa 6) |
| Estáticos | `Content`, `Scripts`, `fonts`, `Images`, `css`, `js`, `lib`, `favicon.ico`, `robots.txt` | `wwwroot/` (URLs `~/Content/...` continuam válidas) |
| Configuração | `web.config`, `app.config`, `Web.*.config`, `Views/web.config`, `packages.config` | convertidos (etapas 4 e 6) |
| Demais | qualquer outro item | copiado |

Arquivos linkados de fora do projeto são copiados se estiverem dentro da solução; ANSI (Windows-1252) vira UTF-8 sem perder acentos.

**3. Inicialização.** Lido com Roslyn dos arquivos legados: rotas do `RouteConfig` e das áreas (`UrlParameter.Optional` → `{id?}`, constraints, `LowercaseUrls`), template e CORS do `WebApiConfig`, filtros globais do `FilterConfig`, registros de Unity/Ninject → `AddTransient/Scoped/Singleton`, eventos do `Global.asax` (`Application_Error`, `BeginRequest`, `Session_Start`... viram itens com a alternativa) e cada `app.UseXxx()` do OWIN.

**4. Configuração.**

| No config | Resultado |
|---|---|
| `<appSettings>`, `<connectionStrings>` | `"AppSettings"`, `"ConnectionStrings"` (chaves de infraestrutura descartadas; `file=`/`configSource=` incorporados; avisos para EDMX, `\|DataDirectory\|` e providers não SQL Server) |
| `Web.Release.config`, `App.Debug.config`... | `appsettings.Production.json`, `appsettings.Development.json`... |
| seções customizadas, `<applicationSettings>`, `<system.serviceModel><client>`, `<mailSettings>` | JSON (`"ApplicationSettings"`, `"WcfClient:Endpoints"`, `"Smtp"`); em projetos não-web o `App.config` é mantido para `Settings.Default` |
| `<log4net>`, `<nlog>` | `log4net.config` / `nlog.config` |
| Forms Authentication, Windows Auth, `<authorization>`, `<sessionState>`, `<globalization>`, limites de upload, `<customErrors>`, `<httpCookies>` | cookie auth, `AddNegotiate()`, `FallbackPolicy`, `AddSession`, `UseRequestLocalization`, limites do Kestrel/IIS/`FormOptions`, `UseExceptionHandler`, `CookiePolicyOptions` no `Program.cs` |
| `<system.webServer>` (rewrite, headers, mime) | `web.config` mínimo para o IIS |
| `httpModules`/`handlers`, `machineKey`, Membership, `<location>`, WIF | itens do inventário com a alternativa |
| `<runtime>`, `<system.codedom>`, `<startup>` | descartados |

**5. Código C#.** Para cada arquivo, nesta ordem: (a) `LiteralExternalizer` ([seção 5](#5-urls-e-mails-e-credenciais-viram-configuração)); (b) **Roslyn**: controllers Web API, mesmo herdando de base própria, ganham `ControllerBase`, `[ApiController]`, `[Route("api/[controller]")]` do template do `WebApiConfig`, verbo explícito por convenção de nome (`GetX` → `[HttpGet]`), `{id}` quando há parâmetro `id`, `HttpResponseMessage` → `IActionResult`; `HttpContext.Current` → `HttpContext` em controllers; `[Area("X")]` por pasta; classes `: ServiceBase` viram `: BackgroundService` (`OnStart` → `ExecuteAsync`, `OnStop` → `StopAsync`) com `Program.cs` de generic host, `AddWindowsService()` e `AddHostedService<T>()`; (c) **regras de reescrita**: usings `System.Web.*` → `Microsoft.AspNetCore.*`, `IHttpActionResult`/`HttpNotFound()`/`[FromUri]`/`[RoutePrefix]`/`[ResponseType]`/`HttpPostedFileBase`/`MvcHtmlString`/`[OutputCache]`/`Request.IsAjaxRequest()` e afins para os equivalentes do ASP.NET Core, `ConfigurationManager.AppSettings["X"]` → `configuration["AppSettings:X"]`, `System.Data.SqlClient` → `Microsoft.Data.SqlClient`, `XmlConfigurator.Configure()` apontando para o `log4net.config`; (d) **injeção de `IConfiguration`** sem intervenção na classe do `Main` de consoles (campo estático com `ConfigurationBuilder`: appsettings + ambiente + variáveis de ambiente) e nos `BackgroundService`s convertidos (construtor); nas demais classes fica o item `CFG001` e o erro de build aponta o lugar; (e) **regras de detecção** sobre o código já transformado (`Data/CodeRules.cs`): `HttpContext.Current` fora de controllers, `Server.MapPath`, `Session[...]`, `FormsAuthentication`, filtros customizados, `IHttpModule`, child actions, `BinaryFormatter`, `Thread.Abort`, Remoting, hospedagem WCF, `WebClient`, `SmtpClient`, `DbContext("name=...")`...

**6. Views Razor.** `@Scripts.Render`/`@Styles.Render` expandidos a partir do `BundleConfig.cs` (com `asp-append-version`, resolvendo `{version}`, `*` e `IncludeDirectory`); `@Html.Partial` → `@await Html.PartialAsync`; `Json.Encode` → `JsonSerializer.Serialize`; `_ViewImports.cshtml` a partir dos `Views/web.config`. Itens manuais: `@helper`, `@Ajax.*`, `Html.Action`, `WebGrid`, `@inherits WebViewPage`.

**7. Pacotes NuGet.** Com regra em `Data/PackageRules.cs`: `Remove` (já faz parte do .NET, conteúdo client-side, `.pt-br`), `Replace` (ex.: `Microsoft.Owin.Security.Jwt` → `JwtBearer`), `Manual` (item bloqueante: Unity, Identity 2, SignalR clássico, ELMAH) ou `Keep` com política de versão. Sem regra: consulta ao feed (pastas `lib/<tfm>`): compatível → mantém na última da mesma major; só .NET Framework → sobe (atenção); nenhuma compatível → mantém + bloqueante (NU1701); inexistente → "feed privado?". Políticas: "linha 10.0" para Microsoft.Extensions/EF Core/ASP.NET Core, "última da major N" (EF6 → 6.x) e tetos de licença (AutoMapper < 15, MediatR < 13, FluentAssertions < 8, EPPlus < 5, MassTransit < 9). Pacotes entram conforme o código usa (`NewtonsoftJson` em Web API, `Microsoft.Data.SqlClient`, `System.ServiceModel.*`, `System.Drawing.Common`, `Microsoft.NET.Test.Sdk`...); referências do GAC viram pacote só se o namespace é usado. `--offline` mantém as versões e marca como não verificadas.

**DLLs locais** (`Reference` com `HintPath`, fora de pacotes) são copiadas e têm os metadados lidos por `Analysis/AssemblyInspector`, sem executar nada: framework de compilação, cada tipo e membro referenciado e cada P/Invoke, classificados em **removido do .NET 10** (`System.Web`, Remoting, COM+, WCF servidor, LINQ to SQL, `AppDomain.CreateDomain`, `Thread.Abort`...), **só Windows** (Registro, MSMQ, System.Drawing, Event Log, PerformanceCounter, DirectoryServices, WMI, Office Interop, Crystal, `WindowsIdentity`, P/Invoke em kernel32/user32/advapi32/winspool...), **risco** (`BinaryFormatter`, `TransactionScope`, `Encoding.GetEncoding`, `Process.Start`) e **pacote** (`System.Configuration`, cliente WCF, `System.Runtime.Caching`...). DLLs que a DLL referencia e estão na mesma pasta são seguidas, e a cadeia aparece no item ("via Legacy.Core.dll → Legacy.Util.dll"). O resultado vai para três lugares: o item `PRJ-DLL` (bloqueante com API removida, atenção com API só Windows ou de risco, informativo quando limpa), o perfil de hospedagem (uma DLL que usa Registro torna o projeto dependente de Windows, com evidência "DLL X" nos itens `MOD-WIN-*`) e os pacotes do projeto migrado (`System.Configuration.ConfigurationManager`, `System.Diagnostics.EventLog`... entram sozinhos). Nos destinos que mantêm o código em .NET Framework (lift-and-shift e projetos VB.NET) o item é informativo: nada muda agora, mas é o que impede a DLL de rodar em .NET 10/Linux depois. O sample traz dois casos em `samples/LegacyShop/lib/`: `Legacy.Barcode.dll` (usa `System.Web`: bloqueante) e `Legacy.Impressao.dll` (Registro, winspool.drv, Event Log, BinaryFormatter: hospedagem Windows).

**8. `.csproj` e `Program.cs`.** SDK `Web`/`Razor`/`Sdk` com `net10.0` (`-windows` para desktop), `Nullable` e `ImplicitUsings` desligados, `AssemblyInfo.cs` preservado, `PlatformTarget`/`DefineConstants`/assinatura/ícone mantidos, `Compile Remove="_Legacy\**"`, `FrameworkReference` para bibliotecas que usavam MVC/Http, targets e build events convertidos. O `Program.cs` web junta o plano da etapa 3 com as dicas da etapa 4, mais `/health`, `UseForwardedHeaders` e cultura padrão; o que não pôde ser convertido vira `// TODO Migrator`. `launchSettings.json` mantém a porta do IIS Express.

**9. Alinhamento.** A solução é percorrida em ordem de dependência elevando versões diretas quando um pacote adicionado ou um projeto referenciado exige mais (evita NU1605).

**10. Gravação.** Pasta de saída fora da origem; se já existir, só é substituída com `--force` e se tiver o marcador `.migrator-output`. Um `.slnx` com os projetos migrados, `nuget.config`, `.editorconfig` e `Directory.Build.*` vão para `app/src`.

**11. Build, testes e smoke.** `dotnet build` por projeto em ordem topológica; dependência que falhou bloqueia os dependentes (`BUILD-BLOCKED`). Erros de compilação, restore (NU1101, NU1605), compatibilidade (NU1701, CA1416), APIs obsoletas (SYSLIB*) e vulnerabilidades (NU1902–NU1904) viram itens com arquivo, linha e dica de `Data/BuildHints.cs`. O compilador só mostra erros de corpo de método depois que os de declaração somem; as regras de detecção da etapa 5 antecipam as camadas seguintes. Depois do build: projetos de teste rodam com `dotnet test` (`TEST-RUN`/`TEST-FAILED`), apps web sobem em porta aleatória e recebem `GET /health` (`SMOKE-OK`/`SMOKE-FAILED`), e com `--verify-docker` as imagens são construídas (`DOCKER-*`). `--no-build`, `--no-tests` e `--no-smoke` desligam cada etapa; `--build-timeout` limita o build.

Exemplo de controller Web API 2 antes e depois:

```csharp
// antes
public class ProdutosApiController : ApiController
{
    public IEnumerable<ProdutoEntidade> GetPorCategoria([FromUri] string categoria) { ... }
    public HttpResponseMessage Post(ProdutoEntidade produto)
    {
        if (!ModelState.IsValid) return Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState);
        return Request.CreateResponse(HttpStatusCode.Created, produto);
    }
}

// depois
[Route("api/[controller]")]
[ApiController]
public class ProdutosApiController : ControllerBase
{
    [HttpGet]
    public IEnumerable<ProdutoEntidade> GetPorCategoria([FromQuery] string categoria) { ... }
    [HttpPost]
    public IActionResult Post(ProdutoEntidade produto)
    {
        if (!ModelState.IsValid) return StatusCode((int)HttpStatusCode.BadRequest, ModelState);
        return StatusCode((int)HttpStatusCode.Created, produto);
    }
}
```

No sample, `LegacyShop.Worker` e `LegacyShop.Importador` compilam sem alteração manual; os erros restantes ficam em `LegacyShop.Core` de propósito, para exercitar o laço de correção com LLM.

---

## 5. URLs, e-mails e credenciais viram configuração

Em qualquer destino, antes de qualquer outra reescrita, `Migration/LiteralExternalizer` troca literais C# por leitura de configuração:

| Literal | Vira | Onde fica o valor |
|---|---|---|
| `"https://erp.exemplo.com.br/api"` | `ConfigurationManager.AppSettings["Urls:ErpProtocoloUrl"]` (no .NET 10, `configuration["AppSettings:Urls:ErpProtocoloUrl"]`) | `web.config`/`appsettings.json` **e** um parâmetro da infra com um valor por ambiente |
| `"suporte@exemplo.com.br"` | `AppSettings["Emails:EmailSuporte"]` | idem |
| `const string Token = "erp-9f3b..."`, connection string com `Password=`, chaves `AKIA...` | `AppSettings["Credenciais:Token"]` com marcador `<secret: nome>` no config | valor real em `_secrets/<projeto>/` (fora do git); nome criado no Secrets Manager por `data.yml`; referência na infra |

A chave vem do identificador (`ErpProtocoloUrl`) ou do host/parte local do e-mail; `const` vira `static readonly`; o `.csproj` ganha `System.Configuration` quando falta. Literais em atributos, `case`, valores padrão de parâmetro e strings interpoladas/verbatim não são reescritos e aparecem em `CS-CONFIG-SKIPPED`. Itens: `CS-CONFIG-EXTERNALIZED`, `CS-SECRET-EXTERNALIZED`.

Os `appSettings` com URL/e-mail também viram parâmetros, com o valor por ambiente que os `Web.Debug.config`/`Web.Release.config` já declaravam. Credenciais em qualquer seção do config (appSettings, connection strings, SMTP, `<identity>`, `sessionState`, `machineKey`, seções customizadas) saem para `_secrets/<projeto>/` (`appsettings.Secrets.json`, template, `create-secrets.sh`, bloco da task definition, user-secrets); `--keep-secrets` desliga.

Na infra: o EC2 recebe os parâmetros pelo Parameter Store (`/<feature>/<env>/...`, gravado no config pelo `after-install.ps1` do CodeDeploy) e os segredos pelo Secrets Manager; ECS e Lambda recebem variáveis de ambiente `AppSettings__Secao__Chave` e `secrets` da task definition. **Segredos nunca passam por template ou arquivo de parâmetros**: `data.yml` cria os nomes com `SecretString: PREENCHER` e `_secrets/<projeto>/create-secrets.sh` coloca os valores.

---

## 6. Dados acessados

Em qualquer modo o relatório traz a seção **"Dados acessados"**: para cada banco, a tecnologia (SQL Server, Oracle, MySQL...) e a lista de tabelas, views e procedures que o código toca, com os campos, as operações (SELECT/INSERT/UPDATE/DELETE/EF), a forma de acesso, os projetos e o arquivo:linha. É o que o negócio e o DBA precisam para decidir o que vai para o RDS. Também sai em `data-access.csv`, na aba do Excel e no JSON; o componente RDS da arquitetura lista as tabelas.

| Fonte (`Analysis/DataAccessAnalyzer`, análise estática) | O que extrai |
|---|---|
| SQL em literais C#/VB (inclusive concatenações, verbatim e interpoladas) e arquivos `.sql` | tabelas de `FROM`/`JOIN`/`INSERT`/`UPDATE`/`DELETE`/`MERGE`/`TRUNCATE`, procedures de `EXEC`; colunas do `SELECT`, `INSERT (...)`, `SET`, `WHERE`/`ON`/`ORDER BY`/`GROUP BY`; aliases resolvidos; CTEs, temporárias e `sys.*` ignorados; `Banco.dbo.Tabela` fixa o banco |
| ADO.NET | `reader["Coluna"]`, `GetOrdinal`, `leitor("Coluna")` (VB) ligados ao comando mais próximo; `CommandType.StoredProcedure` + parâmetros |
| Dapper | `Query/Execute("sql")`, `commandType: StoredProcedure` |
| EF6 / EF Core | `DbSet<T>` → tabela (`ToTable`/`[Table]` ou convenção); colunas = propriedades públicas da entidade, com `HasColumnName`/`[Column]`, sem `[NotMapped]`/navegações; `SaveChanges` marca escrita |
| EDMX | `EntitySet`/`EntityType` do modelo de armazenamento e `Function` |

O banco é resolvido nesta ordem: nome de três partes → connection string citada no arquivo (`ConnectionStrings["X"]`, `name=X`) → banco único conhecido pelo projeto, pelos projetos que o hospedam ou pelas dependências. Ambiguidade fica como "não identificado (candidatos: ...)", em amarelo no Excel. A seção é documental: não entra no percentual de automação nem no código de saída.

---

## 7. Modernização e arquitetura AWS

A ferramenta monta um **perfil** de cada projeto a partir do código original, dos pacotes, das referências e dos configs: banco (e Integrated Security), arquivos locais ou pastas de rede, MSMQ/RabbitMQ, SMTP, sessão em memória, estado estático, timers/agendadores, autenticação, componentes Windows (COM, Registro, System.Drawing, Event Log, WMI, Crystal...), segredos em texto claro, caixas postais (EWS, IMAP, Graph), planilhas, FTP e hosts internos. Bibliotecas são fundidas nos projetos que as referenciam.

### Modernização

Sugestões que não bloqueiam a compilação, separadas do inventário (não entram no percentual nem no código de saída). Cada item tem tipo, impacto, esforço, evidência (pacote ou `arquivo:linha`) e o serviço AWS relacionado.

| Tipo | Exemplos |
|---|---|
| **Licença** | AutoMapper 15+ → Mapperly; MediatR 13+ → Mediator; FluentAssertions 8+ → AwesomeAssertions; EPPlus 5+ → ClosedXML; MassTransit 9+ → AWS.Messaging; iTextSharp → QuestPDF; Telerik/DevExpress |
| **Descontinuado** | Topshelf, Common.Logging, DotNetZip, Rotativa, Crystal Reports, ReportViewer, IdentityServer4, Enterprise Library, **EWS** (bloqueio no Exchange Online a partir de outubro de 2026: Graph ou SES de entrada) |
| **Modernização** (.NET 10/Linux; omitida no destino framework) | `Encoding.GetEncoding(1252)` sem provider, `Encoding.Default`, parse sem cultura, `string.GetHashCode()` persistido, `new HttpClient()` por chamada, `.Result`/`.Wait()`, `async void`, EF6 → EF Core, Newtonsoft → System.Text.Json |
| **Cloud** | MSMQ → SQS; UNC → S3/EFS/FSx; SMTP → SES; sessão InProc → ElastiCache; `TransactionScope` sem MSDTC; `DateTime.Now` em UTC; caminhos com `\`; Integrated Security no RDS; hosts on-premises (VPN); Azure Storage/Service Bus/Key Vault → S3/SQS/Secrets Manager |
| **Segurança** | credenciais em qualquer seção do config e literais no C# → Secrets Manager; MD5/SHA1/DES; `Random` para tokens; SQL por concatenação; catch vazio |

### Hospedagem

| Projeto | `--target framework` | `--target net10` |
|---|---|---|
| Web (MVC/Web API/Web Forms) | **EC2 Windows com IIS** atrás do ALB compartilhado | **ECS Fargate (Linux) + ALB**; dependência dura de Windows (COM, Registro, Office Interop, Crystal, WMI, OLE DB ACE/Jet) → containers Windows; IIS em código → EC2 Windows |
| Windows Service / console com timer ou Quartz/Hangfire | **EC2 Windows** (serviço ou Agendador de Tarefas) | **tarefa ECS Fargate agendada** pelo EventBridge Scheduler, `Main()` intacto |
| Automação orientada a evento (pasta, caixa postal, planilhas, FTP) | EC2 Windows | tarefa ECS agendada; com `--serverless`, **Lambda** com o gatilho certo (S3 Event Notifications, SES de entrada → S3, SQS) e `Function.cs` gerado |
| Consumidor de fila (MSMQ, RabbitMQ, MassTransit) | EC2 Windows | **worker ECS Fargate** consumindo SQS, escalado pela fila |
| Biblioteca / testes / desktop | não publicável | não publicável (desktop: fora da AWS ou AppStream) |

Dependências Windows "moles" (System.Drawing, Event Log, PerformanceCounter, ServiceBase, MSMQ, Windows Auth, UNC) não forçam containers Windows: a recomendação continua Linux e lista o que substituir (`MOD-WIN-*`). As dependências vêm do código, dos pacotes, dos configs **e das DLLs locais** (uma biblioteca interna compilada para .NET Framework que usa Registro ou P/Invoke conta como dependência dura do projeto que a referencia). Cada recomendação traz justificativa, pré-requisitos e alternativas; no destino framework a alternativa é a hospedagem após a modernização.

Na solução como um todo saem a lista de **serviços** (obrigatórios e recomendados, com o que cada um substitui e quem usa), um **diagrama Mermaid**, um **plano em fases**, **riscos** e **notas de custo** (containers Windows ≈ 2x, licença do SQL Server no RDS, NAT Gateway). No .NET 10 cada projeto publicável ganha `Dockerfile` (multi-stage, porta 8080, usuário não-root, `TZ`/`LANG` quando a aplicação depende de fuso/cultura). `--cloud none` desliga arquitetura, Dockerfiles e sugestões do tipo Cloud.

---

## 8. Infraestrutura como código e esteira

No `migrate` a arquitetura vira artefatos de deploy ([layout na seção 2](#2-o-que-sai-de-uma-migração)). `--iac cloudformation` é o padrão nos dois destinos; `--iac terraform` troca o gerador; `--no-infra` desliga.

### CloudFormation (`Cloud/CloudFormationGenerator`)

Reproduz o repositório padrão da plataforma. Infraestrutura compartilhada (VPC, subnets, roles, listener do balanceador, cluster ECS) entra como parâmetro, nunca é criada.

| Arquivo | Conteúdo |
|---|---|
| `infra/service.yml` (framework) | launch template (user data instala IIS, .NET 4.8.1, agente CodeDeploy e CloudWatch), Auto Scaling group, target group + regra no ALB compartilhado, aplicação e grupo do CodeDeploy, parâmetros no SSM Parameter Store, log group, alarmes |
| `infra/service.yml` (net10) | o `service.yml` da plataforma: parâmetros de tags (Squad, Finalidade, Sigla, Versao, TechTeamEmail, OwnerTeamEmail, RepoUrl, GithubRepoId, NomeAplicacao), roles e NLB via SSM (`/Itau/Parameters/Common/*`), listener TCP por serviço no NLB compartilhado, task definition Fargate ARM64 com sidecars `datadog-agent` e `log_router` (FireLens; `EnableDatadog=false` volta ao awslogs), health check, tags `iu:finops:alocacao:*`, ScalableTarget com `/Shared/Role/ecs-scaling-role`, imagem `${DevToolsAccount}.dkr.ecr...:<feature>-<micro>-<env>` |
| `infra/lambda-<micro>.yml` | só com `--serverless`: função, DLQ, gatilhos (fila de eventos do S3, agendamento da caixa postal, SES opcional) |
| `infra/data.yml` | RDS, bucket S3 (+ fila de eventos), filas dos workers, FSx for Windows (framework com pastas UNC), bucket de artefatos do CodeDeploy, `AWS::SecretsManager::Secret` por credencial (`PREENCHER`); exportado para os serviços |
| `infra/{dev,hom,prod}/parameters*.json` | `{"Parameters": {...}}` por template e ambiente: VPC, subnets, tamanhos, tags e os parâmetros de aplicação (URLs/e-mails) com o valor de cada ambiente (dev ← `Web.Debug.config`, hom ← Staging/Homolog, prod ← `Web.Release.config`) |
| `infra/codedeploy/<micro>/` | (framework) `appspec.yml` + `before-install`, `after-install` (lê Parameter Store e Secrets Manager para o config), `application-start`, `validate-service` em PowerShell |
| `infra/deploy.sh`, `deploy.ps1`, `README.md` | `aws cloudformation deploy` na ordem data → serviços → lambdas com a pasta do ambiente; checklist |
| `.iupipes.yml` | language, build (working-directory `./app/src`, docker-platform linux/arm64), unit-tests, publish, `infra.cloudformation` (template `service.yml`, working-directory `infra`), contas por ambiente, sonar, fortify; placeholders para sigla, contas e e-mails |
| `tests/testspec-dev.yml`, `-hom.yml` | specs TAAC idênticos (buildspec 0.2 com `echo`; o comentário diz como automatizar) |
| `.github/workflows/deploy.yml` | framework: MSBuild em `windows-latest` → zip → CodeDeploy; net10: buildx linux/arm64 → ECR → `update-service` |

Convenções: `FeatureName` (solução, só letras) e `MicroServiceName` (projeto sem o prefixo da solução, só letras) com `AllowedPattern "[a-z]*"`, `DevToolsAccount`, `Projeto`/`Negocio`, `Environment` (dev/hom/prod), bloco "NÃO ALTERE" da esteira, alarmes no tópico `{{resolve:ssm:/org/member/workload_local_sns_arn:1}}`. Os templates do sample passam limpos no `cfn-lint` nos dois destinos; revalide após mudar o gerador.

### Terraform (`--iac terraform`)

`infra/terraform/` como módulo raiz: versions, variables (`terraform.tfvars.example`; os parâmetros de aplicação em `app_settings`), network (VPC + endpoints), iam, ecs (cluster, task definitions, serviços web, tarefas agendadas, workers por fila), alb, lambda, data (RDS), storage (S3, SQS), cache, observability (SNS + alarmes), outputs. Segredos via `data "aws_secretsmanager_secret"`; tasks em subnets privadas; roles separadas; circuit breaker com rollback; alarmes de 5xx e CPU. `.iupipes.yml` e os specs TAAC são gerados do mesmo jeito. O módulo do sample passa em `terraform fmt -check`, `init` e `validate` (provider AWS 6.x). Projetos VB e EC2 Windows não geram recursos; o README da infra diz por quê.

---

## 9. Assistência por LLM

Tudo é determinístico e **funciona sem LLM** (`--llm none`, o padrão). Com `--llm <provedor>` entram quatro etapas:

| Etapa | Quando | O que faz | Salvaguardas |
|---|---|---|---|
| **Correção do build** | `migrate --target net10` com build que falhou | Para cada arquivo com erro envia o arquivo, os erros e as dicas; grava a correção e recompila, até `--llm-rounds` rodadas | Se os erros do arquivo não diminuírem, reverte e dá uma segunda tentativa com os erros da própria proposta; arquivo que volta a ter erro na rodada seguinte também é revertido. Originais em `_migration-report/llm/*.before`. Itens `LLM-FIX*`, `LLM-SUMMARY` |
| **Rascunhos** | `migrate --target net10` | `IHttpModule`/`IHttpHandler`, `HttpApplication`, filtros do System.Web, `ServiceHost` → versão ASP.NET Core salva como `<Nome>.Migrator.cs.txt` | Nunca entra no build; item `LLM-DRAFT` |
| **Triagem** | `analyze` e `migrate` | Para itens ambíguos (arquivo temporário ou persistente? cache ou estado? job idempotente?) pede classificação em JSON | Só rebaixa impacto Alto → Médio com confiança ≥ 0,7; nunca remove itens |
| **Leitura do arquiteto** | `analyze` e `migrate` | Resumo executivo, decisões por projeto, riscos e ordem de trabalho a partir do dossiê estruturado | As tabelas por regra continuam ao lado |

Em falha de infraestrutura a ferramenta registra um único `LLM-UNAVAILABLE`, para de chamar o modelo e termina normalmente. Respostas ficam em cache em `~/.migrator/llm-cache` (chave = provedor + modelo + prompts); `--llm-no-cache` desliga. `--llm-timeout` limita cada chamada (padrão 6 min).

### Corporativa, via API (`--llm api`)

Para a LLM do banco, atrás de um gateway HTTP com OAuth2 *client credentials*, o scaffold está em `Llm/CorporateApiAssistant.cs`. A URL do chat e o endpoint de token são constantes no código (`DefaultEndpoint`, `DefaultTokenUrl`: troque uma vez pelos do gateway); o modelo é opcional (`DefaultModel`, `--llm-model` ou `MIGRATOR_LLM_MODEL`); em tempo de execução só `client_id` e `client_secret` são obrigatórios:

```powershell
$env:MIGRATOR_LLM_CLIENT_ID = "..."; $env:MIGRATOR_LLM_CLIENT_SECRET = "..."
migrator migrate C:\src\Loja\Loja.sln --target net10 --llm api --llm-model gpt-4o
```

O token é obtido uma vez (form `grant_type=client_credentials`, credenciais no corpo; `ClientCredentialsInBody = false` troca para Basic Auth; `--llm-scope` acrescenta o escopo) e reaproveitado até expirar; sem endpoint de token, o `client_secret` vai como Bearer fixo. O corpo é *chat completions* compatível com OpenAI (`messages` system/user, `temperature 0`) e a resposta é lida nas formas comuns (`choices[0].message.content`, `content[0].text`, `output.message.content[0].text`, `text`/`response`/`result`). Se o gateway tiver outro contrato, os dois pontos a ajustar são `BuildRequest` e `ExtractText`; headers extras entram em `CorporateApiSettings.ExtraHeaders`. `--llm-endpoint` e `--llm-token-url` só sobrepõem as constantes para testes.

### Local, com Ollama (`--llm ollama`)

```powershell
ollama serve
ollama pull qwen2.5-coder:7b          # o padrão é qwen2.5-coder:3b, mais leve e menos preciso
migrator migrate C:\src\Loja\Loja.sln --target net10 --llm ollama --llm-model qwen2.5-coder:7b --llm-rounds 5
```

Endpoint padrão `http://localhost:11434` (`--llm-endpoint`); temperatura 0 e seed fixa.

### Outro provedor (SDK da empresa, Bedrock, OpenAI, Gemini)

Implemente `ILlmAssistant` (`Name` + `CompleteAsync(systemMessage, userMessage, ct)` devolvendo texto) sobre o SDK, lançando exceção em falha de infraestrutura, respeitando o `CancellationToken` e sem retry interno. Depois, ou acrescente um `case` em `LlmAssistantFactory.Create` e o nome em `Providers` (vira `--llm <nome>`), ou passe a instância a `new MigrationEngine(assistant)` ao hospedar o `Migrator.Core` num programa seu. `LlmAssistantFactory.Wrap` aplica o cache; os prompts de sistema são constantes públicas em `LlmPrompts`, úteis para rotear a narrativa a um modelo mais barato. Teste com assistentes roteirizados (`tests/Migrator.Tests/LlmTests.cs`, `ScriptedAssistant`) e com o sample.

### Que modelo usar

O que a ferramenta pede é **editar C# com precisão** (só os erros listados, sem inventar dependências). Modelos pequenos erram justamente aí.

| Uso | Recomendado | Aceitável | Evite |
|---|---|---|---|
| Correção de build e rascunhos | Claude Sonnet/Opus, GPT da linha principal, Gemini Pro | "mini"/"flash" das mesmas famílias, Nova Pro, Qwen2.5-Coder 14B+ local | < 7B, Nova Micro/Lite, modelos de chat genéricos |
| Leitura do arquiteto e triagem | qualquer um acima; "mini"/"flash" bastam | Nova Lite/Pro | — |
| Desenvolvimento local | `qwen2.5-coder:7b` | `qwen2.5-coder:3b` | `llama3.2:3b` e similares |

Requisitos: contexto ≥ 128k tokens, temperatura 0, timeout compatível, endpoint sem retenção (o código-fonte inteiro dos arquivos com erro vai no prompt). Uma aplicação típica gera de 5 a 40 chamadas de 3k a 15k tokens.

Medido em `samples/LegacyShop` com modelos locais num Mac: sem LLM, 6 erros de compilação em 4 arquivos; `qwen2.5-coder:3b`, 3 erros em 2 arquivos (~7 min); `qwen2.5-coder:7b`, 2 erros em 1 arquivo (~2 min), 3 rascunhos e resumo executivo. O erro restante (`HttpContext.Current` numa biblioteca sem ASP.NET Core) é um caso em que o modelo devolveu o arquivo sem mudanças, corretamente, e a ferramenta registrou `LLM-FIX-FAILED` em vez de fingir correção.

---

## 10. Modo portfólio

`portfolio <pasta>` analisa todas as soluções de uma pasta (uma aplicação por `.sln`/`.slnx`; pastas só com projetos também contam; saídas do Migrator são ignoradas), roda um `analyze` completo em `apps/<nome>/` e consolida:

- **Ranking por esforço**: pontuação de referência (bloqueantes ×3, atenção ×1, modernização de impacto alto ×2, +10 se exige Windows, +5 por arquivo Web Forms, +8 por projeto VB.NET, +3 por projeto web) e faixas Baixo/Médio/Alto. Serve para ordenar, não para estimar horas.
- **Gaps mais frequentes**: em quantas aplicações cada regra aparece ("EWS em 14 aplicações"). Define o que vale resolver uma vez e replicar.
- **Infraestrutura compartilhada**: bancos e hosts internos usados por mais de uma aplicação; o cutover do RDS precisa ser coordenado.
- **Ondas sugeridas**: três ondas por esforço acumulado, quick wins primeiro, mantendo juntas as aplicações que compartilham banco.
- **Totais** de hospedagem e de serviços AWS obrigatórios.
- **Comparação com baseline** (`--baseline portfolio.json` de uma execução anterior): bloqueantes, atenção, impacto alto e esforço antes → depois por aplicação.

```powershell
migrator portfolio C:\src\aplicacoes --report C:\src\portfolio-2026-10
migrator portfolio C:\src\aplicacoes --report C:\src\portfolio-2026-11 --baseline C:\src\portfolio-2026-10\portfolio.json
```

Saídas: `portfolio-report.html`, `portfolio-report.md`, `portfolio.xlsx` (Aplicações, Gaps, Compartilhado, Ondas, Baseline) e `portfolio.json`. `--target`, `--serverless`, `--cloud` e `--llm` valem para todas as aplicações.

---

## 11. Feed NuGet privado

Em redes corporativas o nuget.org é bloqueado e os pacotes vêm de um Artifactory/Nexus. A ferramenta trata isso em três pontos:

1. **`--nuget-config <arquivo>`** (ou `MIGRATOR_NUGET_CONFIG`): copiado para `app/src/nuget.config`, então restore, Docker e workflow usam o mesmo feed. Sem nenhum (nem na raiz da solução de origem), o `migrate` interativo pergunta o caminho; em CI não pergunta. `--nuget-source <url do index.json>` gera um `nuget.config` mínimo.
2. **Compatibilidade pelo mesmo feed**: a consulta de versões usa a primeira fonte v3 do `nuget.config` (`packageSourceCredentials` em texto claro ou `%VARIAVEL%`). Se o feed não responder, a compatibilidade fica "não verificada".
3. **Sondagem antes do build**: um `GET` no service index com 12 s de limite; se falhar, build, testes e smoke são pulados com `BUILD-NUGET-UNREACHABLE` em vez de meia hora de timeouts.

Prefira `%ARTIFACTORY_TOKEN%` a senhas em texto claro; senhas DPAPI só funcionam no Windows e não são lidas. Atrás de proxy, defina `HTTPS_PROXY`.

---

## 12. Relatórios e inventário

Em `_migration-report/` (ou `--report`):

| Arquivo | Uso |
|---|---|
| `migration-report.html` | navegável: resumo, projetos, dados acessados, arquitetura, modernização, filtros por severidade e busca, erros de build por código |
| `migration-report.md` | para PR ou wiki, com o diagrama Mermaid |
| `inventory.xlsx` | abas Resumo, Inventário, Dados acessados, Modernização e Arquitetura AWS |
| `inventory.csv`, `modernization.csv`, `data-access.csv` | integração com outras ferramentas (UTF-8 com BOM) |
| `migration-result.json` | resultado completo (inventário, modernização, arquitetura, hospedagem, bancos, hosts, dados acessados, destino e IaC) |
| `build-verification.log` | saída completa do build (net10) |

**Lendo o inventário.** Severidade: **Bloqueante** impede compilar ou funcionar; **Atenção** compila mas pode mudar de comportamento; **Informativo** é registro. **Automático = Sim** significa que a ferramenta já resolveu (fica para auditoria). Regra é um identificador estável para filtrar (`PKG-` pacotes, `CS-`/`WEB`/`NET` código, `VW` views, `CFG-` configuração, `STARTUP-`, `PRJ-` projeto, `BUILD-` e códigos do compilador, `LLM-`, `TEST-`/`SMOKE-`/`DOCKER-`). **% automatizado** = itens automáticos ÷ (automáticos + pendentes), sem os erros de build.

**Código de saída**: `0` nenhum bloqueante e build OK; `2` há bloqueantes ou erro de build; `1` erro de uso; `130` cancelado. Em pipelines, o `analyze` serve como portão.

---

## 13. Referência da linha de comando

```
migrator analyze   <entrada> [--report] [--offline] [--nuget-config] [--nuget-source] [--cloud] [--target] [--serverless] [--llm ...]
migrator migrate   <entrada> [--output] [--report] [--offline] [--nuget-config] [--nuget-source] [--cloud] [--target] [--iac] [--serverless]
                             [--force] [--no-build] [--build-timeout] [--no-tests] [--no-smoke] [--verify-docker] [--keep-secrets] [--no-infra] [--llm ...]
migrator portfolio <pasta>   [--report] [--offline] [--nuget-config] [--nuget-source] [--cloud] [--target] [--serverless] [--baseline] [--llm ...]
```

**Entrada e saída**

| Opção | Comandos | Descrição |
|---|---|---|
| `<entrada>` / `<pasta>` | todos | `.sln`, `.slnx`, `.csproj` ou pasta com projetos; no `portfolio`, a pasta que contém as aplicações |
| `--output`, `-o` | migrate | Pasta de saída. Padrão: `<pasta-pai>\<nome>.net481` (`.net10` com `--target net10`). Não pode ficar dentro da origem |
| `--report`, `-r` | todos | Pasta dos relatórios. Padrão: `<saída>\_migration-report` no migrate; `<pasta-pai>\<nome>.migration-report` no analyze |
| `--force` | migrate | Substitui uma saída anterior. Só apaga pastas com o marcador `.migrator-output` |

**Destino e nuvem**

| Opção | Comandos | Descrição |
|---|---|---|
| `--target` | todos | `framework` (padrão: lift-and-shift, código intocado em .NET Framework 4.8.1, EC2 Windows) ou `net10` (reescreve o código, ECS Fargate) |
| `--cloud` | todos | `aws` (padrão) ou `none`: desliga arquitetura, Dockerfiles e sugestões do tipo Cloud |
| `--serverless` | todos | Recomenda Lambda (e gera handler/gatilhos) para automações orientadas a evento. Sem a opção elas ficam em tarefa ECS agendada com o `Main()` intacto |
| `--iac` | migrate | `cloudformation` (padrão nos dois destinos; layout da plataforma) ou `terraform` |
| `--no-infra` | migrate | Não gera `infra/`, `.iupipes.yml`, `tests/` nem o workflow |

**Pacotes e build**

| Opção | Comandos | Descrição |
|---|---|---|
| `--offline` | todos | Não consulta o feed NuGet; versões mantidas e marcadas como não verificadas |
| `--nuget-config` | todos | `nuget.config` do feed privado; copiado para `app/src` e usado na compatibilidade e no restore. Alternativa: `MIGRATOR_NUGET_CONFIG` |
| `--nuget-source` | todos | URL do service index v3 do feed; gera um `nuget.config` mínimo se não houver |
| `--no-build` | migrate | Pula o build de verificação (net10; no framework ele já não roda) |
| `--build-timeout` | migrate | Tempo máximo do build de verificação, em minutos (padrão 30) |
| `--no-tests` | migrate | Não executa os projetos de teste migrados |
| `--no-smoke` | migrate | Não sobe as apps web para testar `/health` |
| `--verify-docker` | migrate | Constrói as imagens dos Dockerfiles gerados com o Docker local (lento) |
| `--keep-secrets` | migrate | Mantém credenciais no appsettings gerado em vez de movê-las para `_secrets/` (não recomendado) |

**LLM** (todos os comandos, exceto `--llm-rounds`)

| Opção | Descrição |
|---|---|
| `--llm` | `none` (padrão), `ollama` (local) ou `api` (LLM corporativa via API com OAuth2 client credentials) |
| `--llm-model` | Modelo. Ollama: padrão `qwen2.5-coder:3b`. API: nome enviado no corpo, padrão `DefaultModel`, ou `MIGRATOR_LLM_MODEL` |
| `--llm-endpoint` | Ollama: padrão `http://localhost:11434`. API: a URL é fixa no código; a opção (ou `MIGRATOR_LLM_ENDPOINT`) só sobrepõe para testes |
| `--llm-client-id` | API: `client_id` do OAuth2, ou `MIGRATOR_LLM_CLIENT_ID` |
| `--llm-client-secret` | API: `client_secret`. Prefira `MIGRATOR_LLM_CLIENT_SECRET` para não ficar no histórico do shell |
| `--llm-token-url` | API: sobrepõe o endpoint de token fixo no código, ou `MIGRATOR_LLM_TOKEN_URL`. Vazio = `client_secret` como Bearer fixo |
| `--llm-scope` | API: escopo do token, ou `MIGRATOR_LLM_SCOPE` |
| `--llm-rounds` | migrate: rodadas build → correção → build (padrão 3) |
| `--llm-timeout` | Tempo máximo de cada chamada, em minutos (padrão 6) |
| `--llm-no-cache` | Não usa o cache de respostas (`~/.migrator/llm-cache`) |

**Portfólio**

| Opção | Descrição |
|---|---|
| `--baseline` | `portfolio.json` de uma execução anterior para comparar a evolução |

Variáveis de ambiente: `MIGRATOR_NUGET_CONFIG`, `MIGRATOR_LLM_CLIENT_ID`, `MIGRATOR_LLM_CLIENT_SECRET`, `MIGRATOR_LLM_MODEL`, `MIGRATOR_LLM_ENDPOINT`, `MIGRATOR_LLM_TOKEN_URL`, `MIGRATOR_LLM_SCOPE`, `HTTPS_PROXY`.

---

## 14. Fluxo recomendado

1. **`analyze`** em cada aplicação (ou `portfolio` na pasta) para dimensionar: bloqueantes por projeto, dados acessados, bancos compartilhados.
2. **`migrate`** (lift-and-shift) e versione a saída imediatamente num repositório novo. Preencha os placeholders do `.iupipes.yml` e dos `parameters.json`, rode `_secrets/<projeto>/create-secrets.sh` e suba pela esteira.
3. Para modernizar, **`migrate --target net10`** em outra pasta. Corrija na saída em ordem de dependência (bibliotecas primeiro), recompile, use o relatório como checklist, procure os `// TODO Migrator` e revise `_Legacy/`. Com `--llm`, boa parte do laço é fechada sozinha.
4. **Teste o comportamento**, não só a compilação: autenticação, sessão, uploads, serialização JSON e rotas.
5. Repita `portfolio --baseline` para acompanhar a evolução.

> `migrate --force` **apaga e regera** a pasta de saída. Não use sobre uma saída com correções manuais; gere em outra pasta e compare.

---

## 15. Decisões de projeto e limitações

- **Cópia, nunca in-place.** A saída fica fora da origem e pode ser repetida quantas vezes for preciso.
- **Lift-and-shift primeiro.** O padrão não toca no código: o único ajuste é tirar URLs, e-mails e credenciais fixos, porque esses não podem ir para o repositório da plataforma. A modernização é opt-in.
- **EC2 Windows antes de container.** Aplicações .NET Framework rodam em Windows; o relatório mostra a hospedagem Linux como alternativa pós-modernização, não como padrão.
- **Preservar comportamento ao modernizar.** Web API mantém Newtonsoft.Json e PascalCase; EF6 fica na 6.5; `Encrypt=False` é adicionado às connection strings do SQL Server; `Nullable`/`ImplicitUsings` desligados; `AssemblyInfo.cs` preservado; `App.config` mantido quando há `Settings.settings`.
- **Não inventar código onde há risco.** `IConfiguration` só é injetado onde não há ambiguidade; nas demais classes o erro de build aponta o lugar. O mesmo vale para `HttpContext.Current` fora de controllers. Literais em atributos, `case` e strings interpoladas não são externalizados.
- **Segredos nunca em template, parâmetro ou git.** Só nomes; valores em `_secrets/` e no Secrets Manager.
- **Compatibilidade verificada**: pacotes consultados no feed e saída compilada (net10).

Limitações: VB.NET, Web Sites sem `.csproj`, F# e projetos de banco não são convertidos para .NET 10 (VB é copiado no destino framework). WebForms, WCF servidor, Identity 2, SignalR clássico e OAuth server do OWIN só entram no inventário. Reescritas por padrão de texto podem deixar passar casos incomuns; o build de verificação existe para isso. Erros de build aparecem em camadas. `COMReference` exige o MSBuild do Visual Studio.

---

## 16. Estrutura do código e como estender

```
src/Migrator.Core
  Analysis/     WorkspaceLoader, ProjectLoader, StartupAnalyzer, AssemblyInspector (metadados das DLLs locais), ApplicationProfiler (sinais), DataAccessAnalyzer
  Cloud/        ModernizationAdvisor, AwsArchitect (+ .Framework), LambdaScaffolder, InfrastructureGenerator (Terraform),
                CloudFormationGenerator (+ .Ec2, .EcsService, .Service: templates, parâmetros, CodeDeploy, .iupipes.yml, TAAC, workflows)
  Llm/          ILlmAssistant, OllamaAssistant, CorporateApiAssistant, CachedLlmAssistant, LlmAssistantFactory, LlmSession,
                LlmCodeFixer, LlmCodeDrafter, LlmTriage, LlmNarrator, LlmPrompts
  Data/         PackageRules, FrameworkReferenceRules, CodeRules, BuildHints, ModernizationRules, CodeModernizationRules
  Migration/    MigrationEngine (orquestração, layout da saída), ProjectMigrator (+ .Framework, .Settings), LiteralExternalizer,
                ConfigSettingsCollector, SecretsExtractor, ControllerRewriter, WorkerServiceRewriter, ConfigurationInjector,
                CodeTransformer, RazorTransformer, ConfigMigrator, PackagePlanner, PackageAligner, ProjectFileWriter,
                ProgramGenerator, BuildVerifier, RuntimeVerifier
  NuGet/        NuGetClient, NuGetConfigFile
  Reporting/    HtmlReport, MarkdownReport, ExcelReport, CsvReport, JsonReport (+ .Cloud, .Data)
  Portfolio/    PortfolioRunner, PortfolioAggregator, PortfolioReports
src/Migrator.Cli      analyze / migrate / portfolio (System.CommandLine + Spectre.Console)
tests/Migrator.Tests  165 testes unitários, de ponta a ponta sobre samples/LegacyShop e snapshots
samples/LegacyShop    solução legada de exemplo
docs/                 GitHub Pages (index.html, demo/, diagrams/)
```

Quase toda mudança de comportamento é uma entrada em `src/Migrator.Core/Data/`:

- **Pacote** (`PackageRules.ById`): `["Empresa.Logging.Legado"] = Replace("A API mudou: use ILogger<T>.", To("Empresa.Logging", VersionPolicy.Latest("3.0.0")))` ou `Manual("Depende de System.Web; use ...")`.
- **Padrão de código** (`CodeRules.CSharp`/`Razor`): `new("EMP001", @"\bEmpresa\.Cache\.Get\(", InventorySeverity.Warning, "Cache corporativo antigo", "Sem versão para .NET 10.", "Use IDistributedCache.")`.
- **Dica de build** (`BuildHints`): tipo em `TypeHints`, membro em `MemberHints` ou código em `CodeHints`.
- **Modernização por pacote** (`ModernizationRules.ById`/`ByPrefix`): `Deprecated("MOD-PKG-EMPRESA-PDF", Impact.Medium, Effort.Medium, título, evidência, proposta)`; por código (`CodeModernizationRules.CSharp`) com `Kind`, `Impact`, `Effort`, `OnlyKinds` e `AwsService`.
- **Sinal de arquitetura** (`ApplicationProfiler`): valor no enum `Signal` + regex em `CodeProbes` (ou prefixo em `PackageProbes`); use `profile.Has(Signal.X)` em `ModernizationAdvisor.FromProfile` e/ou em `AwsArchitect`.
- **Infra**: `CloudFormationGenerator.*` e `InfrastructureGenerator`; valide com `cfn-lint infra/*.yml` e `terraform validate` sobre o sample.

**Testes**: `dotnet test Migrator.slnx`. Ao adicionar uma regra, inclua um caso em `tests/Migrator.Tests` e, se for padrão comum, reproduza-o em `samples/LegacyShop`. Os **snapshots** (`tests/Migrator.Tests/Snapshots/*.snap`) congelam os arquivos gerados para o sample; mudança intencional se aceita com `MIGRATOR_UPDATE_SNAPSHOTS=1 dotnet test --filter SnapshotTests` e revisa no diff do commit.
