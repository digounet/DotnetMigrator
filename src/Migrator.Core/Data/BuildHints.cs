using System.Text.RegularExpressions;

namespace Migrator.Core.Data;

public static partial class BuildHints
{
    private static readonly Dictionary<string, string> TypeHints = new(StringComparer.Ordinal)
    {
        ["HttpContextBase"] = "Use Microsoft.AspNetCore.Http.HttpContext.",
        ["HttpRequestBase"] = "Use Microsoft.AspNetCore.Http.HttpRequest.",
        ["HttpResponseBase"] = "Use Microsoft.AspNetCore.Http.HttpResponse.",
        ["HttpSessionStateBase"] = "Use ISession (HttpContext.Session) com SetString/GetString.",
        ["HttpSessionState"] = "Use ISession (HttpContext.Session) com SetString/GetString.",
        ["HttpServerUtility"] = "Server.MapPath → IWebHostEnvironment.ContentRootPath/WebRootPath; Server.HtmlEncode → System.Net.WebUtility.HtmlEncode.",
        ["HttpCookie"] = "Use Response.Cookies.Append(nome, valor, new CookieOptions { ... }).",
        ["HttpPostedFile"] = "Use IFormFile (Length, OpenReadStream(), CopyToAsync()).",
        ["HttpApplication"] = "Mova a lógica do Global.asax para Program.cs / middlewares.",
        ["HttpRuntime"] = "HttpRuntime.Cache → IMemoryCache; HttpRuntime.AppDomainAppPath → IWebHostEnvironment.ContentRootPath.",
        ["HostingEnvironment"] = "Use IWebHostEnvironment (MapPath → Path.Combine(env.ContentRootPath, ...)); QueueBackgroundWorkItem → BackgroundService.",
        ["HttpResponseException"] = "Retorne IActionResult (NotFound(), BadRequest(), Problem()) ou trate exceções com IExceptionHandler.",
        ["HttpConfiguration"] = "A configuração do Web API vai para o Program.cs (builder.Services.AddControllers()).",
        ["HttpActionContext"] = "Filtros usam ActionExecutingContext (Microsoft.AspNetCore.Mvc.Filters).",
        ["HttpActionExecutedContext"] = "Filtros usam ActionExecutedContext (Microsoft.AspNetCore.Mvc.Filters).",
        ["AuthorizationContext"] = "Filtros de autorização usam AuthorizationFilterContext.",
        ["ActionDescriptor"] = "Use Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor / ControllerActionDescriptor.",
        ["HandleErrorAttribute"] = "Use app.UseExceptionHandler(...) ou um IExceptionFilter.",
        ["OutputCacheAttribute"] = "Use [ResponseCache] ou Output Caching (AddOutputCache/UseOutputCache + [OutputCache] de Microsoft.AspNetCore.OutputCaching).",
        ["ChildActionOnlyAttribute"] = "Converta a child action em ViewComponent.",
        ["ValidateInputAttribute"] = "Remova: o ASP.NET Core não faz request validation de HTML.",
        ["AllowHtmlAttribute"] = "Remova: o ASP.NET Core não faz request validation de HTML.",
        ["JsonRequestBehavior"] = "Remova o argumento: Json(x) no ASP.NET Core não restringe GET.",
        ["MvcHtmlString"] = "Use HtmlString / IHtmlContent (Microsoft.AspNetCore.Html).",
        ["HtmlHelper"] = "Use IHtmlHelper (Microsoft.AspNetCore.Mvc.Rendering); extensões de helper recebem 'this IHtmlHelper'.",
        ["UrlHelper"] = "Use IUrlHelper / LinkGenerator.",
        ["WebViewPage"] = "Use RazorPage<TModel>.",
        ["ViewEngines"] = "Configure Razor com builder.Services.Configure<RazorViewEngineOptions>(...).",
        ["BundleCollection"] = "Bundling do System.Web não existe; use WebOptimizer/Vite ou referências diretas.",
        ["BundleTable"] = "Bundling do System.Web não existe; use WebOptimizer/Vite ou referências diretas.",
        ["RouteCollection"] = "Rotas são registradas no Program.cs (app.MapControllerRoute).",
        ["RouteTable"] = "Rotas são registradas no Program.cs (app.MapControllerRoute).",
        ["UrlParameter"] = "Parâmetros opcionais ficam no template: {id?}.",
        ["RouteParameter"] = "Parâmetros opcionais ficam no template: {id?}.",
        ["GlobalFilterCollection"] = "Filtros globais: builder.Services.AddControllers(o => o.Filters.Add(...)).",
        ["GlobalConfiguration"] = "A configuração do Web API vai para o Program.cs.",
        ["FormsAuthentication"] = "Use HttpContext.SignInAsync/SignOutAsync com cookie authentication.",
        ["FormsAuthenticationTicket"] = "Use ClaimsPrincipal + AuthenticationProperties.",
        ["Membership"] = "Use ASP.NET Core Identity (UserManager).",
        ["MembershipUser"] = "Use ASP.NET Core Identity (IdentityUser).",
        ["Roles"] = "Use ASP.NET Core Identity (RoleManager) ou claims de role.",
        ["JavaScriptSerializer"] = "Use System.Text.Json.JsonSerializer.",
        ["WebGrid"] = "Sem equivalente; renderize a tabela manualmente ou use um componente de grid.",
        ["IHttpModule"] = "Reescreva como middleware.",
        ["IHttpHandler"] = "Reescreva como endpoint (MapGet/MapPost) ou middleware.",
        ["IOwinContext"] = "Use HttpContext.",
        ["IAppBuilder"] = "Use WebApplication (Program.cs).",
        ["IAuthenticationManager"] = "Use HttpContext.SignInAsync/SignOutAsync e SignInManager<TUser>.",
        ["OwinStartupAttribute"] = "Remova; o Startup OWIN foi movido para _Legacy e deve ser portado para o Program.cs.",
        ["Installer"] = "System.Configuration.Install não existe; registre o serviço com sc.exe/New-Service.",
        ["RunInstallerAttribute"] = "System.Configuration.Install não existe; registre o serviço com sc.exe/New-Service.",
        ["ServiceHost"] = "Hospede o serviço com CoreWCF ou reescreva como gRPC/Web API.",
        ["SoapHttpClientProtocol"] = "Regenere o proxy com dotnet-svcutil.",
        ["ObjectContext"] = "Gere um DbContext Code First (EF6 ou EF Core).",
        ["IUnityContainer"] = "Use IServiceCollection/IServiceProvider (DI nativo).",
        ["UnityContainer"] = "Use IServiceCollection/IServiceProvider (DI nativo).",
        ["IKernel"] = "Use IServiceCollection/IServiceProvider (DI nativo).",
        ["StandardKernel"] = "Use IServiceCollection/IServiceProvider (DI nativo).",
        ["ProtectedData"] = "Adicione o pacote System.Security.Cryptography.ProtectedData (somente Windows) ou use ASP.NET Core Data Protection.",
        ["SignedXml"] = "Adicione o pacote System.Security.Cryptography.Xml.",
        ["SignedCms"] = "Adicione o pacote System.Security.Cryptography.Pkcs.",
        ["SqlConnection"] = "Adicione using Microsoft.Data.SqlClient (o pacote Microsoft.Data.SqlClient foi incluído quando detectado).",
        ["ConfigurationManager"] = "Use IConfiguration (ou adicione o pacote System.Configuration.ConfigurationManager como compatibilidade temporária).",
        ["WebConfigurationManager"] = "Use IConfiguration.",
        ["HttpUtility"] = "HttpUtility existe em System.Web no .NET 10: adicione 'using System.Web;'.",
        ["BinaryFormatter"] = "Removido no .NET 9+. Use System.Text.Json / MessagePack / protobuf-net.",
        ["EnableCorsAttribute"] = "Use [EnableCors(\"NomeDaPolicy\")] de Microsoft.AspNetCore.Cors.",
        ["ResponseTypeAttribute"] = "Use [ProducesResponseType(typeof(T), 200)].",
        ["IHttpActionResult"] = "Use IActionResult.",
        ["ApiController"] = "Use ControllerBase com [ApiController].",
        ["ModelBinderAttribute"] = "Use [ModelBinder(typeof(T))] de Microsoft.AspNetCore.Mvc.",
        ["DefaultModelBinder"] = "Implemente IModelBinder (BindModelAsync) do ASP.NET Core.",
    };

