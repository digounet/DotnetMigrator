using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Llm;

/// <summary>
/// Build → fix → rebuild loop. One round: pick the files with compile errors, ask the model for a corrected version of each,
/// write it (keeping a backup). The engine then rebuilds and calls <see cref="Settle"/>, which keeps improvements,
/// reverts files that got worse and records what happened in the inventory.
/// </summary>
public sealed class LlmCodeFixer(LlmSession session, LlmOptions options, string outputDir, string reportDir)
{
    public sealed record Attempt(ProjectResult Project, string RelativeFile, string AbsolutePath, string BackupPath, int ErrorsBefore, IReadOnlyList<string> Codes);

    /// <summary>A model proposal that compiled worse than the original: fed back as context on the next attempt.</summary>
    private sealed record RejectedAttempt(string Code, IReadOnlyList<string> Errors);

    /// <summary>A model version that was kept (0 errors at the time) with the original it replaced and how many errors the original had.</summary>
    private sealed record AppliedFix(string BackupPath, int ErrorsBefore);

    private readonly HashSet<string> _doNotRetry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<RejectedAttempt>> _rejected = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AppliedFix> _applied = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _projectContext = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Attempts per file before giving up (the second one sees the errors the first one produced).</summary>
    public const int MaxAttemptsPerFile = 2;

    public int Round { get; private set; }
    /// <summary>True when the last <see cref="SettleAsync"/> reverted at least one file: the engine must rebuild so the report matches the disk.</summary>
    public bool NeedsRebuild { get; private set; }
    public int FilesFixed { get; private set; }
    public int FilesImproved { get; private set; }
    public int FilesReverted { get; private set; }

    /// <summary>Files (relative to the project dir) that currently have compile errors, with their error items.</summary>
    public static Dictionary<string, List<InventoryItem>> ErrorsByFile(ProjectResult project) =>
        project.Inventory
            .Where(i => i.Category == InventoryCategory.Build && i.Severity == InventorySeverity.Breaking && i.FilePath != null
                        && i.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !i.FilePath.Contains("_Legacy", StringComparison.OrdinalIgnoreCase)
                        && !i.FilePath.Contains("obj", StringComparison.OrdinalIgnoreCase))
            .GroupBy(i => i.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

    public async Task<List<Attempt>> FixRoundAsync(SolutionResult result, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var attempts = new List<Attempt>();
        var candidates = result.Projects
            .SelectMany(p => ErrorsByFile(p).Select(kv => (Project: p, File: kv.Key, Errors: kv.Value)))
            .Where(c => !_doNotRetry.Contains(Key(c.Project, c.File)))
            .OrderByDescending(c => c.Errors.Count)
            .Take(options.MaxFilesPerRound)
            .ToList();

        if (candidates.Count == 0) return attempts;
        Round++;
        foreach (var (project, relativeFile, errors) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.Available) break;
            var absolute = Path.GetFullPath(Path.Combine(outputDir, project.RelativeDir, relativeFile));
            if (!File.Exists(absolute)) continue;

            // The C# compiler reports method-body errors only after declaration errors elsewhere are gone, so a file the model
            // "fixed" in an earlier round can show new errors now. Never leave it worse than the original: restore the backup
            // and let the model try again with the new errors as feedback.
            if (_applied.TryGetValue(Key(project, relativeFile), out var applied) && errors.Count >= applied.ErrorsBefore)
                await RevertLateRegressionAsync(project, relativeFile, absolute, applied, errors, cancellationToken);

            var rejected = _rejected.GetValueOrDefault(Key(project, relativeFile));
            progress?.Report($"LLM (rodada {Round}): corrigindo {project.Project.Name}/{relativeFile} ({errors.Count} erro(s){(rejected != null ? ", 2ª tentativa" : "")})...");
            var original = await File.ReadAllTextAsync(absolute, cancellationToken);
            var response = await session.TryCompleteAsync(LlmPrompts.FixSystem, BuildUserMessage(project, relativeFile, original, errors, ProjectContext(project), rejected), cancellationToken);
            if (response == null) break;

            var code = LlmPrompts.ExtractCode(response);
            if (code == null || !LlmPrompts.LooksLikeValidReplacement(original, code) || code.Trim() == original.Trim())
            {
                _doNotRetry.Add(Key(project, relativeFile));
                await SaveAsync(Path.Combine(reportDir, "llm", $"rodada-{Round}", project.RelativeDir, relativeFile + ".resposta-rejeitada.txt"), response, cancellationToken);
                project.Inventory.Add(Item(project, InventorySeverity.Warning, "LLM-FIX-FAILED", relativeFile,
                    $"LLM não produziu uma correção utilizável para {Path.GetFileName(relativeFile)}",
                    code == null ? "A resposta não continha um bloco de código C#." : "A resposta não passou nas verificações de sanidade (tamanho, declaração de tipo, chaves balanceadas) ou não alterou o arquivo.",
                    "Corrija manualmente a partir das dicas dos erros de build."));
                continue;
            }

            var backup = Path.Combine(reportDir, "llm", $"rodada-{Round}", project.RelativeDir, relativeFile + ".before");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            await File.WriteAllTextAsync(backup, original, cancellationToken);
            await File.WriteAllTextAsync(absolute, code.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(true), cancellationToken);
            attempts.Add(new Attempt(project, relativeFile, absolute, backup, errors.Count, errors.Select(e => e.RuleId).Distinct().ToList()));
        }
        return attempts;
    }

