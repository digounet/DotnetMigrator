using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Core.Migration;

public sealed class ControllerCatalog
{
    public HashSet<string> Api { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Mvc { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ApiRoots { get; } = new(StringComparer.Ordinal);

    public static ControllerCatalog Build(IEnumerable<string> sources)
    {
        var bases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();
            foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                if (cls.BaseList?.Types.FirstOrDefault() is { } first)
                    bases.TryAdd(cls.Identifier.Text, ControllerRewriter.SimpleName(first.Type));
        }

        var catalog = new ControllerCatalog();
        foreach (var cls in bases.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = cls;
            while (bases.TryGetValue(current, out var parent) && visited.Add(current))
            {
                if (parent == "ApiController")
                {
                    catalog.Api.Add(cls);
                    catalog.ApiRoots.Add(current);
                    break;
                }
                if (parent is "Controller" or "AsyncController")
                {
                    catalog.Mvc.Add(cls);
                    break;
                }
                current = parent;
            }
        }
        return catalog;
    }
}

public sealed record RouteWarning(string Controller, string Verb, string Template, IReadOnlyList<string> Actions);

public sealed class ControllerRewriter : CSharpSyntaxRewriter
{
    private enum ClassKind { None, Api, Mvc }

    private static readonly string[] Verbs = ["Get", "Post", "Put", "Delete", "Patch", "Head", "Options"];
    private static readonly HashSet<string> VerbAttributes = new(StringComparer.Ordinal)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions", "AcceptVerbs"
    };

    private readonly ControllerCatalog _catalog;
    private readonly string _apiTemplate;
    private readonly string? _area;
    private readonly string _eol;
    private readonly Stack<ClassKind> _classes = new();
    private readonly Dictionary<(string Id, string Description), int> _changes = new();

    public List<RouteWarning> RouteWarnings { get; } = [];
    public List<string> UnconvertedHttpResponseMessage { get; } = [];

    private ControllerRewriter(ControllerCatalog catalog, string apiTemplate, string? area, string eol)
    {
        _catalog = catalog;
        _apiTemplate = apiTemplate;
        _area = area;
        _eol = eol;
    }

    public static (string Text, List<CodeChange> Changes, ControllerRewriter Rewriter) Rewrite(
        string source, ControllerCatalog catalog, string apiTemplate, string? area)
    {
        var rewriter = new ControllerRewriter(catalog, apiTemplate, area, source.Contains("\r\n") ? "\r\n" : "\n");
        if (catalog.Api.Count == 0 && catalog.Mvc.Count == 0)
            return (source, [], rewriter);

        var tree = CSharpSyntaxTree.ParseText(source);
        var newRoot = rewriter.Visit(tree.GetRoot());
        var changes = rewriter._changes.Select(kv => new CodeChange(kv.Key.Id, kv.Key.Description, kv.Value)).ToList();
        return (changes.Count == 0 ? source : newRoot.ToFullString(), changes, rewriter);
    }

    private ClassKind Current => _classes.Count == 0 ? ClassKind.None : _classes.Peek();

    private void Record(string id, string description) =>
        _changes[(id, description)] = _changes.GetValueOrDefault((id, description)) + 1;

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var name = node.Identifier.Text;
        var kind = _catalog.Api.Contains(name) ? ClassKind.Api : _catalog.Mvc.Contains(name) ? ClassKind.Mvc : ClassKind.None;

        _classes.Push(kind);
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        _classes.Pop();

        if (kind == ClassKind.None) return visited;
        if (kind == ClassKind.Api) visited = RewriteApiController(visited);

