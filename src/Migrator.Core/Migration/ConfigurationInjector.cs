using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Core.Migration;

/// <summary>
/// After <see cref="CodeTransformer"/> turned ConfigurationManager calls into <c>configuration[...]</c>, this provides the
/// variable in the two places where doing so is unambiguous:
/// <list type="bullet">
/// <item>the class that owns <c>static Main</c> (console apps): a static <c>IConfiguration configuration</c> built from appsettings*.json + environment variables;</item>
/// <item>classes derived from <c>BackgroundService</c> (converted Windows Services, created by DI): constructor injection, with field initializers that use it moved into the constructor.</item>
/// </list>
/// Everything else (libraries, arbitrary classes) is left to the developer, as before.
/// </summary>
public sealed class ConfigurationInjector : CSharpSyntaxRewriter
{
    private readonly string _eol;
    private readonly Dictionary<(string Id, string Description), int> _changes = new();

    private ConfigurationInjector(string eol) => _eol = eol;

    public static (string Text, List<CodeChange> Changes) Inject(string source)
    {
        if (!UsesConfiguration(source)) return (source, []);
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var injector = new ConfigurationInjector(source.Contains("\r\n") ? "\r\n" : "\n");
        var newRoot = (CompilationUnitSyntax)injector.Visit(root)!;
        if (injector._changes.Count == 0) return (source, []);
        if (newRoot.Usings.All(u => u.Name?.ToString() != "Microsoft.Extensions.Configuration"))
        {
            var usings = newRoot.Usings.ToList();
            usings.Add(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("Microsoft.Extensions.Configuration").WithLeadingTrivia(SyntaxFactory.Space)).WithTrailingTrivia(SyntaxFactory.EndOfLine(injector._eol)));
            if (usings.All(u => u.Name?.ToString() != "System"))
                usings.Insert(0, SyntaxFactory.UsingDirective(SyntaxFactory.ParseName("System").WithLeadingTrivia(SyntaxFactory.Space)).WithTrailingTrivia(SyntaxFactory.EndOfLine(injector._eol)));
            newRoot = newRoot.WithUsings(SyntaxFactory.List(usings.OrderBy(u => u.Name?.ToString().StartsWith("System", StringComparison.Ordinal) == true ? 0 : 1).ThenBy(u => u.Name?.ToString(), StringComparer.Ordinal)));
        }
        return (newRoot.ToFullString(), injector._changes.Select(kv => new CodeChange(kv.Key.Id, kv.Key.Description, kv.Value)).ToList());
    }

    private static bool UsesConfiguration(string text) =>
        text.Contains("configuration[", StringComparison.Ordinal) || text.Contains("configuration.GetConnectionString(", StringComparison.Ordinal) || text.Contains("configuration.GetSection(", StringComparison.Ordinal);

    private static bool DeclaresConfiguration(ClassDeclarationSyntax cls) =>
        cls.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == "configuration")) ||
        cls.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == "configuration") ||
        cls.Members.OfType<ConstructorDeclarationSyntax>().Any(c => c.ParameterList.Parameters.Any(p => p.Identifier.Text == "configuration"));

    private static bool ClassUsesConfiguration(ClassDeclarationSyntax cls) =>
        cls.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == "configuration");

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        if (DeclaresConfiguration(visited) || !ClassUsesConfiguration(visited)) return visited;

        var hasMain = visited.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "Main" && m.Modifiers.Any(SyntaxKind.StaticKeyword));
        var isHostedService = visited.BaseList?.Types.Any(t => t.Type.ToString() is "BackgroundService" or "IHostedService") == true;

        if (hasMain) return InjectStaticField(visited);
        if (isHostedService) return InjectConstructor(visited);
        return visited;
    }

    private ClassDeclarationSyntax InjectStaticField(ClassDeclarationSyntax cls)
    {
        var indent = MemberIndent(cls);
        var field = SyntaxFactory.ParseMemberDeclaration(
            $"private static readonly IConfiguration configuration = new ConfigurationBuilder(){_eol}" +
            $"{indent}    .SetBasePath(AppContext.BaseDirectory){_eol}" +
            $"{indent}    .AddJsonFile(\"appsettings.json\", optional: true){_eol}" +
            $"{indent}    .AddJsonFile($\"appsettings.{{Environment.GetEnvironmentVariable(\"DOTNET_ENVIRONMENT\") ?? \"Production\"}}.json\", optional: true){_eol}" +
            $"{indent}    .AddEnvironmentVariables(){_eol}" +
            $"{indent}    .Build();")!
            .WithLeadingTrivia(SyntaxFactory.Whitespace(indent), SyntaxFactory.Comment("// Configuração (appsettings.json, appsettings.{Ambiente}.json e variáveis de ambiente), gerada pelo Migrator a partir de ConfigurationManager."),
                SyntaxFactory.EndOfLine(_eol), SyntaxFactory.Whitespace(indent))
            .WithTrailingTrivia(SyntaxFactory.EndOfLine(_eol), SyntaxFactory.EndOfLine(_eol));
        Record("CS-CONFIG-STATIC", "IConfiguration criado na classe do Main (ConfigurationBuilder: appsettings + variáveis de ambiente)");
        return cls.WithMembers(cls.Members.Insert(0, field));
    }

    private ClassDeclarationSyntax InjectConstructor(ClassDeclarationSyntax cls)
    {
        var indent = MemberIndent(cls);
        var members = cls.Members.ToList();

        // Field initializers that use `configuration` cannot run before the constructor: move them into it.
        var moved = new List<string>();
        for (var i = 0; i < members.Count; i++)
        {
            if (members[i] is not FieldDeclarationSyntax field) continue;
            var variables = field.Declaration.Variables;
            if (!variables.Any(v => v.Initializer != null && v.Initializer.DescendantNodes().OfType<IdentifierNameSyntax>().Any(id => id.Identifier.Text == "configuration"))) continue;
            var stripped = variables.Select(v =>
            {
                if (v.Initializer == null) return v;
                moved.Add($"{v.Identifier.Text} = {v.Initializer.Value.ToString()};");
                return v.WithInitializer(null).WithIdentifier(v.Identifier.WithTrailingTrivia());
            });
            members[i] = field.WithDeclaration(field.Declaration.WithVariables(SyntaxFactory.SeparatedList(stripped)));
        }

        var fieldDecl = SyntaxFactory.ParseMemberDeclaration("private readonly IConfiguration configuration;")!
            .WithLeadingTrivia(SyntaxFactory.Whitespace(indent)).WithTrailingTrivia(SyntaxFactory.EndOfLine(_eol), SyntaxFactory.EndOfLine(_eol));
        var assignments = new[] { "this.configuration = configuration;" }.Concat(moved).Select(a => SyntaxFactory.ParseStatement(a + _eol).WithLeadingTrivia(SyntaxFactory.Whitespace(indent + "    "))).ToList();

        var ctorIndex = members.FindIndex(m => m is ConstructorDeclarationSyntax c && !c.Modifiers.Any(SyntaxKind.StaticKeyword));
        if (ctorIndex >= 0)
        {
            var ctor = (ConstructorDeclarationSyntax)members[ctorIndex];
            var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier("configuration")).WithType(SyntaxFactory.IdentifierName("IConfiguration").WithTrailingTrivia(SyntaxFactory.Space));
            var parameters = ctor.ParameterList.Parameters.Count == 0
                ? ctor.ParameterList.WithParameters(SyntaxFactory.SingletonSeparatedList(parameter))
                : ctor.ParameterList.AddParameters(parameter.WithLeadingTrivia(SyntaxFactory.Space));
            var body = ctor.Body ?? SyntaxFactory.Block();
            members[ctorIndex] = ctor.WithParameterList(parameters).WithExpressionBody(null).WithSemicolonToken(default)
                .WithBody(body.WithStatements(body.Statements.InsertRange(0, assignments)));
        }
        else
        {
            var ctor = SyntaxFactory.ParseMemberDeclaration(
                $"public {cls.Identifier.Text}(IConfiguration configuration){_eol}{indent}{{{_eol}{string.Concat(assignments.Select(a => a.ToFullString()))}{indent}}}{_eol}{_eol}")!
                .WithLeadingTrivia(SyntaxFactory.Whitespace(indent));
            var firstMethod = members.FindIndex(m => m is MethodDeclarationSyntax);
            members.Insert(firstMethod < 0 ? members.Count : firstMethod, ctor);
        }
        members.Insert(0, fieldDecl);
        Record("CS-CONFIG-INJECT", "IConfiguration injetado pelo construtor do BackgroundService" + (moved.Count > 0 ? " (inicializadores de campo movidos para o construtor)" : ""));
        return cls.WithMembers(SyntaxFactory.List(members));
    }

    private static string MemberIndent(ClassDeclarationSyntax cls)
    {
        var first = cls.Members.FirstOrDefault();
        var ws = first?.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToString();
        if (!string.IsNullOrEmpty(ws)) return ws;
        var classIndent = cls.GetLeadingTrivia().LastOrDefault(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).ToString();
        return classIndent + "    ";
    }

    private void Record(string id, string description) => _changes[(id, description)] = _changes.GetValueOrDefault((id, description)) + 1;
}