    /// <summary>Called after the rebuild: compares error counts per attempted file, reverts regressions, writes inventory items.</summary>
    public async Task SettleAsync(List<Attempt> attempts, SolutionResult result, CancellationToken cancellationToken)
    {
        NeedsRebuild = false;
        foreach (var attempt in attempts)
        {
            var project = result.Projects.First(p => p.Project.ProjectPath == attempt.Project.Project.ProjectPath);
            var after = ErrorsByFile(project).GetValueOrDefault(attempt.RelativeFile)?.Count ?? 0;
            var projectErrorsAfter = project.Build?.Errors ?? 0;
            var model = session.Assistant.Name;
            var backupRelative = Path.GetRelativePath(reportDir, attempt.BackupPath);

            if (after == 0 && project.Build is { BlockedBy: null })
            {
                FilesFixed++;
                _applied[Key(project, attempt.RelativeFile)] = new AppliedFix(attempt.BackupPath, attempt.ErrorsBefore);
                project.Inventory.Add(Item(project, InventorySeverity.Info, "LLM-FIX", attempt.RelativeFile,
                    $"{Path.GetFileName(attempt.RelativeFile)}: {attempt.ErrorsBefore} erro(s) corrigidos pela LLM ({string.Join(", ", attempt.Codes)})",
                    $"Modelo {model}, rodada {Round}. O arquivo original está em {backupRelative}.",
                    "Revise o diff antes de aceitar: a correção compila, mas a LLM pode ter alterado comportamento ou deixado comentários 'TODO Migrator (LLM)'.", auto: true));
            }
            else if (after < attempt.ErrorsBefore)
            {
                FilesImproved++;
                project.Inventory.Add(Item(project, InventorySeverity.Warning, "LLM-FIX-PARTIAL", attempt.RelativeFile,
                    $"{Path.GetFileName(attempt.RelativeFile)}: erros reduzidos de {attempt.ErrorsBefore} para {after} pela LLM",
                    $"Modelo {model}, rodada {Round}. Original em {backupRelative}. Os erros restantes estão listados em 'Build de verificação'.",
                    "Termine a correção manualmente ou aumente --llm-rounds."));
            }
            else
            {
                FilesReverted++;
                NeedsRebuild = true;
                var key = Key(project, attempt.RelativeFile);
                var current = await File.ReadAllTextAsync(attempt.AbsolutePath, cancellationToken);
                var proposal = Path.ChangeExtension(attempt.BackupPath, ".llm.cs.txt");
                await File.WriteAllTextAsync(proposal, current, cancellationToken);
                await File.WriteAllTextAsync(attempt.AbsolutePath, await File.ReadAllTextAsync(attempt.BackupPath, cancellationToken), new UTF8Encoding(true), cancellationToken);

                var newErrors = (ErrorsByFile(project).GetValueOrDefault(attempt.RelativeFile) ?? [])
                    .Select(e => $"linha {e.Line?.ToString() ?? "?"}: {e.RuleId} {e.Description}").Distinct().Take(15).ToList();
                var history = _rejected.TryGetValue(key, out var list) ? list : _rejected[key] = [];
                history.Add(new RejectedAttempt(current, newErrors));
                var giveUp = history.Count >= MaxAttemptsPerFile;
                if (giveUp) _doNotRetry.Add(key);

                project.Inventory.Add(Item(project, InventorySeverity.Warning, "LLM-FIX-REVERTED", attempt.RelativeFile,
                    $"{Path.GetFileName(attempt.RelativeFile)}: correção da LLM revertida ({attempt.ErrorsBefore} → {after} erro(s), tentativa {history.Count})",
                    $"A versão proposta não reduziu os erros e foi desfeita; ela ficou salva em {Path.GetRelativePath(reportDir, proposal)} para consulta." +
                    (giveUp ? "" : " Na próxima rodada a LLM recebe estes erros como feedback e tenta de novo."),
                    "Use a proposta como ponto de partida ou corrija manualmente."));
            }
        }
    }