    private static readonly Dictionary<string, string> MemberHints = new(StringComparer.Ordinal)
    {
        ["Current"] = "HttpContext.Current não existe: use a propriedade HttpContext (controllers) ou IHttpContextAccessor.",
        ["MapPath"] = "Use IWebHostEnvironment.ContentRootPath/WebRootPath com Path.Combine.",
        ["ContentLength"] = "Em IFormFile use Length.",
        ["InputStream"] = "Em IFormFile use OpenReadStream().",
        ["SaveAs"] = "Em IFormFile use: using var fs = File.Create(caminho); await arquivo.CopyToAsync(fs);",
        ["UrlReferrer"] = "Use Request.Headers.Referer.",
        ["UserAgent"] = "Use Request.Headers.UserAgent.",
        ["UserHostAddress"] = "Use HttpContext.Connection.RemoteIpAddress.",
        ["RawUrl"] = "Use Request.Path + Request.QueryString (ou Request.GetEncodedPathAndQuery()).",
        ["Url"] = "Use Request.GetDisplayUrl() (Microsoft.AspNetCore.Http.Extensions).",
        ["IsAjaxRequest"] = "Use Request.Headers.XRequestedWith == \"XMLHttpRequest\".",
        ["QueryString"] = "Use Request.Query[\"chave\"].",
        ["ServerVariables"] = "Use HttpContext.Connection / Request.Headers / HttpContext.Features.",
        ["Abandon"] = "Use HttpContext.Session.Clear().",
        ["ReadAsAsync"] = "Use ReadFromJsonAsync<T>() (System.Net.Http.Json) ou mantenha Microsoft.AspNet.WebApi.Client.",
        ["CreateResponse"] = "Retorne IActionResult (Ok(), StatusCode(...)).",
        ["CreateErrorResponse"] = "Retorne IActionResult (BadRequest(), Problem(...)).",
        ["GetOwinContext"] = "Use HttpContext / serviços injetados.",
        ["GetUserId"] = "Use User.FindFirstValue(ClaimTypes.NameIdentifier).",
        ["IsAuthenticated"] = "Use User.Identity.IsAuthenticated.",
        ["AddHeader"] = "Use Response.Headers.Append(nome, valor).",
        ["Write"] = "Use await Response.WriteAsync(...) ou retorne Content(...).",
        ["End"] = "Response.End não existe; apenas retorne da action.",
        ["Redirect"] = "Use return Redirect(url) / RedirectToAction(...).",
        ["Files"] = "Use Request.Form.Files.",
        ["Browser"] = "Sem equivalente; analise o User-Agent manualmente.",
        ["StatusDescription"] = "Use HttpContext.Features.Get<IHttpResponseFeature>().ReasonPhrase.",
        ["IsLocal"] = "Compare Connection.RemoteIpAddress com Connection.LocalIpAddress / IPAddress.IsLoopback.",
    };

