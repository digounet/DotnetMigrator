using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Migrator.Core.Migration;

/// <summary>
/// Console automation → long-running worker (the platform's ECS worker module needs a container that stays up and answers
/// <c>/health</c>). The original <c>Main</c> is kept as <c>RunOnce</c> (every <c>Process.Kill()</c>/<c>Environment.Exit()</c> becomes a
/// <c>return</c>), and a generated <c>BackgroundService</c> calls it on an interval; the new entry point hosts the worker and the health check.
/// Nothing is invented about the business logic: one cycle is exactly what one run of the console did.
/// </summary>
public static partial class PlatformWorkerRewriter
{
    public const string LegacyClass = "LegacyEntryPoint";
    public const string WorkerFile = "MigratorWorker.cs";

    public sealed record Result(string EntryFile, string EntryText, string WorkerText, string Reason);

    /// <summary>Entry point found in one of the sources, or null when the project is not a classic console (top-level statements, no Main, already hosted).</summary>
    public static Result? Convert(IEnumerable<(string Relative, string Text)> sources, string rootNamespace, int defaultIntervalMinutes = 15)
    {
        foreach (var (relative, text) in sources.OrderByDescending(x => Path.GetFileName(x.Relative).Equals("Program.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var root = CSharpSyntaxTree.ParseText(text).GetRoot();
            if (root.DescendantNodes().OfType<GlobalStatementSyntax>().Any()) { var top = ConvertTopLevel(root, relative, rootNamespace, defaultIntervalMinutes); if (top != null) return top; continue; }
            var main = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == "Main" && m.Modifiers.Any(SyntaxKind.StaticKeyword) && m.Parent is ClassDeclarationSyntax);
            if (main == null || main.Body == null) continue;
            var cls = (ClassDeclarationSyntax)main.Parent!;
            if (cls.Identifier.Text == LegacyClass) continue;
            var kind = ReturnKind(main.ReturnType.ToString());
            if (kind == null) continue;

            var rewritten = new KillRewriter(main, kind.Value).Visit(root)!;
            // Rename the method and the class so the generated host owns Main; the original stays compilable and reachable as RunOnce.
            var edited = rewritten.ReplaceNodes(rewritten.DescendantNodes().OfType<MethodDeclarationSyntax>().Where(m => m.Identifier.Text == "Main" && m.Parent is ClassDeclarationSyntax),
                (m, _) => m.WithIdentifier(SyntaxFactory.Identifier("RunOnce").WithTriviaFrom(m.Identifier)).WithModifiers(InternalStatic(m.Modifiers)));
            edited = edited.ReplaceNodes(edited.DescendantNodes().OfType<ClassDeclarationSyntax>().Where(c => c.Identifier.Text == cls.Identifier.Text),
                (c, _) => c.WithIdentifier(SyntaxFactory.Identifier(LegacyClass).WithTriviaFrom(c.Identifier)));
            var ns = (cls.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString());
            return new Result(relative, edited.ToFullString(), WorkerSource(ns, kind.Value, defaultIntervalMinutes), "Main() → RunOnce()");
        }
        return null;
    }

    /// <summary>
    /// Top-level statements: the statements move into <c>static async Task RunOnce(string[] args)</c> of a <c>LegacyEntryPoint</c> class (usings and type declarations stay at file level);
    /// <c>return &lt;value&gt;;</c> and Process.Kill()/Environment.Exit() at the top level end the cycle.
    /// </summary>
    private static Result? ConvertTopLevel(SyntaxNode root, string relative, string rootNamespace, int intervalMinutes)
    {
        var unit = (CompilationUnitSyntax)root;
        var globals = unit.Members.OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count == 0) return null;
        // Local functions are statements too; type declarations (class/record/...) after the statements are members and stay outside the method.
        var rewritten = new KillRewriter(null, Return.Task).Visit(unit)!;
        var rewrittenUnit = (CompilationUnitSyntax)rewritten;
        var statements = string.Concat(rewrittenUnit.Members.OfType<GlobalStatementSyntax>().Select(g => g.Statement.ToFullString().TrimEnd() + "\n"));
        var types = string.Concat(rewrittenUnit.Members.Where(m => m is not GlobalStatementSyntax).Select(m => m.ToFullString()));
        var usings = string.Concat(rewrittenUnit.Usings.Select(u => u.ToFullString()));
        var externs = string.Concat(rewrittenUnit.Externs.Select(u => u.ToFullString()));
        var body = string.Join("\n", statements.Replace("\r\n", "\n").Split('\n').Select(l => l.Length == 0 ? l : "        " + l));
        var text = $$"""
            {{externs}}{{usings}}
            #pragma warning disable CS1998
            {{types.TrimStart('\r', '\n')}}
            internal static class {{LegacyClass}}
            {
                internal static async System.Threading.Tasks.Task RunOnce(string[] args)
                {
            {{body}}
                }
            }
            """.Replace("\r\n", "\n");
        return new Result(relative, text + "\n", WorkerSource(null, Return.Task, intervalMinutes), "instruções de nível superior → RunOnce()");
    }

