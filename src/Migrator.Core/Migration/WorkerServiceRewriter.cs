using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Core.Migration;

/// <summary>What the rewriter learned about the Windows Services in a project; used to generate Program.cs.</summary>
public sealed class WorkerServiceInfo
{
    public List<(string ClassName, string? Namespace)> Services { get; } = [];
    public Dictionary<string, string> ServiceNames { get; } = new(StringComparer.Ordinal);
    public bool Any => Services.Count > 0;
}

/// <summary>
/// Deterministic ServiceBase → BackgroundService conversion:
/// OnStart → ExecuteAsync, OnStop → StopAsync, designer plumbing removed, usings adjusted.
/// Pause/Continue/Shutdown/custom commands have no equivalent and are kept as plain methods with a TODO.
/// </summary>
public sealed partial class WorkerServiceRewriter : CSharpSyntaxRewriter
{
    private readonly string _eol;
    private readonly Dictionary<(string Id, string Description), int> _changes = new();
    private readonly List<string> _warnings = [];
    private bool _inService;

    private WorkerServiceRewriter(string eol) => _eol = eol;

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Finds every ServiceBase subclass in the project's sources (before any rewrite).</summary>
    public static WorkerServiceInfo Discover(IEnumerable<(string Path, string Text)> sources)
    {
        var info = new WorkerServiceInfo();
        foreach (var (_, text) in sources)
        {
            var hasServiceBase = text.Contains("ServiceBase", StringComparison.Ordinal);
            var nameMatch = ServiceNameAssignment().Match(text);
            if (!hasServiceBase && !nameMatch.Success) continue;
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            if (hasServiceBase)
                foreach (var cls in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    if (IsServiceBase(cls))
                        info.Services.Add((cls.Identifier.Text, Namespace(cls)));
            if (nameMatch.Success)
            {
                // this.ServiceName = "X" lives in the designer file (a partial of the service class): pair it by that class name
                var owner = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
                if (owner != null) info.ServiceNames[owner] = nameMatch.Groups[1].Value;
            }
        }
        return info;
    }

    public static (string Text, List<CodeChange> Changes, WorkerServiceRewriter Rewriter) Rewrite(string source)
    {
        var rewriter = new WorkerServiceRewriter(source.Contains("\r\n") ? "\r\n" : "\n");
        if (!source.Contains("ServiceBase", StringComparison.Ordinal)) return (source, [], rewriter);

        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();
        if (!root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(IsServiceBase)) return (source, [], rewriter);

        var newRoot = (CompilationUnitSyntax)rewriter.Visit(root)!;
        newRoot = rewriter.FixUsings(newRoot);
        var changes = rewriter._changes.Select(kv => new CodeChange(kv.Key.Id, kv.Key.Description, kv.Value)).ToList();
        return (newRoot.ToFullString(), changes, rewriter);
    }

    /// <summary>Program.cs for the generic host: one hosted service per converted class, Windows Service integration kept for on-premises installs.</summary>
    public static string GenerateProgram(string projectName, WorkerServiceInfo info)
    {
        var usings = new SortedSet<string>(StringComparer.Ordinal) { "Microsoft.Extensions.DependencyInjection", "Microsoft.Extensions.Hosting" };
        foreach (var (_, ns) in info.Services) if (ns != null) usings.Add(ns);
        var serviceName = info.Services.Select(s => info.ServiceNames.GetValueOrDefault(s.ClassName)).FirstOrDefault(n => n != null) ?? projectName;
        var sb = new System.Text.StringBuilder();
        foreach (var u in usings) sb.Append("using ").Append(u).Append(";\n");
        sb.Append('\n');
        sb.Append($"// Gerado pelo Migrator a partir de Program.cs (ServiceBase.Run). O Program.cs original está em _Legacy/.\n");
        sb.Append("var builder = Host.CreateApplicationBuilder(args);\n");
        sb.Append($"builder.Services.AddWindowsService(options => options.ServiceName = \"{serviceName}\"); // sem efeito fora do Windows (containers Linux)\n");
        foreach (var (cls, _) in info.Services) sb.Append($"builder.Services.AddHostedService<{cls}>();\n");
        sb.Append("\nvar host = builder.Build();\nhost.Run();\n");
        return sb.ToString().Replace("\n", Environment.NewLine);
    }

    private static bool IsServiceBase(ClassDeclarationSyntax cls) =>
        cls.BaseList?.Types.Any(t => SimpleName(t.Type) == "ServiceBase") == true;

    private static string SimpleName(TypeSyntax type) => type switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        _ => type.ToString()
    };

    private static string? Namespace(SyntaxNode node) =>
        node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();

    private void Record(string id, string description) => _changes[(id, description)] = _changes.GetValueOrDefault((id, description)) + 1;

    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        if (!IsServiceBase(node)) return base.VisitClassDeclaration(node);
        _inService = true;
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;
        _inService = false;