        var isAbstract = visited.Modifiers.Any(SyntaxKind.AbstractKeyword);
        if (_area != null && !isAbstract && !HasAttribute(visited, "Area"))
        {
            visited = AddAttribute(visited, $"Area(\"{_area}\")");
            Record("CS-AREA", "[Area] adicionado aos controllers de Areas/");
        }
        return visited;
    }

    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        if (Current != ClassKind.None && node.Name.Identifier.Text == "Current" &&
            node.Expression.ToString() is "HttpContext" or "System.Web.HttpContext")
        {
            Record("CS-HTTPCONTEXT", "HttpContext.Current → HttpContext (propriedade do controller)");
            return SyntaxFactory.IdentifierName("HttpContext").WithTriviaFrom(node);
        }
        return base.VisitMemberAccessExpression(node);
    }

    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;
        if (Current == ClassKind.Api && visited.Expression is IdentifierNameSyntax { Identifier.Text: "Json" })
        {
            Record("CS-WEBAPI-JSON", "Json(x) em ApiController → new JsonResult(x)");
            return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.Token(SyntaxKind.NewKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.IdentifierName("JsonResult"),
                    visited.ArgumentList,
                    null)
                .WithTriviaFrom(visited);
        }
        return visited;
    }

    private ClassDeclarationSyntax RewriteApiController(ClassDeclarationSyntax cls)
    {
        if (cls.BaseList != null)
        {
            var types = cls.BaseList.Types;
            for (var i = 0; i < types.Count; i++)
            {
                if (SimpleName(types[i].Type) != "ApiController") continue;
                types = types.Replace(types[i], SyntaxFactory.SimpleBaseType(SyntaxFactory.IdentifierName("ControllerBase")).WithTriviaFrom(types[i]));
                cls = cls.WithBaseList(cls.BaseList.WithTypes(types));
                Record("CS-WEBAPI-BASE", "ApiController → ControllerBase");
                break;
            }
        }

        if (_catalog.ApiRoots.Contains(cls.Identifier.Text) && !HasAttribute(cls, "ApiController"))
        {
            cls = AddAttribute(cls, "ApiController");
            Record("CS-WEBAPI-ATTR", "[ApiController] adicionado (preserva o binding do Web API: tipos complexos via corpo JSON)");
        }

        var isAbstract = cls.Modifiers.Any(SyntaxKind.AbstractKeyword);
        var classHasRoute = HasAttribute(cls, "Route") || HasAttribute(cls, "RoutePrefix");
        var actions = cls.Members.OfType<MethodDeclarationSyntax>().Where(IsAction).ToList();
        var methodsHaveRoute = actions.Any(m => HasAttribute(m, "Route"));

        var addClassRoute = !isAbstract && !classHasRoute && !methodsHaveRoute;
        if (addClassRoute)
        {
            cls = AddAttribute(cls, $"Route(\"{_apiTemplate}\")");
            Record("CS-WEBAPI-ROUTE", "[Route] adicionado a partir do template do WebApiConfig (roteamento por convenção não se aplica a [ApiController])");
        }
        var relative = classHasRoute || addClassRoute;

        var routes = new Dictionary<(string Verb, string Template), List<string>>();
        var members = cls.Members.Select(member =>
        {
            if (member is not MethodDeclarationSyntax method || !IsAction(method)) return member;
            return RewriteAction(method, relative, routes);
        }).ToList();
        cls = cls.WithMembers(SyntaxFactory.List(members));

        foreach (var ((verb, template), names) in routes.Where(r => r.Value.Count > 1))
            RouteWarnings.Add(new RouteWarning(cls.Identifier.Text, verb, template, names));

        return cls;
    }

    private MemberDeclarationSyntax RewriteAction(MethodDeclarationSyntax method, bool relative, Dictionary<(string, string), List<string>> routes)
    {
        var hasRoute = HasAttribute(method, "Route");
        var hasId = method.ParameterList.Parameters.Any(p =>
            p.Identifier.Text.Equals("id", StringComparison.OrdinalIgnoreCase) &&
            !p.AttributeLists.SelectMany(a => a.Attributes).Any(a => SimpleName(a.Name) is "FromBody" or "FromBodyAttribute"));

        string? template = hasRoute ? null
            : relative ? (hasId ? "{id}" : null)
            : (hasId ? _apiTemplate + "/{id}" : _apiTemplate);

        var verbAttribute = method.AttributeLists.SelectMany(a => a.Attributes)
            .FirstOrDefault(a => VerbAttributes.Contains(Normalize(SimpleName(a.Name))));

        string verb;
        if (verbAttribute == null)
        {
            verb = Verbs.FirstOrDefault(v => method.Identifier.Text.StartsWith(v, StringComparison.OrdinalIgnoreCase)) ?? "Post";
            method = AddAttribute(method, template == null ? $"Http{verb}" : $"Http{verb}(\"{template}\")");
            Record("CS-WEBAPI-VERB", "Verbo HTTP explicitado ([HttpGet]/[HttpPost]...) conforme a convenção de nomes do Web API");
        }
        else
        {
            verb = Normalize(SimpleName(verbAttribute.Name)).Replace("Http", "");
            if (template != null && verbAttribute.ArgumentList == null)
            {
                method = method.ReplaceNode(verbAttribute, verbAttribute.WithArgumentList(SyntaxFactory.ParseAttributeArgumentList($"(\"{template}\")")));
                Record("CS-WEBAPI-VERB", "Template de rota adicionado ao atributo de verbo HTTP");
            }
        }

        method = RewriteReturnType(method);

        var routeTemplate = template ?? RouteAttributeTemplate(method) ?? "";
        var key = (verb.ToUpperInvariant(), routeTemplate);
        if (!routes.TryGetValue(key, out var list)) routes[key] = list = [];
        list.Add(method.Identifier.Text);

        return method;
    }

    private MethodDeclarationSyntax RewriteReturnType(MethodDeclarationSyntax method)
    {
        var body = method.Body?.ToString() ?? method.ExpressionBody?.ToString() ?? "";
        var returnsMessage = method.ReturnType switch
        {
            IdentifierNameSyntax { Identifier.Text: "HttpResponseMessage" } => true,
            GenericNameSyntax { Identifier.Text: "Task" } g when g.TypeArgumentList.Arguments.Count == 1 &&
                                                               g.TypeArgumentList.Arguments[0] is IdentifierNameSyntax { Identifier.Text: "HttpResponseMessage" } => true,
            _ => false
        };
        if (!returnsMessage) return method;

        if (body.Contains("HttpResponseMessage", StringComparison.Ordinal))
        {
            UnconvertedHttpResponseMessage.Add(method.Identifier.Text);
            return method;
        }

        TypeSyntax newType = method.ReturnType is GenericNameSyntax
            ? SyntaxFactory.ParseTypeName("Task<IActionResult>")
            : SyntaxFactory.IdentifierName("IActionResult");
        Record("CS-WEBAPI-RETURN", "Retorno HttpResponseMessage → IActionResult");
        return method.WithReturnType(newType.WithTriviaFrom(method.ReturnType));
    }

    private static bool IsAction(MethodDeclarationSyntax m) =>
        m.Modifiers.Any(SyntaxKind.PublicKeyword) &&
        !m.Modifiers.Any(SyntaxKind.StaticKeyword) &&
        !m.Modifiers.Any(SyntaxKind.AbstractKeyword) &&
        !m.Modifiers.Any(SyntaxKind.OverrideKeyword) &&
        !HasAttribute(m, "NonAction") &&
        m.Identifier.Text != "Dispose";

    private static string? RouteAttributeTemplate(MethodDeclarationSyntax m) =>
        m.AttributeLists.SelectMany(a => a.Attributes)
            .FirstOrDefault(a => Normalize(SimpleName(a.Name)) == "Route")?
            .ArgumentList?.Arguments.FirstOrDefault()?.Expression.ToString().Trim('"');

    private static bool HasAttribute(MemberDeclarationSyntax member, string name) =>
        member.AttributeLists.SelectMany(a => a.Attributes).Any(a => Normalize(SimpleName(a.Name)) == name);

    private static string Normalize(string attributeName) =>
        attributeName.EndsWith("Attribute", StringComparison.Ordinal) ? attributeName[..^"Attribute".Length] : attributeName;

    public static string SimpleName(SyntaxNode type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => SimpleName(q.Right),
        AliasQualifiedNameSyntax a => SimpleName(a.Name),
        _ => type.ToString()
    };

    private TNode AddAttribute<TNode>(TNode node, string attributeText) where TNode : MemberDeclarationSyntax
    {
        var parsed = CSharpSyntaxTree.ParseText($"[{attributeText}] class __M {{ }}")
            .GetRoot().DescendantNodes().OfType<AttributeListSyntax>().First();

        var leading = node.GetLeadingTrivia();
        var indentation = leading.Count > 0 && leading[^1].IsKind(SyntaxKind.WhitespaceTrivia)
            ? SyntaxFactory.TriviaList(leading[^1])
            : SyntaxTriviaList.Empty;

        var list = parsed.WithLeadingTrivia(leading).WithTrailingTrivia(SyntaxFactory.EndOfLine(_eol));
        var stripped = (TNode)node.WithLeadingTrivia(indentation);
        return (TNode)stripped.WithAttributeLists(stripped.AttributeLists.Insert(0, list));
    }
}