    /// <summary>The original modifiers with private/public replaced by internal (reachable from the generated worker), keeping the indentation once.</summary>
    private static SyntaxTokenList InternalStatic(SyntaxTokenList modifiers)
    {
        var lead = modifiers.Count > 0 ? modifiers[0].LeadingTrivia : SyntaxTriviaList.Empty;
        var kept = modifiers.Where(x => !x.IsKind(SyntaxKind.PrivateKeyword) && !x.IsKind(SyntaxKind.PublicKeyword) && !x.IsKind(SyntaxKind.InternalKeyword))
            .Select(x => x.WithLeadingTrivia(SyntaxFactory.Space)).ToList();
        kept.Insert(0, SyntaxFactory.Token(SyntaxKind.InternalKeyword).WithLeadingTrivia(lead));
        return SyntaxFactory.TokenList(kept);
    }

    private enum Return { Void, Task, Int, TaskInt }

    private static Return? ReturnKind(string type) => type.Replace(" ", "") switch
    {
        "void" => Return.Void,
        "Task" or "System.Threading.Tasks.Task" => Return.Task,
        "int" => Return.Int,
        "Task<int>" or "System.Threading.Tasks.Task<int>" => Return.TaskInt,
        _ => null
    };

    /// <summary>Process.GetCurrentProcess().Kill(), Environment.Exit(n) and Process.Kill() statements directly in Main end the cycle, not the host.</summary>
    private sealed class KillRewriter(MethodDeclarationSyntax? main, Return kind) : CSharpSyntaxRewriter
    {
        /// <summary>Directly in Main (or in the top-level statements), not inside a lambda or local function.</summary>
        private bool InEntry(SyntaxNode node) =>
            (main != null ? node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == main : node.Ancestors().OfType<GlobalStatementSyntax>().Any() && node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() == null)
            && !node.Ancestors().Any(a => a is LocalFunctionStatementSyntax or LambdaExpressionSyntax or AnonymousMethodExpressionSyntax);

        /// <summary>Top level only: <c>return 1;</c> becomes a plain return (RunOnce is a Task).</summary>
        public override SyntaxNode? VisitReturnStatement(ReturnStatementSyntax node) =>
            main == null && node.Expression != null && InEntry(node) ? SyntaxFactory.ParseStatement("return;").WithTriviaFrom(node) : base.VisitReturnStatement(node);

        public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
        {
            var text = node.Expression.ToString().Replace(" ", "");
            var ends = text.EndsWith("Process.GetCurrentProcess().Kill()", StringComparison.Ordinal) || text.EndsWith(".Kill()", StringComparison.Ordinal) && text.Contains("Process", StringComparison.Ordinal)
                       || text.StartsWith("Environment.Exit(", StringComparison.Ordinal) || text.StartsWith("System.Environment.Exit(", StringComparison.Ordinal);
            if (!ends || !InEntry(node))
                return base.VisitExpressionStatement(node);
            var value = kind is Return.Int or Return.TaskInt ? " 0" : "";
            return SyntaxFactory.ParseStatement($"return{value};").WithTriviaFrom(node);
        }
    }

