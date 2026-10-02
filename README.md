# Migrator — .NET Framework → .NET 10

Ferramenta de linha de comando que lê uma aplicação .NET Framework inteira (solução, projeto ou diretório), gera uma **cópia** migrada para .NET 10, compila essa cópia e produz um **inventário** do que ainda exige ação manual, com uma sugestão para cada item. O código original nunca é alterado.

Além da migração, a ferramenta **entende a aplicação** e entrega duas camadas de recomendação: **modernização** (bibliotecas que passaram a ser pagas ou foram descontinuadas, código C# que compila mas muda de comportamento no .NET 10/Linux, idiomas antigos) e **arquitetura alvo na AWS** (qual serviço hospeda cada projeto — ECS Fargate, tarefa agendada, Lambda, containers Windows — e quais serviços gerenciados substituem banco, filas, arquivos, e-mail, sessão, segredos e agendamento), com Dockerfiles gerados, diagrama e plano de migração. Foi pensada para programas de migração em lote: dezenas de aplicações, mesmo pipeline, mesma arquitetura de referência.

Tipos de projeto suportados: ASP.NET MVC 5, ASP.NET Web API 2, console, Windows Service, bibliotecas e testes (MSTest/NUnit/xUnit). Projetos WinForms/WPF recebem apenas a conversão do `.csproj` (`net10.0-windows`). Projetos VB.NET, Web Site (sem `.csproj`) e de banco de dados aparecem no inventário como não migrados.

- [Resumo](#resumo)
- [Início rápido](#início-rápido)
- [Como funciona](#como-funciona)
- [Modernização e arquitetura AWS](#modernização-e-arquitetura-aws)
- [Exemplo: antes e depois](#exemplo-antes-e-depois)
- [Lendo o inventário](#lendo-o-inventário)
- [Fluxo de trabalho recomendado](#fluxo-de-trabalho-recomendado)
- [Decisões de projeto](#decisões-de-projeto)
- [Limitações conhecidas](#limitações-conhecidas)
- [Referência da linha de comando](#referência-da-linha-de-comando)
- [Estrutura do código e como estender](#estrutura-do-código-e-como-estender)

---

## Resumo

É uma CLI em .NET 10 que lê a aplicação inteira (`.sln`, `.slnx`, `.csproj` ou uma pasta), gera uma cópia migrada ao lado da original, compila essa cópia e entrega o inventário do que ainda precisa de ação manual, com sugestão em cada item. O projeto original nunca é alterado.

```powershell
migrator analyze C:\src\MinhaApp\MinhaApp.sln   # só gera o inventário, não grava código
migrator migrate C:\src\MinhaApp\MinhaApp.sln   # gera C:\src\MinhaApp.net10 + relatórios
```

**O que ela converte sozinha:**

- **Projetos:** `.csproj` antigo vira formato SDK com `net10.0`; `packages.config` vira `PackageReference`. Referências entre projetos, DLLs locais, arquivos linkados, recursos embutidos e build events são preservados.
- **Pacotes NuGet:**
  - Tem regras próprias para OWIN, Identity, Unity/Ninject, EF6, pacotes `.pt-br` e bibliotecas front-end.
  - Para os demais, verifica no nuget.org se há versão compatível e só atualiza quando precisa.
  - Não sobe para versões que passaram a ser pagas (AutoMapper ≥ 15, MediatR ≥ 13).
  - Alinha as versões entre projetos para o restore não falhar.
- **`web.config`/`app.config` → `appsettings.json`:** inclui `Web.Release.config` → `appsettings.Production.json`, seções customizadas, SMTP, endpoints WCF e log4net/NLog em arquivo próprio. As configurações de IIS (rewrite, headers, limites) ficam num `web.config` mínimo.
- **`Program.cs` gerado:** rotas do `RouteConfig` e das áreas, filtros, CORS, Forms Authentication virando cookie, sessão, cultura pt-BR, limite de upload e os registros do Unity convertidos para o DI nativo.
- **Código:** os controllers Web API ganham `[ApiController]`, rotas e verbos explícitos. Sem isso, o JSON do corpo e o roteamento `Get()`/`Post()` quebrariam em silêncio. Também converte bundles nas views, move arquivos estáticos para `wwwroot` e regrava arquivos ANSI em UTF-8 sem perder acentos.
- **Código antigo sem equivalente** (`Global.asax`, `App_Start`, OWIN, WebForms, `.ashx`) vai para `_Legacy/`, fora do build, para consulta.

**Inventário:** sai em HTML (com filtros), Markdown, Excel e CSV. Cada item traz severidade, arquivo e linha, e uma sugestão. Os erros do compilador da cópia migrada também entram no inventário.

**Como testei:**

- **Aplicação de exemplo:** criei uma aplicação legada realista em `samples/LegacyShop`, com MVC5 + Web API 2 + EF6, um Windows Service e testes MSTest.
- **Migração de ponta a ponta:** os erros que o compilador encontrou na cópia migrada já estavam todos previstos no inventário.
- **Testes automatizados:** 46 testes, todos passando.
- **Relatório HTML:** abri no Edge e está renderizando corretamente.
- **Ferramenta global:** o pacote instala e funciona como `migrator`.

**Limitações:**

- **Erros de build aparecem em camadas.** O compilador só mostra os erros dentro dos métodos depois que os erros de tipos e assinaturas são corrigidos. Na prática, o ciclo é corrigir, compilar e repetir.
- **WebForms, WCF servidor e Identity 2 não são migrados.** Ficam no inventário como itens bloqueantes, com sugestão.
- **EF6 é mantido na versão 6.5**, que roda no .NET 10, em vez de passar para EF Core. É a opção de menor risco; a troca para EF Core fica sugerida para uma etapa posterior.
- **Projetos VB.NET ficam de fora** e aparecem listados como não migrados.
- **Pacotes de feed privado** precisam do `NuGet.config` na raiz da solução para o build de verificação funcionar.
- **Não testei com aplicações reais da sua empresa.** Sugiro começar rodando o `analyze` em uma delas; o que aparecer de padrão novo vira regra em `src/Migrator.Core/Data/` (o README explica como estender).

---

## Início rápido

Requisitos: SDK do .NET 10. Acesso ao nuget.org é recomendado (sem ele, use `--offline`). Para o build de verificação de projetos que usam feeds privados, a solução original precisa ter um `NuGet.config` na raiz (ele é copiado para a saída).

```powershell
dotnet build Migrator.slnx

# 1) Análise: lê tudo, simula a migração em memória e gera só o inventário
dotnet run --project src/Migrator.Cli -- analyze C:\src\Loja\Loja.sln

# 2) Migração: gera C:\src\Loja.net10, compila cada projeto e gera o inventário
dotnet run --project src/Migrator.Cli -- migrate C:\src\Loja\Loja.sln
```

Para usar como ferramenta global (comando `migrator`):

```powershell
dotnet pack src/Migrator.Cli -o nupkg
dotnet tool install -g Migrator.NetFramework --add-source nupkg
migrator migrate C:\src\Loja\Loja.sln
```

---

## Como funciona

As duas operações executam o mesmo pipeline. `analyze` roda tudo em memória e só grava os relatórios. `migrate` também grava a saída e compila.

```
 entrada (.sln / .slnx / .csproj / pasta)
   │
   ▼
 1. Leitura da aplicação ........ WorkspaceLoader, ProjectLoader
   │
   ▼  para cada projeto:
 2. Classificação dos arquivos .. código, legado, views, estáticos, cópia
 3. Análise da inicialização .... Global.asax, App_Start, Startup OWIN → plano de inicialização
 4. Configuração ................ web.config/app.config → appsettings*.json + dicas para o Program.cs
 5. Código C# ................... Roslyn (controllers) + regras de reescrita + regras de detecção
 6. Views Razor ................. bundles, partials, _ViewImports
 7. Pacotes NuGet ............... regras + consulta ao nuget.org
 8. .csproj e Program.cs ........ montagem do projeto SDK-style e do pipeline ASP.NET Core
   │
   ▼  para a solução:
 9. Alinhamento de pacotes ...... versões coerentes entre projetos (evita NU1605)
10. Gravação da saída ........... cópia migrada + .slnx            (só migrate)
11. Build de verificação ........ dotnet build por projeto           (só migrate)
12. Relatórios .................. HTML, Markdown, Excel, CSV
   (em paralelo, por projeto)
 M. Perfil da aplicação ......... sinais de arquitetura (banco, arquivos, filas, SMTP, sessão, agendamento, Windows...)
 N. Modernização ................ regras de pacotes + regras de código C# + sinais → sugestões
 A. Arquitetura AWS ............. hospedagem por projeto, serviços, Dockerfile, diagrama, plano
```

Cada etapa registra o que fez no inventário: o que foi resolvido automaticamente e o que ficou pendente.

### 1. Leitura da aplicação

- **Entrada**: `.sln` (lê as linhas `Project(...)`), `.slnx` (XML), um `.csproj` isolado ou uma pasta (procura `*.csproj` recursivamente, ignorando `bin`, `obj`, `packages`, `node_modules` e saídas anteriores do Migrator).
- **Projetos não C#** (`.vbproj`, `.fsproj`, `.sqlproj`, `.wixproj`, Web Sites) são listados como "não migrados", com o motivo.
- **Leitura do `.csproj`** (formato antigo ou SDK-style): propriedades (`TargetFrameworkVersion`, `OutputType`, `RootNamespace`, `PlatformTarget`, `DefineConstants`, assinatura), todos os itens de arquivo com seus metadados, `Reference` (GAC ou com `HintPath`), `ProjectReference`, `COMReference`, `Import` e `Target` customizados, build events, `packages.config` e a URL do IIS Express.
- **Tipo do projeto** (a primeira regra que bater vence):

| Tipo | Como é detectado |
|---|---|
| Testes | referência a MSTest/NUnit/xUnit ou GUID de projeto de teste |
| Web | GUID de Web Application ou `web.config` + (`Global.asax`, MVC, Web API ou `.aspx`) |
| Desktop | referência a `System.Windows.Forms` ou WPF |
| Windows Service | `Exe` + `System.ServiceProcess` + classe `: ServiceBase` |
| Console | `OutputType` `Exe`/`WinExe` |
| Biblioteca | demais casos |

Projetos que **já são** SDK-style com `netstandard`/`net5+` são copiados sem alteração.

### 2. Classificação dos arquivos

Apenas os arquivos que pertencem ao `.csproj` são considerados. Projetos SDK-style incluem tudo o que estiver na pasta, então arquivos esquecidos no disco quebrariam o build; por isso eles não são copiados, mas são listados no inventário.

| Papel | Quais arquivos | Destino |
|---|---|---|
| Código | `.cs` | transformado (etapa 5) |
| Legado | `Global.asax.cs`; `App_Start/*.cs` que usam System.Web/OWIN/Unity/Ninject; Startup OWIN (`IAppBuilder`); `AreaRegistration`; code-behind de WebForms; instaladores `[RunInstaller]` | `_Legacy/` (fora do build, para consulta) |
| WebForms | `.aspx`, `.ascx`, `.master`, `.ashx`, `.asmx`, `.svc` | `_Legacy/` |
| Views | `.cshtml` | transformadas (etapa 6) |
| Estáticos (web) | arquivos em `Content`, `Scripts`, `fonts`, `Images`, `css`, `js`, `lib`... e `favicon.ico`/`robots.txt` | `wwwroot/` (as URLs `~/Content/...` continuam válidas) |
| Configuração | `web.config`, `app.config`, `Web.*.config`, `Views/web.config`, `packages.config` | não copiados; convertidos (etapas 4 e 6) |
| Demais | qualquer outro item do projeto | copiado como está |

Arquivos `.cs` linkados de fora da pasta do projeto (`<Compile Include="..\Shared\X.cs">`) são copiados e transformados se estiverem dentro da solução. Os arquivos `.cs` são lidos respeitando BOM, e os salvos em ANSI (Windows-1252) são convertidos para UTF-8 sem perder a acentuação.

### 3. Análise da inicialização (projetos web)

Os arquivos legados são lidos com Roslyn para extrair um **plano de inicialização**, usado depois no `Program.cs`:

| Origem | O que é extraído |
|---|---|
| `RouteConfig` (`routes.MapRoute`) | nome, URL, defaults (`UrlParameter.Optional` → `{id?}`), constraints (regex ancoradas), `LowercaseUrls` |
| `AreaRegistration` | rotas de área → `MapAreaControllerRoute` |
| `WebApiConfig` | template da rota (`api/{controller}/{id}` → `api/[controller]`), CORS (`EnableCorsAttribute`), camelCase do JSON, remoção do formatter XML |
| `FilterConfig` | `RequireHttps`, `Authorize` global; filtros customizados viram TODO |
| `UnityConfig`, `NinjectWebCommon` | `RegisterType<I, C>()` e `Bind<I>().To<C>()` → `AddTransient`/`AddScoped`/`AddSingleton` conforme o lifetime |
| `Global.asax.cs` | instruções desconhecidas do `Application_Start` (viram comentários TODO); eventos `Application_Error`, `BeginRequest`, `AuthenticateRequest`, `Session_Start`... (itens do inventário com a alternativa) |
| Startup OWIN | cada `app.UseXxx()` vira um item com o equivalente no ASP.NET Core |

### 4. Configuração (`web.config`/`app.config` → `appsettings.json`)

| No config | Resultado |
|---|---|
| `<appSettings>` | `appsettings.json` → `"AppSettings"`; chaves de infraestrutura (`webpages:*`, `ClientValidationEnabled`...) descartadas; `file=` e `configSource=` incorporados |
| `<connectionStrings>` | `"ConnectionStrings"`; avisos para EDMX (`metadata=`), `|DataDirectory|` e providers não SQL Server |
| `Web.Release.config`, `App.Debug.config`... | `appsettings.Production.json`, `appsettings.Development.json`... (transformações de `appSettings`/`connectionStrings`) |
| seções customizadas (`<configSections>`) | convertidas de XML para JSON (atributos → propriedades, `<add key value>` → dicionário) |
| `<applicationSettings>` (Settings.settings) | `"ApplicationSettings"`; em projetos não-web o `App.config` é mantido para `Properties.Settings.Default` continuar funcionando |
| `<system.serviceModel><client>` | `"WcfClient:Endpoints"` (endereços para criar o cliente em código) |
| `<system.net><mailSettings>` | `"Smtp"` |
| `<log4net>` / `<nlog>` | `log4net.config` / `nlog.config`, copiados para a saída |
| `<authentication mode="Forms">` | cookie authentication no `Program.cs` (loginUrl, timeout, nome do cookie) |
| `<authentication mode="Windows">` | `AddNegotiate()` + pacote Negotiate |
| `<authorization><deny users="?">` | `FallbackPolicy` exigindo usuário autenticado |
| `<sessionState>` | `AddSession`/`UseSession` com o mesmo timeout |
| `<globalization culture="pt-BR">` | `app.UseRequestLocalization("pt-BR")` |
| `maxRequestLength` / `maxAllowedContentLength` | limites do Kestrel, IIS e `FormOptions` |
| `<customErrors defaultRedirect>` | `UseExceptionHandler(...)` |
| `<httpCookies>` | `CookiePolicyOptions` |
| `<system.webServer>` (rewrite, headers, mime, limites) | preservado num `web.config` mínimo para o IIS (o publish acrescenta o handler do ASP.NET Core) |
| `httpModules`/`handlers` customizados, `machineKey`, providers de Membership, `<location>`, WIF... | itens do inventário com a alternativa |
| `<runtime>` (binding redirects), `<system.codedom>`, `<startup>` | descartados (não se aplicam) |

Senhas e chaves copiadas geram um alerta para movê-las para User Secrets, variáveis de ambiente ou Key Vault.

### 5. Código C#

Três passos, nesta ordem, para cada arquivo:

1. **Reescrita de controllers (Roslyn).** A ferramenta monta um catálogo de herança de todo o projeto e detecta controllers mesmo quando herdam de uma base própria (`MeuApiBase : ApiController`). Em cada controller Web API ela:
   - troca `ApiController` → `ControllerBase` e adiciona `[ApiController]`, o que preserva o binding do Web API (tipos complexos vêm do corpo JSON);
   - adiciona `[Route("api/[controller]")]` a partir do template do `WebApiConfig`, porque `[ApiController]` exige rota por atributo;
   - explicita o verbo de cada action pela convenção de nomes do Web API (`GetX` → `[HttpGet]`, sem prefixo → `[HttpPost]`) e adiciona `{id}` quando há parâmetro `id`;
   - troca o retorno `HttpResponseMessage` → `IActionResult` e `Json(x)` → `new JsonResult(x)`;
   - detecta rotas que ficariam ambíguas no ASP.NET Core.

   Em controllers MVC e Web API, `HttpContext.Current` vira a propriedade `HttpContext`. Controllers em `Areas/X/Controllers` ganham `[Area("X")]`.
2. **Reescrita por regras.** Usings `System.Web.*` → `Microsoft.AspNetCore.*` (namespaces sem equivalente ficam comentados) e trocas de tipos e APIs:

   | Antes | Depois |
   |---|---|
   | `IHttpActionResult`, `HttpStatusCodeResult`, `HttpNotFound()` | `IActionResult`, `StatusCodeResult`, `NotFound()` |
   | `Request.CreateResponse(HttpStatusCode.X, v)`, `StatusCode(HttpStatusCode.X)` | `StatusCode((int)HttpStatusCode.X, v)` |
   | `[FromUri]`, `[RoutePrefix]`, `[ResponseType]`, `[Bind(Include=...)]` | `[FromQuery]`, `[Route]`, `[ProducesResponseType]`, `[Bind(...)]` |
   | `HttpPostedFileBase`, `HttpContextBase`, `MvcHtmlString`, `this HtmlHelper` | `IFormFile`, `HttpContext`, `HtmlString`, `this IHtmlHelper` |
   | `Json(x, JsonRequestBehavior.AllowGet)` | `Json(x)` |
   | `[OutputCache(Duration=60, VaryByParam="id")]` | Output Caching do ASP.NET Core (+ `AddOutputCache`/`UseOutputCache`) |
   | `Request.IsAuthenticated`, `Request.QueryString[..]`, `Request.Files`, `Request.IsAjaxRequest()` | equivalentes do ASP.NET Core |
   | `[AllowHtml]`, `[ValidateInput(false)]` | removidos (não há request validation) |
   | `ConfigurationManager.AppSettings["X"]` / `.ConnectionStrings["Y"].ConnectionString` | `configuration["AppSettings:X"]` / `configuration.GetConnectionString("Y")` |
   | `System.Data.SqlClient` | `Microsoft.Data.SqlClient` |
   | `XmlConfigurator.Configure()` (log4net) | aponta para o `log4net.config` extraído |

   Os usings necessários (`Mvc.Filters`, `Mvc.Rendering`, `Http`, `Authorization`, `OutputCaching`...) são adicionados conforme os tipos usados no arquivo.
3. **Regras de detecção** (`Data/CodeRules.cs`). Rodam sobre o código já transformado e geram os itens manuais, com arquivo, linha da primeira ocorrência e contagem. Exemplos: `HttpContext.Current` fora de controllers, `Server.MapPath`, `Session[...]`, `FormsAuthentication`, filtros customizados, `IHttpModule`, child actions, `BinaryFormatter`, `Thread.Abort`, AppDomains, Remoting, hospedagem WCF, `WebClient`, `SmtpClient`, `DbContext("name=...")` e `CreatedAtRoute("DefaultApi")`.

Durante essas etapas a ferramenta também levanta "fatos" sobre o código (usa SqlClient? WCF client? `System.Drawing`? `EventLog`? APIs de `System.Configuration` que sobraram?). Eles decidem quais pacotes adicionar e se o `App.config` precisa ser mantido.

### 6. Views Razor

- `@Scripts.Render("~/bundles/x")` e `@Styles.Render(...)` são expandidos em `<script>`/`<link>` com `asp-append-version`, a partir do `BundleConfig.cs`. São resolvidos `{version}`, `*` e `IncludeDirectory`, e ficam de fora `.min`, `-vsdoc`, `.intellisense` e `.map`, como no bundling original.
- `@Html.Partial` → `@await Html.PartialAsync`, `Html.RenderPartial` → `RenderPartialAsync`, `Json.Encode` → `JsonSerializer.Serialize`, `Request.IsAuthenticated` → `User.Identity.IsAuthenticated`, `@using System.Web.*` removidos, `@model HandleErrorInfo` removido.
- `_ViewImports.cshtml` é gerado a partir dos `<namespaces>` de cada `Views/web.config` (inclusive das áreas), com Tag Helpers habilitados.
- Itens manuais: `@helper`, `@Ajax.*`, `Html.Action` (child actions), `EnumDropDownListFor`, `WebGrid`, `@inherits WebViewPage`.

### 7. Pacotes NuGet

Para cada pacote do `packages.config`/`PackageReference`:

```
há regra em Data/PackageRules.cs?
 ├─ Remove   → removido (já faz parte do .NET/ASP.NET Core, conteúdo client-side, idioma .pt-br, ferramenta de build)
 ├─ Replace  → trocado pelo equivalente (ex.: Microsoft.Owin.Security.Jwt → Microsoft.AspNetCore.Authentication.JwtBearer)
 ├─ Manual   → removido + item bloqueante (ex.: Unity, Identity 2, SignalR clássico, ELMAH)
 └─ Keep     → mantido com a política de versão da regra
não há regra → consulta ao nuget.org (pastas lib/<tfm> do pacote):
 ├─ versão atual compatível → mantém, atualizando para a última da mesma versão major
 ├─ só tem binários .NET Framework → sobe para a versão mais recente compatível (item de atenção)
 ├─ nenhuma versão compatível → mantém + item bloqueante (NU1701 no build)
 └─ não existe no nuget.org → mantém + item "feed privado?"
```

- Políticas de versão: "mesma se compatível", "linha 10.0 do .NET" (Microsoft.Extensions.*, EF Core, ASP.NET Core), "última da major N" (EF6 → 6.x) e limites de licença (AutoMapper < 15, MediatR < 13, FluentAssertions < 8, EPPlus < 5, MassTransit < 9).
- Pacotes adicionados conforme os fatos do código: `Microsoft.AspNetCore.Mvc.NewtonsoftJson` (Web API), `System.ServiceProcess.ServiceController` (Windows Service), `System.Configuration.ConfigurationManager`, `Microsoft.Extensions.Configuration.*`, `Microsoft.Data.SqlClient`, `System.ServiceModel.*` (cliente WCF), `System.Drawing.Common`, `System.Diagnostics.EventLog`, `System.Runtime.Caching`, `Microsoft.NET.Test.Sdk` e o adapter de testes.
- Referências do GAC (`System.Management`, `System.DirectoryServices`...) viram pacotes **somente se o código usa o namespace**; referências padrão de template sem uso são descartadas.
- DLLs locais (`HintPath`) são copiadas e inspecionadas pelo metadata: a ferramenta identifica o framework para o qual foram compiladas e se dependem de `System.Web`.
- Com `--offline`, as versões originais são mantidas e marcadas como "não verificadas".

### 8. `.csproj` e `Program.cs`

O `.csproj` gerado usa o SDK `Microsoft.NET.Sdk.Web` (web), `Microsoft.NET.Sdk.Razor` (biblioteca com views) ou `Microsoft.NET.Sdk`, com `net10.0` (ou `net10.0-windows` para Windows Service e desktop). Contém:

- `Nullable` e `ImplicitUsings` desligados, `GenerateAssemblyInfo=false` quando existe `AssemblyInfo.cs`, e `Deterministic=false` quando a versão usa curinga (`1.0.*`);
- propriedades preservadas: `PlatformTarget`, `DefineConstants`, assinatura, ícone e `StartupObject`;
- `ProjectReference` (caminhos relativos mantidos) e `Reference` com `HintPath`;
- itens com tipo diferente do padrão do SDK: recursos embutidos não-`.resx`, `Content` que precisa ir para o publish e metadados de designer (`Generator`, `DependentUpon`...);
- `Compile Remove="_Legacy\**"`;
- `FrameworkReference Microsoft.AspNetCore.App` para bibliotecas que usavam System.Web.Mvc/Http;
- targets, imports e `COMReference` customizados; build events convertidos em targets, com fallback de `$(SolutionDir)`.

Para projetos web é gerado o `Program.cs` com o plano da etapa 3 e as dicas da etapa 4, além de `Properties/launchSettings.json` com a mesma porta do IIS Express. O que não pôde ser convertido aparece como comentário `// TODO Migrator`.

### 9. Alinhamento de pacotes

Depois de planejar todos os projetos, a ferramenta percorre a solução em ordem de dependência e eleva versões diretas quando um pacote adicionado exige mais (ex.: `NewtonsoftJson 10.0.x` exige `Newtonsoft.Json >= 13.0.3`) ou quando um projeto referenciado usa versão maior. Sem isso o restore falharia com NU1605 (downgrade).

### 10. Gravação da saída (somente `migrate`)

- **Pasta de saída**: `<pasta-pai>\<nome-da-solução>.net10`. Ela não pode ficar dentro da origem nem contê-la.
- **Proteção de pastas**: se a pasta já existir com conteúdo, o Migrator só a substitui com `--force` e apenas se ela tiver o marcador `.migrator-output`, ou seja, se tiver sido criada pela própria ferramenta.
- **Arquivos gerados**: um `.slnx` com os projetos migrados; são copiados `NuGet.config`, `.editorconfig` e `Directory.Build.*`.

### 11. Build de verificação (somente `migrate`)

- **Ordem de compilação**: cada projeto é compilado com `dotnet build` em ordem topológica, começando pelas bibliotecas. Um projeto cuja dependência falhou não é compilado e aparece como **bloqueado**. Assim, um pacote privado ausente num projeto não esconde os erros dos outros.
- **Erros no inventário**: erros de compilação, restore (NU1101, NU1605), compatibilidade (NU1701, CA1416), APIs obsoletas (SYSLIB*) e vulnerabilidades (NU1902–NU1904) viram itens com arquivo, linha e uma sugestão de `Data/BuildHints.cs` (ex.: `CS0246 'HttpPostedFileBase'` → "use IFormFile").
- **Log completo**: fica em `build-verification.log`.

> O compilador C# só aponta erros dentro dos métodos depois que os erros de declaração (tipos, atributos, assinaturas) são resolvidos. O build mostra a primeira camada; as regras de detecção da etapa 5 já antecipam as seguintes.

### 12. Relatórios

| Arquivo | Uso |
|---|---|
| `migration-report.html` | relatório navegável: resumo, tabela de projetos, filtros por severidade e busca, erros de build agrupados por código |
| `migration-report.md` | versionar no repositório, anexar em PR ou wiki |
| `inventory.xlsx` | abas Resumo, Inventário, Modernização e Arquitetura AWS, com filtros, para estimar e distribuir o trabalho |
| `inventory.csv` | integração com outras ferramentas (UTF-8 com BOM) |
| `modernization.csv` | sugestões de modernização e arquitetura (tipo, impacto, esforço, evidência, serviço AWS) |
| `build-verification.log` | saída completa do build |

---

## Modernização e arquitetura AWS

Enquanto migra, a ferramenta monta um **perfil** de cada projeto a partir do código original, dos pacotes, das referências de framework e do `web.config`/`app.config`: que banco usa (e se a connection string usa Integrated Security), se grava arquivos em disco ou em pastas de rede, se consome MSMQ/RabbitMQ, se envia e-mail por SMTP, se guarda sessão em memória ou estado em coleções estáticas, se tem timers/agendadores, que autenticação usa, se depende de componentes Windows (COM, Registro, System.Drawing, Event Log, WMI, Crystal Reports...), se tem segredos em texto claro e quais hosts internos acessa. Bibliotecas são **fundidas** nos projetos que as referenciam, então a recomendação de um site considera o que as DLLs dele fazem.

### Modernização

Sugestões que **não bloqueiam a compilação** e por isso ficam separadas do inventário (não entram no percentual de automação nem no código de saída). Cada item tem tipo, impacto, esforço, evidência (pacote ou `arquivo:linha`) e, quando se aplica, o serviço AWS relacionado.

| Tipo | O que detecta | Exemplos |
|---|---|---|
| **Licença** | bibliotecas que passaram a ser comerciais; a ferramenta mantém a última versão gratuita e propõe alternativas | AutoMapper 15+ → Mapperly; MediatR 13+ → Mediator (source generator); FluentAssertions 8+ → AwesomeAssertions; EPPlus 5+ → ClosedXML; MassTransit 9+ → AWS.Messaging (SQS/SNS); iTextSharp (AGPL) → QuestPDF; suites comerciais (Telerik, DevExpress...) |
| **Descontinuado** | sem manutenção ou sem versão para .NET 10 | Topshelf, Common.Logging, DotNetZip (CVE), Rotativa/wkhtmltopdf, Crystal Reports, ReportViewer, IdentityServer4, DotNetOpenAuth, Enterprise Library |
| **Modernização** | alternativa mais simples/rápida ou código C# que **compila mas muda de comportamento** | `Encoding.GetEncoding(1252)` sem provider (exceção em runtime), `Encoding.Default` (virou UTF-8), parse/formatação sem cultura (container sem `LANG` usa cultura invariante), comparações de string com ICU, `string.GetHashCode()` persistido (aleatório por processo), `new HttpClient()` por chamada, `.Result`/`.Wait()`, `async void`, threads manuais, `ArrayList`, DataSet, EF6 → EF Core, Newtonsoft → System.Text.Json |
| **Cloud (AWS)** | o que precisa mudar para rodar em container/serviços gerenciados | MSMQ → SQS; arquivos/UNC → S3 (ou EFS); SMTP → SES; sessão InProc → ElastiCache; chaves do Data Protection fora do container; `TransactionScope` (sem MSDTC no Linux); `DateTime.Now` (container em UTC); caminhos com `\` e maiúsculas (Linux é case-sensitive); `Process.Start`; IP/HTTPS atrás do ALB; Windows Service → BackgroundService; System.Drawing/Event Log/Registro/COM; Integrated Security no RDS; hosts on-premises (VPN); Azure Storage/Service Bus/Key Vault → S3/SQS/Secrets Manager |
| **Segurança** | riscos que a migração é um bom momento para corrigir | senhas no config → Secrets Manager; MD5/SHA1/DES/Rijndael; `Random` para tokens; SQL por concatenação; catch vazio |

### Arquitetura alvo (AWS)

Para cada projeto publicável a ferramenta recomenda **onde rodar** e por quê, com pré-requisitos e alternativas:

| Projeto | Recomendação padrão | Quando muda |
|---|---|---|
| Web (MVC/Web API) | **ECS Fargate (Linux) atrás de um ALB** — padrão de menor operação para um portfólio | Dependência dura de Windows (COM, Registro, Office Interop, Crystal, WMI, P/Invoke) → **containers Windows no ECS**; IIS em código → **EC2 Windows**. API sem views/sessão → alternativa **Lambda**; app interna simples → alternativa **App Runner** |
| Windows Service / console com timer ou Quartz/Hangfire | **tarefa ECS Fargate agendada pelo EventBridge Scheduler** (paga só a execução) | alternativa Lambda se < 15 min |
| Windows Service consumindo fila (MSMQ/RabbitMQ/MassTransit) | **worker ECS Fargate consumindo SQS**, escalado pela profundidade da fila | alternativa Lambda com gatilho SQS |
| Biblioteca / testes | não publicável (empacotada nos consumidores / roda no CI) | — |
| Desktop | fora da AWS (ou AppStream 2.0) | — |

Dependências Windows "moles" (System.Drawing, Event Log, PerformanceCounter, ServiceBase, MSMQ, Windows Auth, pastas UNC) **não** forçam containers Windows: a recomendação continua Linux e lista o que substituir (itens `MOD-WIN-*`).

Na solução como um todo, a ferramenta monta a lista de **serviços** (obrigatórios e recomendados) com o que cada um substitui e quem usa — RDS (engine conforme as connection strings), S3/EFS, SQS/SNS ou Amazon MQ, SES, ElastiCache, Secrets Manager, Parameter Store, CloudWatch, EventBridge Scheduler, Cognito ou Managed AD, CloudFront, VPN/Direct Connect quando há hosts internos, ECR e pipeline de CI/CD —, um **diagrama Mermaid**, um **plano em fases**, **riscos** e **notas de custo** (containers Windows ≈ 2x, licença do SQL Server no RDS e a opção Aurora PostgreSQL/Babelfish, NAT Gateway, retenção de logs).

No `migrate`, a saída já vem **pronta para container**:

- `Dockerfile` por projeto publicável (multi-stage, build a partir da raiz da solução, porta 8080, usuário não-root, `TZ`/`LANG` definidos quando a aplicação depende de fuso/cultura; imagens Windows quando a recomendação exige) e `.dockerignore` na raiz;
- `Program.cs` com `/health` (target group do ALB), `UseForwardedHeaders` (IP e esquema reais atrás do balanceador) e cultura padrão para threads fora de requisição.

Tudo isso aparece no relatório HTML (seções "Arquitetura alvo (AWS)" e "Modernização"), no Markdown (com o diagrama renderizável no GitHub/Azure DevOps), no Excel (abas "Modernização" e "Arquitetura AWS") e em `modernization.csv`. Para desligar: `--cloud none` (remove a arquitetura, os Dockerfiles e as sugestões do tipo Cloud; as demais continuam).

---

## Exemplo: antes e depois

Controller Web API 2 original:

```csharp
public class ProdutosApiController : ApiController
{
    public IEnumerable<ProdutoEntidade> GetPorCategoria([FromUri] string categoria) { ... }

    [ResponseType(typeof(ProdutoEntidade))]
    public IHttpActionResult Get(int id) { ... }

    public HttpResponseMessage Post(ProdutoEntidade produto)
    {
        if (!ModelState.IsValid)
            return Request.CreateErrorResponse(HttpStatusCode.BadRequest, ModelState);
        return Request.CreateResponse(HttpStatusCode.Created, produto);
    }
}
```

Depois da migração:

```csharp
[Route("api/[controller]")]
[ApiController]
public class ProdutosApiController : ControllerBase
{
    [HttpGet]
    public IEnumerable<ProdutoEntidade> GetPorCategoria([FromQuery] string categoria) { ... }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ProdutoEntidade), 200)]
    public IActionResult Get(int id) { ... }

    [HttpPost]
    public IActionResult Post(ProdutoEntidade produto)
    {
        if (!ModelState.IsValid)
            return StatusCode((int)HttpStatusCode.BadRequest, ModelState);
        return StatusCode((int)HttpStatusCode.Created, produto);
    }
}
```

Trecho do `web.config` original e o resultado:

```xml
<appSettings>
  <add key="webpages:Version" value="3.0.0.0" />
  <add key="ItensPorPagina" value="20" />
</appSettings>
<connectionStrings>
  <add name="DefaultConnection" connectionString="Data Source=srv01;Initial Catalog=Loja;Integrated Security=True" />
</connectionStrings>
<system.web>
  <authentication mode="Forms"><forms loginUrl="~/Account/Login" timeout="60" /></authentication>
  <globalization culture="pt-BR" />
</system.web>
```

```jsonc
// appsettings.json
{
  "ConnectionStrings": { "DefaultConnection": "Data Source=srv01;Initial Catalog=Loja;Integrated Security=True;Encrypt=False" },
  "AppSettings": { "ItensPorPagina": "20" }
}
```

```csharp
// Program.cs (trecho)
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options => { options.LoginPath = "/Account/Login"; options.ExpireTimeSpan = TimeSpan.FromMinutes(60); });
...
app.UseRequestLocalization("pt-BR");
```

A solução completa usada nesses exemplos está em `samples/LegacyShop` (MVC 5 + Web API 2 + EF6 + Windows Service + MSTest):

```powershell
dotnet run --project src/Migrator.Cli -- migrate samples\LegacyShop\LegacyShop.sln -o C:\temp\LegacyShop.net10
```

---

## Lendo o inventário

| Campo | Significado |
|---|---|
| Severidade | **Bloqueante**: impede compilar ou funcionar. **Atenção**: compila, mas o comportamento pode mudar ou precisa de revisão. **Informativo**: registro. |
| Automático | **Sim**: a ferramenta já resolveu (fica registrado para auditoria). **Não**: precisa de ação. |
| Categoria | Pacote, Código, View, Configuração, Inicialização, Projeto ou Build |
| Regra | Identificador estável, útil para filtrar e agrupar (prefixos: `PKG-` pacotes, `CS-`/`WEB`/`NET` código, `VW` views, `CFG-` configuração, `STARTUP-` inicialização, `PRJ-` projeto, `CSxxxx`/`NUxxxx`/`SYSLIBxxxx` build) |
| Arquivo / Linha | Caminho relativo ao projeto migrado e a primeira ocorrência |
| Ocorrências | Quantas vezes o padrão aparece no arquivo |

**% automatizado** = itens resolvidos automaticamente ÷ (automáticos + pendentes de ação), sem contar os erros de build.

**Código de saída**: `0` = nenhuma pendência bloqueante e build OK; `2` = há itens bloqueantes ou erro de build; `1` = erro de uso (caminho inválido, pasta de saída em conflito). Em pipelines, use o `analyze` como "portão" para acompanhar a evolução de cada aplicação.

---

## Fluxo de trabalho recomendado

1. **Rode o `analyze`** em cada aplicação do portfólio e use o `inventory.xlsx` para estimar o esforço (bloqueantes por projeto e por categoria).
2. **Ataque antes os bloqueantes estruturais**, que costumam definir o tamanho do trabalho: WebForms, WCF servidor, Identity 2/Membership, containers de DI, child actions, módulos HTTP.
3. **Rode o `migrate`** e versione a pasta gerada imediatamente (novo repositório ou branch).
4. **Corrija na saída, em ordem de dependência**: bibliotecas primeiro, depois os projetos que as consomem. Recompile, porque novos erros aparecem a cada camada, e use o relatório como checklist.
5. **Procure os `// TODO Migrator`** no `Program.cs` e revise a pasta `_Legacy/`. Apague-a quando terminar.
6. **Teste o comportamento**, não só a compilação: autenticação, sessão, uploads, serialização JSON (nomes de propriedades) e rotas.

> Atenção: `migrate --force` **apaga e regera** a pasta de saída. Não use `--force` sobre uma saída em que você já fez correções manuais; gere em outra pasta e compare.

---

## Decisões de projeto

- **Cópia, nunca in-place.** A saída fica fora da pasta de origem, permitindo comparar e repetir quantas vezes for preciso.
- **Preservar comportamento antes de modernizar.** Algumas escolhas priorizam "funcionar igual" e deixam a modernização como sugestão no inventário:
  - Web API continua com Newtonsoft.Json e nomes de propriedade sem camelCase, para não quebrar clientes;
  - views MVC mantêm PascalCase no `Json()`;
  - EF6 é mantido na versão 6.5, que roda no .NET 10, em vez de forçar EF Core;
  - `Encrypt=False` é adicionado às connection strings do SQL Server, porque o `Microsoft.Data.SqlClient` criptografa por padrão. Isso vale para a solução inteira, já que a connection string costuma ficar no projeto host;
  - o `App.config` é mantido em projetos não-web quando o código ainda usa `Settings.settings` ou seções customizadas;
  - `Nullable` e `ImplicitUsings` ficam desligados, para não gerar milhares de avisos e conflitos de nomes no código legado;
  - o `AssemblyInfo.cs` é preservado.
- **Não inventar código onde há risco.** `ConfigurationManager` é reescrito para `configuration[...]`, mas a injeção de `IConfiguration` fica para o desenvolvedor: o erro de compilação aponta exatamente onde. O mesmo vale para `HttpContext.Current` fora de controllers.
- **Compatibilidade verificada, não presumida.** A ferramenta consulta o conteúdo real dos pacotes no nuget.org e compila a saída.

---

## Limitações conhecidas

- **Projetos fora do escopo**: VB.NET, Web Sites sem `.csproj`, F# e projetos de banco de dados.
- **Sem conversão automática, só inventário**: WebForms, hospedagem WCF, ASP.NET Identity 2, SignalR clássico e servidor OAuth do OWIN. Todos aparecem com a alternativa sugerida.
- **Reescrita por padrões de texto**: parte das trocas de código é feita assim, e casos muito incomuns podem escapar. O build de verificação existe justamente para revelar o que sobrou.
- **Erros de compilação em camadas**: veja a etapa 11.
- **Feeds privados**: sem `NuGet.config` e credenciais, pacotes internos aparecem como NU1101 no build.
- **`COMReference`**: é mantido, mas exige o MSBuild do Visual Studio (MSB4803 no `dotnet build`).

---

## Referência da linha de comando

```
migrator analyze <entrada> [--report <pasta>] [--offline] [--cloud aws|none]
migrator migrate <entrada> [--output <pasta>] [--report <pasta>] [--offline] [--cloud aws|none] [--force] [--no-build] [--build-timeout <min>]
```

| Opção | Comando | Descrição |
|---|---|---|
| `<entrada>` | ambos | `.sln`, `.slnx`, `.csproj` ou pasta com projetos |
| `--output`, `-o` | migrate | Pasta de saída (padrão: `<pasta-pai>\<nome-da-solução>.net10`) |
| `--report`, `-r` | ambos | Pasta dos relatórios (padrão: `<saída>\_migration-report` no migrate; `<pasta-pai>\<nome>.migration-report` no analyze) |
| `--offline` | ambos | Não consulta o nuget.org |
| `--cloud` | ambos | Nuvem de destino da proposta de arquitetura e dos Dockerfiles: `aws` (padrão) ou `none` |
| `--force` | migrate | Substitui uma saída anterior do Migrator |
| `--no-build` | migrate | Pula o build de verificação |
| `--build-timeout` | migrate | Tempo máximo do build de verificação em minutos (padrão 30) |

Atrás de proxy corporativo, defina `HTTPS_PROXY` antes de executar. Se o nuget.org estiver inacessível, a ferramenta continua e avisa no relatório.

---

## Estrutura do código e como estender

```
src/Migrator.Core
  Analysis/     WorkspaceLoader (.sln/.slnx/pasta), ProjectLoader (.csproj), StartupAnalyzer (Global.asax/App_Start/OWIN), AssemblyInspector (DLLs),
                ApplicationProfiler (sinais de arquitetura: banco, arquivos, filas, SMTP, sessão, agendamento, Windows, segredos...)
  Cloud/        ModernizationAdvisor (pacotes + código + sinais → sugestões), AwsArchitect (hospedagem, serviços, Dockerfile, diagrama, plano)
  Data/         PackageRules, FrameworkReferenceRules, CodeRules (C# e Razor), BuildHints,
                ModernizationRules (pacotes: licença/descontinuados/equivalentes AWS), CodeModernizationRules (C# que compila mas muda)
  Migration/    MigrationEngine (orquestração), ProjectMigrator (por projeto), ControllerRewriter (Roslyn), CodeTransformer,
                RazorTransformer/BundleConfigParser, ConfigMigrator, PackagePlanner, PackageAligner, ProjectFileWriter,
                ProgramGenerator, BuildVerifier, TextFiles
  NuGet/        NuGetClient (versões, frameworks e dependências via API v3 do nuget.org)
  Reporting/    HtmlReport, MarkdownReport, ExcelReport, CsvReport
src/Migrator.Cli      comandos analyze/migrate (System.CommandLine + Spectre.Console)
tests/Migrator.Tests  testes unitários e de ponta a ponta sobre samples/LegacyShop
samples/LegacyShop    solução legada de exemplo
```

As regras ficam em `src/Migrator.Core/Data/`, e quase sempre basta acrescentar uma entrada nesses arquivos.

**Novo pacote** (`PackageRules.cs`, dicionário `ById`):

```csharp
["Empresa.Logging.Legado"] = Replace("A API mudou: use ILogger<T>.", To("Empresa.Logging", VersionPolicy.Latest("3.0.0"))),
["Empresa.Seguranca.Web"]  = Manual("Depende de System.Web; use a nova biblioteca Empresa.Seguranca.AspNetCore."),
```

**Novo padrão de código** (`CodeRules.cs`, lista `CSharp` ou `Razor`):

```csharp
new("EMP001", @"\bEmpresa\.Cache\.Get\(", InventorySeverity.Warning,
    "Cache corporativo antigo",
    "A biblioteca Empresa.Cache não tem versão para .NET 10.",
    "Use IDistributedCache com a configuração padrão da empresa."),
```

**Dica para erro de compilação** (`BuildHints.cs`): acrescente o tipo em `TypeHints`, o membro em `MemberHints` ou o código do erro em `CodeHints`.

**Nova sugestão de modernização por pacote** (`ModernizationRules.cs`, dicionário `ById` ou lista `ByPrefix`):

```csharp
["Empresa.Pdf.Legado"] = Deprecated("MOD-PKG-EMPRESA-PDF", Impact.Medium, Effort.Medium,
    "Empresa.Pdf.Legado não tem versão para .NET 10",
    "Depende de binários nativos Windows.",
    "Use QuestPDF; para HTML→PDF, PuppeteerSharp em uma tarefa ECS."),
```

**Novo padrão de código C# que compila mas muda** (`CodeModernizationRules.cs`, lista `CSharp`): mesma forma das `CodeRules`, com `Kind`, `Impact`, `Effort`, filtro opcional por tipo de projeto (`OnlyKinds`) e `AwsService`.

**Novo sinal de arquitetura** (`ApplicationProfiler.cs`): acrescente o valor no enum `Signal` e a regex em `CodeProbes` (ou o prefixo de pacote em `PackageProbes` / a referência em `FrameworkReferenceProbes`); depois use `profile.Has(Signal.X)` em `ModernizationAdvisor.FromProfile` (sugestão) e/ou em `AwsArchitect` (hospedagem/componente).

**Testes**: `dotnet test Migrator.slnx`. Ao adicionar uma regra, inclua um caso em `tests/Migrator.Tests` e, se for um padrão comum, reproduza-o em `samples/LegacyShop`.