    private async Task RevertLateRegressionAsync(ProjectResult project, string relativeFile, string absolute, AppliedFix applied, List<InventoryItem> errors, CancellationToken cancellationToken)
    {
        var key = Key(project, relativeFile);
        var current = await File.ReadAllTextAsync(absolute, cancellationToken);
        await SaveAsync(Path.ChangeExtension(applied.BackupPath, ".llm.cs.txt"), current, cancellationToken);
        await File.WriteAllTextAsync(absolute, await File.ReadAllTextAsync(applied.BackupPath, cancellationToken), new UTF8Encoding(true), cancellationToken);
        _applied.Remove(key);
        FilesFixed = Math.Max(0, FilesFixed - 1);
        FilesReverted++;
        var history = _rejected.TryGetValue(key, out var list) ? list : _rejected[key] = [];
        history.Add(new RejectedAttempt(current, errors.Select(e => $"linha {e.Line?.ToString() ?? "?"}: {e.RuleId} {e.Description}").Distinct().Take(15).ToList()));
        if (history.Count >= MaxAttemptsPerFile) _doNotRetry.Add(key);
        project.Inventory.RemoveAll(i => i.RuleId == "LLM-FIX" && string.Equals(i.FilePath, relativeFile, StringComparison.OrdinalIgnoreCase));
        project.Inventory.Add(Item(project, InventorySeverity.Warning, "LLM-FIX-REVERTED", relativeFile,
            $"{Path.GetFileName(relativeFile)}: correção da LLM revertida ({applied.ErrorsBefore} → {errors.Count} erro(s) após os demais arquivos compilarem)",
            "A versão da LLM compilava enquanto outros arquivos ainda tinham erros de declaração; com eles resolvidos, o compilador apontou erros nela. O original foi restaurado.",
            "Use a proposta salva em _migration-report/llm/ como ponto de partida ou corrija manualmente."));
        // Keep the original's errors as the baseline for the retry in this same round.
        errors.Clear();
        errors.AddRange(ErrorsByFile(project).GetValueOrDefault(relativeFile) ?? []);
    }