        var types = visited.BaseList!.Types.Select(t => SimpleName(t.Type) == "ServiceBase"
            ? t.WithType(SyntaxFactory.IdentifierName("BackgroundService").WithTriviaFrom(t.Type))
            : t);
        visited = visited.WithBaseList(visited.BaseList.WithTypes(SyntaxFactory.SeparatedList(types)));
        Record("CS-WORKER", "ServiceBase → BackgroundService (OnStart → ExecuteAsync, OnStop → StopAsync)");
        return visited;
    }

    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        if (!_inService || !node.Modifiers.Any(SyntaxKind.OverrideKeyword)) return base.VisitMethodDeclaration(node);
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
        var name = node.Identifier.Text;
        var task = SyntaxFactory.IdentifierName("Task").WithTrailingTrivia(SyntaxFactory.Space);
        var indent = visited.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)).LastOrDefault().ToString();

        switch (name)
        {
            case "OnStart":
            {
                var usesArgs = visited.Body != null && visited.Body.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.Text == "args");
                var statements = new List<StatementSyntax>();
                if (usesArgs)
                    statements.Add(SyntaxFactory.ParseStatement($"var args = Environment.GetCommandLineArgs().Skip(1).ToArray(); // TODO Migrator (Worker): OnStart recebia os argumentos do SCM{_eol}")
                        .WithLeadingTrivia(SyntaxFactory.Whitespace(indent + "    ")));
                if (visited.Body != null) statements.AddRange(visited.Body.Statements);
                statements.Add(SyntaxFactory.ParseStatement($"return Task.CompletedTask;{_eol}").WithLeadingTrivia(SyntaxFactory.Whitespace(indent + "    ")));
                var method = visited
                    .WithIdentifier(SyntaxFactory.Identifier("ExecuteAsync").WithTriviaFrom(visited.Identifier))
                    .WithReturnType(task)
                    .WithParameterList(SyntaxFactory.ParseParameterList("(CancellationToken stoppingToken)").WithTrailingTrivia(visited.ParameterList.GetTrailingTrivia()))
                    .WithBody(visited.Body?.WithStatements(SyntaxFactory.List(statements)))
                    .WithExpressionBody(null)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.ProtectedKeyword).WithTrailingTrivia(SyntaxFactory.Space), SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                    .WithLeadingTrivia(visited.GetLeadingTrivia());
                return method;
            }
            case "OnStop":
            {
                var statements = new List<StatementSyntax>();
                if (visited.Body != null) statements.AddRange(visited.Body.Statements);
                statements.Add(SyntaxFactory.ParseStatement($"return base.StopAsync(cancellationToken);{_eol}").WithLeadingTrivia(SyntaxFactory.Whitespace(indent + "    ")));
                return visited
                    .WithIdentifier(SyntaxFactory.Identifier("StopAsync").WithTriviaFrom(visited.Identifier))
                    .WithReturnType(task)
                    .WithParameterList(SyntaxFactory.ParseParameterList("(CancellationToken cancellationToken)").WithTrailingTrivia(visited.ParameterList.GetTrailingTrivia()))
                    .WithBody(visited.Body?.WithStatements(SyntaxFactory.List(statements)))
                    .WithExpressionBody(null)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword).WithTrailingTrivia(SyntaxFactory.Space), SyntaxFactory.Token(SyntaxKind.OverrideKeyword).WithTrailingTrivia(SyntaxFactory.Space)))
                    .WithLeadingTrivia(visited.GetLeadingTrivia());
            }
            case "OnPause" or "OnContinue" or "OnShutdown" or "OnCustomCommand" or "OnPowerEvent" or "OnSessionChange":
            {
                _warnings.Add($"{name} não existe em BackgroundService; o método foi mantido sem 'override' e precisa ser chamado por você (ex.: OnShutdown → StopAsync).");
                var modifiers = SyntaxFactory.TokenList(visited.Modifiers.Where(m => !m.IsKind(SyntaxKind.OverrideKeyword)));
                var comment = SyntaxFactory.Comment($"// TODO Migrator (Worker): {name} não existe em BackgroundService. Ligue esta lógica a StopAsync/ExecuteAsync ou remova.");
                return visited.WithModifiers(modifiers)
                    .WithLeadingTrivia(visited.GetLeadingTrivia().Add(comment).Add(SyntaxFactory.EndOfLine(_eol)).Add(SyntaxFactory.Whitespace(indent)));
            }
            case "Dispose":
                return visited; // BackgroundService implements IDisposable too
            default:
                return visited;
        }
    }

    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (!_inService) return base.VisitExpressionStatement(node);
        var text = node.Expression.ToString();
        // Designer plumbing and ServiceBase properties that no longer exist.
        if (text is "InitializeComponent()" or "this.InitializeComponent()" || DesignerProperty().IsMatch(text))
        {
            Record("CS-WORKER-DESIGNER", "InitializeComponent()/propriedades do ServiceBase removidos (designer movido para _Legacy)");
            return null;
        }
        return base.VisitExpressionStatement(node);
    }

    private CompilationUnitSyntax FixUsings(CompilationUnitSyntax root)
    {
        var existing = root.Usings.Select(u => u.Name?.ToString()).Where(n => n != null).ToHashSet(StringComparer.Ordinal)!;
        var usings = root.Usings.ToList();
        var text = root.ToFullString();
        if (!text.Contains("ServiceController", StringComparison.Ordinal))
            usings.RemoveAll(u => u.Name?.ToString() == "System.ServiceProcess");
        foreach (var required in new[] { "System", "System.Linq", "System.Threading", "System.Threading.Tasks", "Microsoft.Extensions.Hosting" })
            if (!existing.Contains(required))
                usings.Add(SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(required).WithLeadingTrivia(SyntaxFactory.Space)).WithTrailingTrivia(SyntaxFactory.EndOfLine(_eol)));
        var ordered = usings.OrderBy(u => u.Name?.ToString().StartsWith("System", StringComparison.Ordinal) == true ? 0 : 1).ThenBy(u => u.Name?.ToString(), StringComparer.Ordinal).ToList();
        return root.WithUsings(SyntaxFactory.List(ordered));
    }

    [GeneratedRegex(@"this\.ServiceName\s*=\s*""([^""]+)""")]
    private static partial Regex ServiceNameAssignment();

    [GeneratedRegex(@"^(this\.)?(ServiceName|CanStop|CanPauseAndContinue|CanShutdown|CanHandlePowerEvent|CanHandleSessionChangeEvent|AutoLog|ExitCode)\s*=")]
    private static partial Regex DesignerProperty();
}
