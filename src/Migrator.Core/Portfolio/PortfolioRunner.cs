using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Llm;
using Migrator.Core.Migration;
using Migrator.Core.Models;

namespace Migrator.Core.Portfolio;

/// <summary>Discovers every solution under a folder, analyzes each one (dry run) and aggregates the results.</summary>
public sealed class PortfolioRunner(ILlmAssistant? assistant = null)
{
    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", "packages", ".git", ".vs", ".idea", "TestResults", "_migration-report", "_secrets" };

    public async Task<PortfolioResult> RunAsync(PortfolioOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(options.RootDir);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Pasta não encontrada: {options.RootDir}");
        var reportDir = Path.GetFullPath(options.ReportDir ?? Path.Combine(root, "_portfolio-report"));
        Directory.CreateDirectory(reportDir);

        var inputs = Discover(root);
        if (inputs.Count == 0) throw new InvalidOperationException($"Nenhuma solução (.sln/.slnx) ou projeto (.csproj/.vbproj) encontrado em {root}.");

        var apps = new List<PortfolioApp>();
        var index = 0;
        foreach (var (name, input) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            progress?.Report($"[{index}/{inputs.Count}] {name}...");
            var appReport = Path.Combine(reportDir, "apps", Safe(name));
            try
            {
                var result = await new MigrationEngine(assistant).RunAsync(new MigrationOptions
                {
                    InputPath = input, DryRun = true, VerifyBuild = false, Offline = options.Offline, Cloud = options.Cloud, Target = options.Target, Llm = options.Llm, ReportDir = appReport,
                    NuGetConfigPath = options.NuGetConfigPath, NuGetSourceUrl = options.NuGetSourceUrl
                }, new Progress<string>(m => progress?.Report($"[{index}/{inputs.Count}] {name}: {m}")), cancellationToken);
                apps.Add(PortfolioAggregator.Summarize(name, input, result));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                apps.Add(new PortfolioApp { Name = name, InputPath = input, Error = $"{ex.GetType().Name}: {ex.Message}", ReportDir = appReport });
            }
        }

        string? baseline = null;
        if (options.BaselinePath != null && File.Exists(options.BaselinePath)) baseline = await File.ReadAllTextAsync(options.BaselinePath, cancellationToken);
        var portfolio = PortfolioAggregator.Aggregate(root, apps, baseline, options.BaselinePath);
        portfolio.ReportDir = reportDir;
        progress?.Report("Gerando relatórios do portfólio...");
        await PortfolioReports.WriteAllAsync(portfolio, reportDir, cancellationToken);
        return portfolio;
    }

    /// <summary>
    /// One application per solution file. A folder with both .sln and .slnx counts once (.slnx wins). Folders without any
    /// solution but with project files are analyzed as a directory input. Previous Migrator outputs are skipped.
    /// </summary>
    public static List<(string Name, string Input)> Discover(string root)
    {
        var solutions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // dir → solution file
        var projectDirs = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            if (File.Exists(Path.Combine(dir, WorkspaceLoader.OutputMarkerFile))) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".sln" or ".slnx")
                {
                    if (!solutions.TryGetValue(dir, out var existing) || (ext == ".slnx" && !existing.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)))
                        solutions[dir] = file;
                }
                else if (ext is ".csproj" or ".vbproj") projectDirs.Add(dir);
            }
            foreach (var sub in Directory.EnumerateDirectories(dir))
                if (!IgnoredDirs.Contains(Path.GetFileName(sub))) pending.Push(sub);
        }

        var inputs = solutions.Values.Select(s => (Name: Path.GetFileNameWithoutExtension(s), Input: s)).ToList();
        // Projects not covered by any solution: group by the top-level folder under root
        var covered = solutions.Keys.ToList();
        foreach (var projectDir in projectDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (covered.Any(c => projectDir.StartsWith(c.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || projectDir.Equals(c, StringComparison.OrdinalIgnoreCase))) continue;
            var relative = Path.GetRelativePath(root, projectDir);
            var top = relative == "." ? root : Path.Combine(root, relative.Split(Path.DirectorySeparatorChar)[0]);
            if (inputs.Any(i => i.Input.Equals(top, StringComparison.OrdinalIgnoreCase))) continue;
            if (covered.Any(c => c.StartsWith(top.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) continue; // a solution deeper in the same top folder already represents it
            inputs.Add((new DirectoryInfo(top).Name, top));
        }

        // Unique names
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return inputs.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i =>
        {
            var n = seen.GetValueOrDefault(i.Name);
            seen[i.Name] = n + 1;
            return n == 0 ? i : (i.Name + $" ({n + 1})", i.Input);
        }).ToList();
    }

    private static string Safe(string name) => Regex.Replace(name, @"[^\w.\-]+", "_");
}
