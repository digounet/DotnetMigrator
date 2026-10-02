using System.Text;
using System.Xml.Linq;
using Migrator.Core.Analysis;
using Migrator.Core.Cloud;
using Migrator.Core.Llm;
using Migrator.Core.Data;
using Migrator.Core.Models;
using Migrator.Core.NuGet;
using Migrator.Core.Reporting;

namespace Migrator.Core.Migration;

public sealed class MigrationEngine
{
    private readonly ILlmAssistant? _assistant;

    public MigrationEngine() { }

    /// <summary>Use a custom <see cref="ILlmAssistant"/> (e.g. the corporate SDK) instead of the provider named in <see cref="LlmOptions.Provider"/>.</summary>
    public MigrationEngine(ILlmAssistant? assistant) => _assistant = assistant;

    private static readonly string[] RootFilesToCopy = ["NuGet.config", "nuget.config", "NuGet.Config", ".editorconfig", "Directory.Build.props", "Directory.Build.targets"];

    public async Task<SolutionResult> RunAsync(MigrationOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var workspace = WorkspaceLoader.Load(options.InputPath);
        var result = new SolutionResult { Options = options, RootDir = workspace.RootDir, SolutionName = workspace.Name };
        var parent = Path.GetDirectoryName(workspace.RootDir.TrimEnd(Path.DirectorySeparatorChar)) ?? workspace.RootDir;

        if (!options.DryRun)
        {
            result.OutputDir = Path.GetFullPath(options.OutputDir ?? Path.Combine(parent, workspace.Name + ".net10"));
            PrepareOutputDirectory(result.OutputDir, workspace.RootDir, options.Force);
        }
        result.ReportDir = Path.GetFullPath(options.ReportDir ??
            (options.DryRun ? Path.Combine(parent, workspace.Name + ".migration-report") : Path.Combine(result.OutputDir!, "_migration-report")));

        foreach (var (path, reason) in workspace.Skipped)
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Warning, Category = InventoryCategory.ProjectFile, RuleId = "SLN-SKIPPED",
                Title = $"Projeto não migrado: {path}", Description = reason, Suggestion = "Trate este projeto separadamente."
            });

        progress?.Report("Lendo projetos...");
        var projects = new List<ProjectInfo>();
        foreach (var path in workspace.Projects)
        {
            try { projects.Add(ProjectLoader.Load(path, workspace.RootDir)); }
            catch (Exception ex)
            {
                result.GlobalItems.Add(new InventoryItem
                {
                    Project = Path.GetFileNameWithoutExtension(path), Severity = InventorySeverity.Breaking, Category = InventoryCategory.ProjectFile,
                    RuleId = "SLN-LOADERROR", Title = $"Falha ao ler {Path.GetFileName(path)}", Description = ex.Message,
                    Suggestion = "Verifique se o arquivo de projeto é um XML válido."
                });
            }
        }

        var llm = ResolveLlm(options, result);

        var map = projects.ToDictionary(p => p.ProjectPath, p => Path.GetRelativePath(workspace.RootDir, p.ProjectPath), StringComparer.OrdinalIgnoreCase);
        using var nuget = new NuGetClient(options.Offline);
        var context = new ProjectMigrationContext(workspace.RootDir, map, new PackagePlanner(nuget), UsesSystemDataSqlClient(projects), options.Cloud);

        var migrated = new List<MigratedProject>();
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Migrando {project.Name} ({project.Kind}, {project.TargetFramework})...");
            try
            {
                var item = await ProjectMigrator.MigrateAsync(project, context);
                migrated.Add(item);
                result.Projects.Add(item.Result);
            }
            catch (Exception ex)
            {
                result.GlobalItems.Add(new InventoryItem
                {
                    Project = project.Name, Severity = InventorySeverity.Breaking, Category = InventoryCategory.ProjectFile, RuleId = "SLN-MIGRATIONERROR",
                    Title = $"Erro inesperado ao migrar {project.Name}", Description = $"{ex.GetType().Name}: {ex.Message}",
                    Suggestion = "Reporte o erro com o arquivo de projeto; os demais projetos foram processados normalmente."
                });
            }
        }
        result.NuGetChecked = !options.Offline && nuget.IsOnline;
        if (!options.Offline && !nuget.IsOnline)
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Warning, Category = InventoryCategory.Package, RuleId = "NUGET-OFFLINE",
                Title = "nuget.org inacessível", Description = "A compatibilidade dos pacotes não pôde ser verificada (proxy/firewall?).",
                Suggestion = "Configure o proxy (HTTPS_PROXY) e execute novamente; enquanto isso, confie no build de verificação (avisos NU1701)."
            });

        progress?.Report("Alinhando versões de pacotes entre projetos...");
        var ordered = PackageAligner.TopologicalOrder(migrated);
        await PackageAligner.AlignAsync(ordered, nuget);
        foreach (var project in migrated.Where(p => p.Spec != null))
            project.Plan.Write(project.Result.OutputProjectPath!, ProjectFileWriter.Write(project.Spec!));

        List<(ProjectResult Result, ApplicationProfile Profile)>? profiles = null;
        if (options.Cloud == CloudTarget.Aws)
        {
            progress?.Report("Avaliando arquitetura alvo na AWS...");
            profiles = AdviseCloud(result, migrated, ordered);
        }

        if (!options.DryRun)
        {
            foreach (var project in migrated) await ApplyAsync(project.Plan, result.OutputDir!);
            WriteSolution(result, workspace);
            CopyRootFiles(workspace.RootDir, result.OutputDir!, result);
            await File.WriteAllTextAsync(Path.Combine(result.OutputDir!, WorkspaceLoader.OutputMarkerFile),
                $"Gerado pelo Migrator em {DateTime.Now:O} a partir de {workspace.RootDir}{Environment.NewLine}", cancellationToken);

            if (llm != null) await new LlmCodeDrafter(llm, options.Llm, result.OutputDir!).DraftAsync(result, progress, cancellationToken);

            if (options.VerifyBuild && ordered.Count > 0)
            {
                await VerifyBuildAsync(result, ordered, options, progress, cancellationToken);
                if (llm != null && result.BuildSucceeded == false)
                    await FixBuildWithLlmAsync(llm, result, ordered, options, progress, cancellationToken);
            }
        }

        if (llm != null && profiles != null)
            await LlmNarrator.NarrateAsync(llm, result, profiles, progress, cancellationToken);
        if (llm != null) result.LlmCalls = llm.Calls;

        result.FinishedAt = DateTime.Now;
        progress?.Report("Gerando relatórios...");
        await ReportWriter.WriteAllAsync(result, result.ReportDir);
        return result;
    }

    /// <summary>
    /// Hosting recommendation per project (using the merged profile of the project plus everything it references),
    /// Dockerfiles for deployable projects and the solution-wide architecture proposal.
    /// </summary>
    private LlmSession? ResolveLlm(MigrationOptions options, SolutionResult result)
    {
        var assistant = _assistant != null
            ? (options.Llm.Enabled || options.Llm.CacheDir != null ? LlmAssistantFactory.Wrap(_assistant, options.Llm) : _assistant)
            : LlmAssistantFactory.Create(options.Llm);
        if (assistant == null) return null;
        result.LlmModel = assistant.Name;
        return new LlmSession(assistant, result);
    }

    /// <summary>
    /// Build → ask the model to fix the files with errors → rebuild, up to <see cref="LlmOptions.MaxFixRounds"/> rounds or until the build passes.
    /// Each round re-runs the whole verification so the inventory's "Build de verificação" section always reflects the final state.
    /// </summary>
    private static async Task FixBuildWithLlmAsync(LlmSession llm, SolutionResult result, IReadOnlyList<MigratedProject> ordered, MigrationOptions options,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var fixer = new LlmCodeFixer(llm, options.Llm, result.OutputDir!, result.ReportDir!);
        var totalBefore = result.AllItems.Count(i => i.Category == InventoryCategory.Build && i.Severity == InventorySeverity.Breaking);
        async Task RebuildAsync(string label)
        {
            if (result.BuildLogPath != null && File.Exists(result.BuildLogPath))
                File.Move(result.BuildLogPath, Path.Combine(Path.GetDirectoryName(result.BuildLogPath)!, $"build-verification.{label}.log"), overwrite: true);
            foreach (var project in result.Projects)
            {
                project.Inventory.RemoveAll(i => i.Category == InventoryCategory.Build && !i.RuleId.StartsWith("LLM-", StringComparison.Ordinal));
                project.Build = null;
            }
            result.GlobalItems.RemoveAll(i => i.Category == InventoryCategory.Build && !i.RuleId.StartsWith("LLM-", StringComparison.Ordinal));
            await VerifyBuildAsync(result, ordered, options, progress, cancellationToken);
        }

        for (var round = 1; round <= options.Llm.MaxFixRounds && llm.Available && result.BuildSucceeded == false; round++)
        {
            var attempts = await fixer.FixRoundAsync(result, progress, cancellationToken);
            if (attempts.Count == 0) break;

            progress?.Report($"Build de verificação após a rodada {round} da LLM...");
            await RebuildAsync($"antes-da-rodada-{round}");
            await fixer.SettleAsync(attempts, result, cancellationToken);
            // Reverted files are back to their original content: rebuild so the inventory describes what is actually on disk.
            if (fixer.NeedsRebuild)
            {
                progress?.Report($"Build de verificação após reverter propostas da rodada {round}...");
                await RebuildAsync($"rodada-{round}-com-propostas-revertidas");
            }
        }

        var totalAfter = result.AllItems.Count(i => i.Category == InventoryCategory.Build && i.Severity == InventorySeverity.Breaking);
        if (fixer.Round > 0)
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Info, Category = InventoryCategory.Code, RuleId = "LLM-SUMMARY",
                Title = $"Correção assistida por LLM: {fixer.Round} rodada(s), {fixer.FilesFixed} arquivo(s) corrigidos, {fixer.FilesImproved} melhorados, {fixer.FilesReverted} revertidos",
                Description = $"Erros de compilação: {totalBefore} antes → {totalAfter} depois ({llm.Assistant.Name}, {llm.Calls} chamada(s)). Originais em _migration-report/llm/.",
                Suggestion = result.BuildSucceeded == true ? "Revise os diffs dos arquivos LLM-FIX antes de aceitar." : "Os erros restantes seguem listados por projeto; corrija manualmente ou rode novamente com mais rodadas.",
                AutoMigrated = fixer.FilesFixed > 0
            });
    }

    private static List<(ProjectResult Result, ApplicationProfile Profile)> AdviseCloud(SolutionResult result, List<MigratedProject> migrated, IReadOnlyList<MigratedProject> ordered)
    {
        var profiles = new List<(ProjectResult Result, ApplicationProfile Profile)>();
        var byPath = migrated.ToDictionary(m => m.Result.Project.ProjectPath, StringComparer.OrdinalIgnoreCase);
        var dockerIgnoreWritten = false;
        foreach (var project in migrated)
        {
            var closure = PackageAligner.Closure(project, ordered).ToList();
            var merged = project.Profile.MergeWith(closure.Select(c => c.Profile));
            var hosting = AwsArchitect.Recommend(project.Result.Project, merged);
            project.Result.Hosting = hosting;
            profiles.Add((project.Result, merged));

            if (hosting.Primary is AwsHosting.NotDeployable or AwsHosting.Desktop || project.Result.OutputProjectPath == null) continue;
            if (hosting.Primary == AwsHosting.Lambda)
            {
                project.Result.Inventory.Add(new InventoryItem
                {
                    Project = project.Result.Project.Name, Severity = InventorySeverity.Info, Category = InventoryCategory.ProjectFile, RuleId = "AWS-LAMBDA",
                    Title = "Recomendado como AWS Lambda: sem Dockerfile, empacotar com Amazon.Lambda.Tools",
                    Description = "Automação orientada a evento; o ponto de entrada precisa virar um handler (Amazon.Lambda.Core) com o gatilho indicado na arquitetura.",
                    Suggestion = "Se preferir manter o Main() sem alterações, use a alternativa 'tarefa ECS agendada' e gere o Dockerfile rodando com --cloud aws após ajustar."
                });
                continue;
            }
            var dependencies = closure.Where(c => c.Result.OutputProjectPath != null).Select(c => c.Result.OutputProjectPath!).ToList();
            var dockerfile = AwsArchitect.Dockerfile(project.Result.Project, hosting, merged, project.Result.OutputProjectPath, dependencies);
            project.Plan.Write(Path.Combine(project.Result.RelativeDir, "Dockerfile"), dockerfile);
            if (!dockerIgnoreWritten)
            {
                project.Plan.Write(".dockerignore", AwsArchitect.DockerIgnore());
                dockerIgnoreWritten = true;
            }
            hosting.DockerfileGenerated = true;
            project.Result.Inventory.Add(new InventoryItem
            {
                Project = project.Result.Project.Name, Severity = InventorySeverity.Info, Category = InventoryCategory.ProjectFile, RuleId = "AWS-DOCKERFILE",
                Title = $"Dockerfile gerado ({(hosting.RequiresWindows ? "imagem Windows" : "imagem Linux")}) para {hosting.Primary.Short()}",
                Description = "Build multi-stage a partir da raiz da solução; porta 8080; usuário não-root; TZ/LANG definidos quando a aplicação depende de cultura/fuso.",
                Suggestion = "Veja a seção 'Arquitetura alvo (AWS)' do relatório para os pré-requisitos antes do primeiro deploy.", AutoMigrated = true,
                FilePath = "Dockerfile"
            });
        }
        result.Architecture = AwsArchitect.Propose(result, profiles);
        return profiles;
    }

    // Connection strings live in the host while SqlConnection is opened in libraries, so this is decided solution-wide.
    private static bool UsesSystemDataSqlClient(IEnumerable<ProjectInfo> projects) =>
        projects.Any(p =>
            p.Packages.Any(pkg => pkg.Id.Equals("System.Data.SqlClient", StringComparison.OrdinalIgnoreCase)) ||
            p.SourceFiles.Any(f => File.Exists(f.FullPath) && File.ReadAllText(f.FullPath).Contains("System.Data.SqlClient", StringComparison.Ordinal)));

    private static void PrepareOutputDirectory(string output, string root, bool force)
    {
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var outWithSep = output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (outWithSep.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase) || rootWithSep.StartsWith(outWithSep, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"A pasta de saída ({output}) não pode ficar dentro da pasta de origem nem contê-la ({root}).");

        if (!Directory.Exists(output) || !Directory.EnumerateFileSystemEntries(output).Any())
        {
            Directory.CreateDirectory(output);
            return;
        }

        if (!File.Exists(Path.Combine(output, WorkspaceLoader.OutputMarkerFile)))
            throw new InvalidOperationException($"A pasta de saída {output} já existe e não foi criada pelo Migrator. Escolha outra pasta com --output.");
        if (!force)
            throw new InvalidOperationException($"A pasta de saída {output} já contém uma migração anterior. Use --force para substituí-la.");

        Directory.Delete(output, recursive: true);
        Directory.CreateDirectory(output);
    }

    private static async Task ApplyAsync(OutputPlan plan, string outputDir)
    {
        foreach (var (source, relative) in plan.Copies)
        {
            var target = Path.Combine(outputDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        foreach (var (relative, content) in plan.Writes)
        {
            var target = Path.Combine(outputDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
    }

    private static string WriteSolution(SolutionResult result, Workspace workspace)
    {
        var solution = new XElement("Solution",
            result.Projects
                .Where(p => p.OutputProjectPath != null)
                .OrderBy(p => p.OutputProjectPath, StringComparer.OrdinalIgnoreCase)
                .Select(p => new XElement("Project", new XAttribute("Path", p.OutputProjectPath!.Replace('\\', '/')))));
        var path = Path.Combine(result.OutputDir!, workspace.Name + ".slnx");
        File.WriteAllText(path, solution + Environment.NewLine);
        return path;
    }

    private static void CopyRootFiles(string root, string output, SolutionResult result)
    {
        foreach (var name in RootFilesToCopy.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var source = ProjectLoader.FindFile(root, name);
            if (source == null) continue;
            var target = Path.Combine(output, Path.GetFileName(source));
            if (File.Exists(target)) continue;
            File.Copy(source, target);
            if (name.StartsWith("Directory.Build", StringComparison.OrdinalIgnoreCase))
                result.GlobalItems.Add(new InventoryItem
                {
                    Project = "(solução)", Severity = InventorySeverity.Warning, Category = InventoryCategory.ProjectFile, RuleId = "SLN-DIRECTORYBUILD",
                    Title = $"{Path.GetFileName(source)} copiado", Description = "Propriedades globais do build antigo foram mantidas.",
                    Suggestion = "Remova configurações específicas do .NET Framework (TargetFrameworkVersion, caminhos de packages/, LangVersion antigo)."
                });
        }
        if (ProjectLoader.FindFile(root, "global.json") != null)
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Info, Category = InventoryCategory.ProjectFile, RuleId = "SLN-GLOBALJSON",
                Title = "global.json não copiado", Description = "O global.json original fixa uma versão antiga do SDK.",
                Suggestion = "Se quiser fixar o SDK, crie um global.json com a versão 10.0.x."
            });
    }

    private static async Task VerifyBuildAsync(SolutionResult result, IReadOnlyList<MigratedProject> ordered, MigrationOptions options,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + options.BuildTimeout;
        var log = new StringBuilder();
        var diagnostics = new List<BuildDiagnostic>();
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var blocked = new Dictionary<ProjectResult, string>();
        var timedOut = false;
        var unexplainedFailures = new List<(string Project, int ExitCode)>();

        foreach (var project in ordered)
        {
            var info = project.Result.Project;
            var failedDependency = PackageAligner.Closure(project, ordered).FirstOrDefault(d => failed.Contains(d.Result.Project.ProjectPath));
            if (failedDependency != null)
            {
                failed.Add(info.ProjectPath);
                blocked[project.Result] = failedDependency.Result.Project.Name;
                continue;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) { timedOut = true; break; }

            progress?.Report($"Build de verificação: {info.Name}...");
            var path = Path.Combine(result.OutputDir!, project.Result.OutputProjectPath!);
            var outcome = await BuildVerifier.BuildAsync(path, result.OutputDir!, remaining, cancellationToken);
            log.AppendLine($"===== dotnet build {project.Result.OutputProjectPath} (código {outcome.ExitCode}) =====").AppendLine(outcome.Log);
            diagnostics.AddRange(outcome.Diagnostics);
            if (outcome.TimedOut) { timedOut = true; break; }
            if (!outcome.Succeeded)
            {
                failed.Add(info.ProjectPath);
                if (outcome.Diagnostics.All(d => d.Severity != "error")) unexplainedFailures.Add((info.Name, outcome.ExitCode));
            }
        }

        Directory.CreateDirectory(result.ReportDir!);
        result.BuildLogPath = Path.Combine(result.ReportDir!, "build-verification.log");
        await File.WriteAllTextAsync(result.BuildLogPath, log.ToString(), cancellationToken);

        var unique = diagnostics.DistinctBy(d => $"{d.Code}|{d.File}|{d.Line}|{d.Message}|{d.ProjectPath}").ToList();
        AttachBuildResults(result, unique, blocked);
        result.BuildSucceeded = failed.Count == 0 && !timedOut;

        foreach (var (project, blocker) in blocked)
            project.Inventory.Add(new InventoryItem
            {
                Project = project.Project.Name, Severity = InventorySeverity.Warning, Category = InventoryCategory.Build, RuleId = "BUILD-BLOCKED",
                Title = $"Não compilado: depende de {blocker}, que tem erros",
                Description = "Os erros deste projeto só aparecerão depois que a dependência compilar.",
                Suggestion = $"Corrija primeiro {blocker} e execute 'dotnet build' novamente (ou rode a ferramenta outra vez com --force)."
            });
        foreach (var (name, exitCode) in unexplainedFailures)
            result.GlobalItems.Add(new InventoryItem
            {
                Project = name, Severity = InventorySeverity.Breaking, Category = InventoryCategory.Build, RuleId = "BUILD-FAILED",
                Title = $"Build de {name} falhou (código {exitCode}) sem diagnósticos reconhecidos",
                Description = "Veja a seção correspondente em build-verification.log.", Suggestion = "Consulte build-verification.log."
            });
        if (timedOut)
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Warning, Category = InventoryCategory.Build, RuleId = "BUILD-TIMEOUT",
                Title = "Build de verificação excedeu o tempo limite", Description = "Parte dos projetos não foi compilada.",
                Suggestion = "Execute 'dotnet build' manualmente na pasta de saída ou aumente --build-timeout."
            });
    }

    private static void AttachBuildResults(SolutionResult result, IReadOnlyList<BuildDiagnostic> diagnostics, IReadOnlyDictionary<ProjectResult, string> blocked)
    {
        var byProject = result.Projects
            .Where(p => p.OutputProjectPath != null)
            .ToDictionary(p => Path.GetFullPath(Path.Combine(result.OutputDir!, p.OutputProjectPath!)), p => p, StringComparer.OrdinalIgnoreCase);
        var byDirectory = byProject
            .Select(kv => (Dir: Path.GetDirectoryName(kv.Key)! + Path.DirectorySeparatorChar, Project: kv.Value))
            .OrderByDescending(x => x.Dir.Length)
            .ToList();

        var counters = result.Projects.ToDictionary(p => p, _ => (Errors: 0, Warnings: 0));

        foreach (var diagnostic in diagnostics)
        {
            ProjectResult? owner = null;
            if (diagnostic.ProjectPath != null) byProject.TryGetValue(Path.GetFullPath(diagnostic.ProjectPath), out owner);
            if (owner == null && diagnostic.File != null)
                owner = byDirectory.FirstOrDefault(x => diagnostic.File.StartsWith(x.Dir, StringComparison.OrdinalIgnoreCase)).Project;

            var isError = diagnostic.Severity == "error";
            if (owner != null)
            {
                var c = counters[owner];
                counters[owner] = isError ? (c.Errors + 1, c.Warnings) : (c.Errors, c.Warnings + 1);
            }
            if (!isError && !BuildVerifier.IsRelevantWarning(diagnostic.Code)) continue;

            var projectDir = owner != null ? Path.GetDirectoryName(Path.Combine(result.OutputDir!, owner.OutputProjectPath!))! : result.OutputDir!;
            var item = new InventoryItem
            {
                Project = owner?.Project.Name ?? "(solução)",
                Severity = isError ? InventorySeverity.Breaking : InventorySeverity.Warning,
                Category = InventoryCategory.Build,
                RuleId = diagnostic.Code,
                Title = $"{diagnostic.Code}: {StartupAnalyzer.Shorten(diagnostic.Message)}",
                Description = diagnostic.Message,
                Suggestion = BuildHints.Suggest(diagnostic.Code, diagnostic.Message),
                FilePath = diagnostic.File != null ? Path.GetRelativePath(projectDir, diagnostic.File) : null,
                Line = diagnostic.Line
            };
            (owner?.Inventory ?? result.GlobalItems).Add(item);
        }

        foreach (var (project, (errors, warnings)) in counters)
            project.Build = new ProjectBuildStatus(errors, warnings, blocked.GetValueOrDefault(project));
    }
}