    private static async Task SaveAsync(string path, string content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    /// <summary>What the model may rely on: NuGet packages and framework references of the generated project (read from the migrated .csproj).</summary>
    private string ProjectContext(ProjectResult project)
    {
        if (_projectContext.TryGetValue(project.Project.ProjectPath, out var cached)) return cached;
        var sb = new StringBuilder();
        try
        {
            var csproj = Path.Combine(outputDir, project.OutputProjectPath ?? "");
            if (File.Exists(csproj))
            {
                var doc = System.Xml.Linq.XDocument.Load(csproj);
                var sdk = doc.Root?.Attribute("Sdk")?.Value ?? "Microsoft.NET.Sdk";
                var frameworks = doc.Descendants("FrameworkReference").Select(e => e.Attribute("Include")?.Value).Where(v => v != null).ToList();
                if (sdk.Contains("Web", StringComparison.OrdinalIgnoreCase)) frameworks.Insert(0, "Microsoft.AspNetCore.App (SDK Web)");
                var packages = doc.Descendants("PackageReference").Select(e => $"{e.Attribute("Include")?.Value} {e.Attribute("Version")?.Value}").ToList();
                var projects = doc.Descendants("ProjectReference").Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")?.Value ?? "")).ToList();
                sb.AppendLine($"- SDK: {sdk}; TargetFramework: {doc.Descendants("TargetFramework").FirstOrDefault()?.Value ?? "net10.0"}");
                sb.AppendLine("- Frameworks: " + (frameworks.Count > 0 ? string.Join(", ", frameworks) : "nenhum além do runtime (sem ASP.NET Core)"));
                sb.AppendLine("- Pacotes NuGet: " + (packages.Count > 0 ? string.Join(", ", packages) : "nenhum"));
                if (projects.Count > 0) sb.AppendLine("- Projetos referenciados: " + string.Join(", ", projects));
            }
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { }
        return _projectContext[project.Project.ProjectPath] = sb.ToString();
    }

    private static string Key(ProjectResult project, string file) => project.Project.ProjectPath + "|" + file;

    private static string BuildUserMessage(ProjectResult project, string relativeFile, string content, List<InventoryItem> errors, string projectContext, List<RejectedAttempt>? rejected)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Projeto: {project.Project.Name} ({KindLabel(project.Project.Kind)}, net10.0)");
        sb.AppendLine($"Arquivo: {relativeFile}");
        if (projectContext.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Disponível no projeto (não use nada além disto e do runtime):");
            sb.Append(projectContext);
        }
        if (rejected is { Count: > 0 })
        {
            var last = rejected[^1];
            sb.AppendLine();
            sb.AppendLine($"Tentativa anterior rejeitada ({rejected.Count}ª): a versão abaixo foi descartada porque gerou estes erros de compilação:");
            foreach (var e in last.Errors) sb.AppendLine($"- {e}");
            sb.AppendLine("Versão rejeitada (apenas para referência; parta do arquivo atual):");
            sb.AppendLine("```csharp");
            sb.AppendLine(last.Code);
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("Erros de compilação neste arquivo:");
        foreach (var e in errors.OrderBy(e => e.Line))
        {
            sb.AppendLine($"- linha {e.Line?.ToString() ?? "?"}: {e.RuleId} {e.Description}");
            if (!string.IsNullOrWhiteSpace(e.Suggestion) && e.Suggestion != "Nenhuma ação necessária.") sb.AppendLine($"  dica: {e.Suggestion}");
        }
        sb.AppendLine();
        sb.AppendLine("Conteúdo atual do arquivo:");
        sb.AppendLine("```csharp");
        sb.AppendLine(content);
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static string KindLabel(ProjectKind kind) => kind switch
    {
        ProjectKind.Web => "ASP.NET Core MVC/Web API", ProjectKind.WindowsService => "serviço/worker", ProjectKind.Console => "console",
        ProjectKind.Test => "testes", ProjectKind.Desktop => "desktop", _ => "biblioteca de classes"
    };

    private static InventoryItem Item(ProjectResult project, InventorySeverity severity, string rule, string file, string title, string description, string suggestion, bool auto = false) => new()
    {
        Project = project.Project.Name, Severity = severity, Category = InventoryCategory.Code, RuleId = rule, Title = title,
        Description = description, Suggestion = suggestion, FilePath = file, AutoMigrated = auto
    };
}