    private static string WorkerSource(string? ns, Return kind, int intervalMinutes)
    {
        var call = kind switch
        {
            Return.Void => $"await Task.Run(() => {LegacyClass}.RunOnce(arguments), stoppingToken);",
            Return.Int => $"await Task.Run(() => {LegacyClass}.RunOnce(arguments), stoppingToken);",
            _ => $"await {LegacyClass}.RunOnce(arguments);"
        };
        var body = $$"""
            using Microsoft.AspNetCore.Builder;
            using Microsoft.Extensions.Configuration;
            using Microsoft.Extensions.DependencyInjection;
            using Microsoft.Extensions.Diagnostics.HealthChecks;
            using Microsoft.Extensions.Hosting;
            using Microsoft.Extensions.Logging;
            using System;
            using System.Linq;
            using System.Threading;
            using System.Threading.Tasks;

            {{(ns != null ? $"namespace {ns};\n" : "")}}/// <summary>
            /// Gerado pelo Migrator: o console virou um worker de longa duração (módulo worker do ECS da plataforma). Cada ciclo executa
            /// exatamente o que uma execução do console fazia (RunOnce); o intervalo vem de Worker:IntervalMinutes (padrão {{intervalMinutes}} min).
            /// </summary>
            internal static class MigratorHost
            {
                public static async Task Main(string[] args)
                {
                    var builder = WebApplication.CreateBuilder(args);
                    builder.Services.AddHostedService<MigratorWorker>();
                    builder.Services.AddHealthChecks();
                    var app = builder.Build();
                    app.MapHealthChecks("/health");
                    await app.RunAsync();
                }
            }

            internal sealed class MigratorWorker(ILogger<MigratorWorker> logger, IConfiguration configuration) : BackgroundService
            {
                protected override async Task ExecuteAsync(CancellationToken stoppingToken)
                {
                    var minutes = int.TryParse(configuration["Worker:IntervalMinutes"], out var m) && m > 0 ? m : {{intervalMinutes}};
                    var interval = TimeSpan.FromMinutes(minutes);
                    var arguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
                    logger.LogInformation("Worker iniciado; um ciclo a cada {Minutes} minuto(s).", minutes);
                    while (!stoppingToken.IsCancellationRequested)
                    {
                        try
                        {
                            {{call}}
                        }
                        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                        {
                            logger.LogError(ex, "Falha no ciclo; nova tentativa no próximo intervalo.");
                        }
                        try { await Task.Delay(interval, stoppingToken); }
                        catch (TaskCanceledException) { break; }
                    }
                    logger.LogInformation("Worker finalizado.");
                }
            }

            """;
        return body.Replace("\r\n", "\n");
    }

    /// <summary>The Microsoft.AspNetCore.App shared framework brings WebApplication/health checks to a plain console project.</summary>
    public static string AddAspNetCoreFramework(string csproj)
    {
        if (csproj.Contains("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase) || csproj.Contains("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase)) return csproj;
        // The shared framework already ships these; keeping the PackageReference triggers NU1510 (the generated code must build with 0 warnings).
        csproj = InFramework().Replace(csproj, "");
        var close = csproj.LastIndexOf("</Project>", StringComparison.OrdinalIgnoreCase);
        if (close < 0) return csproj;
        return csproj.Insert(close, "  <ItemGroup>\n    <!-- Migrator: worker com /health (WebApplication) -->\n    <FrameworkReference Include=\"Microsoft.AspNetCore.App\" />\n  </ItemGroup>\n");
    }

    /// <summary>Environment variables the code reads (COD_AMBI_EXEO, ...), minus the ones the runtime or the platform already own.</summary>
    public static IEnumerable<string> EnvironmentVariablesRead(IEnumerable<string> sources)
    {
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PATH", "HOME", "USERNAME", "USERPROFILE", "COMPUTERNAME", "TEMP", "TMP", "OS", "HOSTNAME", "TZ", "LANG" };
        return sources.SelectMany(s => EnvironmentVariable().Matches(s).Select(m => m.Groups[1].Value))
            .Where(v => !ignored.Contains(v) && !v.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && !v.StartsWith("AWS_", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal);
    }

    [GeneratedRegex(@"^[ \t]*<PackageReference\s+Include=""Microsoft\.Extensions\.(?:Configuration(?:\.\w+)*|DependencyInjection(?:\.Abstractions)?|Hosting(?:\.Abstractions)?|Logging(?:\.Abstractions|\.Console|\.Debug|\.EventSource|\.EventLog)?|Options(?:\.ConfigurationExtensions)?|Primitives|Http|FileProviders(?:\.\w+)*|Diagnostics(?:\.\w+)*|Caching(?:\.\w+)*)""\s+Version=""[^""]*""\s*/>[ \t]*\r?\n", RegexOptions.Multiline)]
    private static partial Regex InFramework();

    [GeneratedRegex(@"GetEnvironmentVariable\(\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*\)")]
    private static partial Regex EnvironmentVariable();
}
