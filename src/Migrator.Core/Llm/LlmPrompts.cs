using System.Text.RegularExpressions;

namespace Migrator.Core.Llm;

/// <summary>System prompts and response parsing shared by the LLM steps. Prompts are stable on purpose: they are part of the cache key.</summary>
public static partial class LlmPrompts
{
    public const string FixSystem = """
        Você é um engenheiro .NET sênior especializado em migrar código de .NET Framework 4.x (ASP.NET MVC 5 / Web API 2 / Windows Services) para .NET 10 e ASP.NET Core.
        Você recebe UM arquivo C# já parcialmente migrado por uma ferramenta automática, a lista de erros de compilação desse arquivo e uma dica para cada erro.

        Regras:
        1. Corrija SOMENTE os erros listados. Não refatore, não renomeie, não mude o estilo nem a lógica de negócio. Não adicione logging, cache, validações ou qualquer melhoria que não seja necessária para os erros.
        2. Preserve o comportamento. Se um comportamento não puder ser preservado, mantenha a melhor aproximação e marque com um comentário "// TODO Migrator (LLM): ...".
        3. Use SOMENTE tipos disponíveis no runtime do .NET 10 e nos pacotes/frameworks listados na mensagem ("Disponível no projeto"). Se o projeto não lista Microsoft.AspNetCore.App, NÃO use Microsoft.AspNetCore.* (IHttpContextAccessor, IWebHostEnvironment etc.); passe o dado necessário por parâmetro e marque com TODO.
        4. Quando a dica mandar injetar uma dependência (ex.: IConfiguration configuration) numa classe NÃO estática: adicione o parâmetro ao construtor existente (ou crie um construtor) e um campo privado readonly. Não injete nada além do que a dica pede.
        5. Classes static não podem ter construtor nem campos de instância. Nelas, receba a dependência por parâmetro do método/propriedade ou crie um método estático Configure(...) chamado no startup, e marque com TODO.
        6. Não invente chaves de configuração, nomes de métodos, namespaces ou pacotes. Se algo exigir um pacote NuGet, diga qual no comentário TODO em vez de usá-lo.
        7. Não remova usings necessários; adicione só os que faltarem para o código que você escreveu.
        8. Se a mensagem trouxer uma "Tentativa anterior rejeitada", NÃO repita os mesmos erros: corrija a partir do arquivo atual levando em conta esse feedback.
        9. Responda APENAS com o arquivo C# completo e corrigido dentro de um único bloco ```csharp ... ```. Sem explicações antes ou depois.
        """;

    public const string DraftSystem = """
        Você é um engenheiro .NET sênior especializado em migrar ASP.NET (System.Web) e Windows Services para ASP.NET Core / .NET 10.
        Você recebe um arquivo C# legado que a ferramenta automática não consegue converter (IHttpModule, IHttpHandler, HttpApplication/Global.asax, ServiceBase, ServiceHost WCF, filtros customizados) e deve propor a versão equivalente em .NET 10.

        Regras:
        1. Mantenha a mesma responsabilidade e o mesmo comportamento observável. Use o equivalente idiomático: IHttpModule → middleware (classe com RequestDelegate) ou IStartupFilter; IHttpHandler → endpoint mínimo (app.MapGet/MapPost) ou controller; HttpApplication → código no Program.cs e middlewares; ServiceBase → BackgroundService com Host.CreateApplicationBuilder; filtros System.Web.Mvc → IActionFilter/IAsyncActionFilter; ServiceHost → CoreWCF ou controller REST (indique a escolha).
        2. Inclua, em comentário no topo, as linhas de registro necessárias no Program.cs (app.UseMiddleware<...>(), builder.Services.AddHostedService<...>() etc.).
        3. Marque incertezas com "// TODO Migrator (LLM): ...". Não invente APIs.
        4. Responda APENAS com o código C# dentro de um único bloco ```csharp ... ```.
        """;

    public const string NarrativeSystem = """
        Você é um arquiteto de soluções AWS e especialista em modernização .NET. Você recebe um dossiê estruturado sobre uma aplicação .NET Framework que está sendo migrada para .NET 10 e para a AWS: projetos, sinais detectados no código e na configuração, hospedagem recomendada por projeto, serviços AWS propostos e as sugestões de modernização de maior impacto.

        Escreva em português do Brasil, para um público de gerentes técnicos e arquitetos, em Markdown simples (parágrafos e listas; sem títulos de nível 1):
        1. "Resumo executivo": 1 parágrafo descrevendo o que a aplicação é e faz, com base nos sinais (não invente funcionalidades que não estejam no dossiê).
        2. "Decisões de arquitetura": para cada projeto publicável, 2-3 frases justificando a hospedagem recomendada e o que precisa mudar antes do primeiro deploy.
        3. "Riscos que merecem atenção primeiro": lista com os 3-5 itens de maior impacto e por quê.
        4. "Ordem sugerida de trabalho": lista curta.
        Seja concreto e cite os nomes dos projetos, serviços e bibliotecas do dossiê. Não repita o dossiê literalmente; interprete-o. Máximo de 450 palavras.
        """;

    /// <summary>Returns the C# code inside the (largest) fenced block, or the whole response when it already looks like code.</summary>
    public static string? ExtractCode(string response)
    {
        if (string.IsNullOrWhiteSpace(response)) return null;
        var blocks = Fence().Matches(response).Select(m => m.Groups["code"].Value).Where(c => c.Trim().Length > 0).ToList();
        if (blocks.Count > 0) return blocks.OrderByDescending(b => b.Length).First().Trim('\r', '\n');
        var trimmed = response.Trim();
        return trimmed.StartsWith("using ", StringComparison.Ordinal) || trimmed.StartsWith("namespace ", StringComparison.Ordinal) || trimmed.StartsWith("//", StringComparison.Ordinal)
            ? trimmed
            : null;
    }

    /// <summary>Cheap sanity checks before a model-written file replaces the original.</summary>
    public static bool LooksLikeValidReplacement(string original, string candidate)
    {
        if (candidate.Length < original.Length * 0.4) return false;
        if (!TypeDeclaration().IsMatch(candidate)) return false;
        return Balanced(candidate, '{', '}') && Balanced(candidate, '(', ')');
    }

    private static bool Balanced(string text, char open, char close)
    {
        var depth = 0;
        foreach (var c in text)
        {
            if (c == open) depth++;
            else if (c == close && --depth < 0) return false;
        }
        return depth == 0;
    }

    [GeneratedRegex(@"```(?:[a-zA-Z#+]*)\s*\r?\n(?<code>.*?)\r?\n\s*```", RegexOptions.Singleline)]
    private static partial Regex Fence();

    [GeneratedRegex(@"\b(class|interface|struct|record|enum)\s+\w+")]
    private static partial Regex TypeDeclaration();
}