    private static readonly Dictionary<string, string> CodeHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CS0104"] = "Nome ambíguo entre namespaces (comum entre System.Configuration e Microsoft.Extensions.Configuration): qualifique o tipo ou remova o using desnecessário.",
        ["CS0115"] = "A assinatura do método virtual mudou no ASP.NET Core (ex.: filtros usam ActionExecutingContext; Dispose de controllers é público).",
        ["CS0012"] = "Uma DLL/pacote compilado para .NET Framework depende de um assembly que não existe no .NET 10 (ex.: System.Web). Obtenha uma versão moderna da dependência.",
        ["CS0579"] = "Atributo de assembly duplicado: remova o atributo do AssemblyInfo.cs ou defina <GenerateAssemblyInfo>false</GenerateAssemblyInfo>.",
        ["CS8357"] = "Versão com curinga (1.0.*) exige <Deterministic>false</Deterministic>.",
        ["CS1503"] = "Tipo de argumento mudou (ex.: StatusCode recebe int; use (int)HttpStatusCode.X).",
        ["CS0029"] = "Conversão de tipo inválida: a API equivalente do .NET 10 retorna outro tipo (ex.: Request.Query retorna StringValues).",
        ["CS0266"] = "Conversão de tipo inválida: a API equivalente do .NET 10 retorna outro tipo.",
        ["CS0619"] = "API marcada como obsoleta com erro no .NET 10; substitua conforme a mensagem.",
        ["CS0618"] = "API obsoleta; substitua conforme a mensagem.",
        ["SYSLIB0011"] = "BinaryFormatter foi removido. Use System.Text.Json / MessagePack / protobuf-net.",
        ["SYSLIB0014"] = "WebRequest/WebClient/ServicePoint obsoletos: use HttpClient (IHttpClientFactory).",
        ["SYSLIB0006"] = "Thread.Abort não é suportado: use CancellationToken.",
        ["SYSLIB0003"] = "Code Access Security não existe: remova os atributos/permissões.",
        ["SYSLIB0012"] = "Assembly.CodeBase obsoleto: use Assembly.Location ou AppContext.BaseDirectory.",
        ["SYSLIB0021"] = "Tipos criptográficos derivados obsoletos (SHA256Managed...): use SHA256.Create() / SHA256.HashData().",
        ["SYSLIB0022"] = "Rijndael obsoleto: use Aes.Create().",
        ["SYSLIB0023"] = "RNGCryptoServiceProvider obsoleto: use RandomNumberGenerator.",
        ["SYSLIB0041"] = "Construtor de Rfc2898DeriveBytes obsoleto: use Rfc2898DeriveBytes.Pbkdf2(...) com SHA256.",
        ["SYSLIB0050"] = "Serialização baseada em formatter obsoleta: remova [Serializable]/ISerializable se não for usada.",
        ["SYSLIB0051"] = "Construtor de serialização legado obsoleto: remova o construtor (SerializationInfo, StreamingContext) e GetObjectData.",
        ["CA1416"] = "API disponível apenas no Windows: mude o TargetFramework para net10.0-windows ou marque o código com [SupportedOSPlatform(\"windows\")].",
        ["NU1701"] = "O pacote só tem binários para .NET Framework e foi restaurado em modo de compatibilidade: pode falhar em tempo de execução. Procure uma versão com suporte a netstandard2.0/net8+ ou um substituto.",
        ["NU1101"] = "Pacote não encontrado nas fontes NuGet configuradas. Se for um feed privado, confira o NuGet.config e as credenciais.",
        ["NU1102"] = "Versão do pacote não encontrada nas fontes configuradas.",
        ["NU1103"] = "Só existe versão prerelease desse pacote nas fontes configuradas.",
        ["NU1605"] = "Downgrade de pacote detectado: alinhe a versão referenciada diretamente com a exigida pelas dependências.",
        ["NU1608"] = "Dependência fora do intervalo suportado: atualize o pacote que impõe a restrição.",
        ["NU1901"] = "Pacote com vulnerabilidade conhecida (baixa): atualize para uma versão corrigida.",
        ["NU1902"] = "Pacote com vulnerabilidade conhecida (moderada): atualize para uma versão corrigida.",
        ["NU1903"] = "Pacote com vulnerabilidade conhecida (alta): atualize para uma versão corrigida.",
        ["NU1904"] = "Pacote com vulnerabilidade conhecida (crítica): atualize imediatamente.",
        ["MSB4803"] = "<COMReference> não é suportado pelo 'dotnet build': compile com o MSBuild do Visual Studio ou gere o interop com tlbimp e referencie a DLL.",
        ["MSB3277"] = "Conflito de versões de assembly: alinhe as versões dos pacotes.",
        ["MSB3073"] = "Um comando de build event/target falhou. Confira caminhos e macros (a pasta de saída agora é bin\\<Config>\\net10.0\\; $(SolutionDir) só existe ao compilar pela solução, por isso a ferramenta define um fallback).",
        ["RZ1002"] = "A diretiva @helper não existe no Razor do ASP.NET Core: use partial view, Tag Helper ou função local (@{ void Nome() { ... } }).",
        ["NETSDK1022"] = "Itens duplicados: remova entradas explícitas que já são incluídas pelos globs do SDK.",
    };

    public static string Suggest(string code, string message)
    {
        if (code is "CS0246" or "CS0234" or "CS0103" or "CS0117" or "CS1061" or "CS0426" or "CS0616" or "CS1069")
        {
            foreach (Match m in Quoted().Matches(message))
            {
                var name = m.Groups[1].Value;
                var last = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
                if (TypeHints.TryGetValue(last, out var typeHint)) return typeHint;
                if (TypeHints.TryGetValue(last + "Attribute", out var attrHint)) return attrHint;
            }
            if (code is "CS1061" or "CS0117")
            {
                var names = Quoted().Matches(message).Select(m => m.Groups[1].Value).ToList();
                if (names.Count >= 2 && MemberHints.TryGetValue(names[1], out var memberHint)) return memberHint;
            }
            if (message.Contains("System.Web", StringComparison.Ordinal))
                return "Tipo/namespace do System.Web: não existe no .NET 10. Localize o equivalente em Microsoft.AspNetCore.* ou reescreva o trecho.";
            return code switch
            {
                "CS0246" or "CS0234" => "Tipo ou namespace inexistente no .NET 10: verifique se falta um pacote NuGet, um 'using' do equivalente ASP.NET Core ou se a API foi descontinuada.",
                "CS1061" or "CS0117" => "O membro não existe no tipo equivalente do .NET 10; consulte a API nova (frequentemente assíncrona ou movida para outra classe).",
                _ => "Símbolo inexistente no .NET 10; verifique o equivalente moderno."
            };
        }

        if (CodeHints.TryGetValue(code, out var hint)) return hint;
        if (code.StartsWith("RZ", StringComparison.OrdinalIgnoreCase)) return "Erro de compilação Razor: a sintaxe/helper da view não existe no ASP.NET Core.";
        if (code.StartsWith("NU", StringComparison.OrdinalIgnoreCase)) return "Problema de restauração de pacotes NuGet: revise a referência indicada.";
        return "Corrija conforme a mensagem do compilador.";
    }

    [GeneratedRegex(@"['‘’""“”]([\w.<>`]+)['‘’""“”]")]
    private static partial Regex Quoted();
}
