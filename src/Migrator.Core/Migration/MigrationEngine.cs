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
            result.OutputDir = Path.GetFullPath(options.OutputDir ?? Path.Combine(parent, workspace.Name + (options.KeepsFramework ? ".net481" : ".net10")));
            PrepareOutputDirectory(result.OutputDir, workspace.RootDir, options.Force);
            // Repository layout of the platform: the solution under app/src, infra/ tests/ .github/ and the pipeline descriptor at the root.
            result.SourceDir = Path.Combine(result.OutputDir, CloudFormationGenerator.SourceDir.Replace('/', Path.DirectorySeparatorChar));
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
        var (nugetConfig, nugetSource) = ResolveNuGetSource(options, workspace.RootDir, result);
        result.NuGetSource = nugetSource.ToString();
        using var nuget = new NuGetClient(options.Offline, nugetSource);
        var context = new ProjectMigrationContext(workspace.RootDir, map, new PackagePlanner(nuget), UsesSystemDataSqlClient(projects), options.Cloud, workspace.Name, options.KeepSecrets, options.Target);

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
                Title = $"Feed NuGet inacessível: {nuget.Source.Name}", Description = $"A compatibilidade dos pacotes não pôde ser verificada em {nuget.Source.IndexUrl}.",
                Suggestion = nuget.Source.IsNuGetOrg
                    ? "Configure o proxy (HTTPS_PROXY) ou informe o nuget.config do feed privado (--nuget-config) e execute novamente; enquanto isso, confie no build de verificação (avisos NU1701)."
                    : "Confira a URL do service index (deve terminar em index.json), as credenciais do nuget.config e o acesso de rede (VPN/proxy); enquanto isso, confie no build de verificação (avisos NU1701)."
            });

        progress?.Report("Consolidando bancos, tabelas e campos acessados...");
        DataAccessAnalyzer.Resolve(migrated);

        progress?.Report("Alinhando versões de pacotes entre projetos...");
        var ordered = PackageAligner.TopologicalOrder(migrated);
        await PackageAligner.AlignAsync(ordered, nuget);
        foreach (var project in migrated)
        {
            // What a deployable needs at runtime: its own settings plus those of every library it ships.
            var all = project.Result.Settings.Concat(PackageAligner.Closure(project, ordered).SelectMany(c => c.Result.Settings)).DistinctBy(s => s.Key, StringComparer.OrdinalIgnoreCase);
            project.Result.SettingsWithDependencies.AddRange(all);
        }
        List<(ProjectResult Result, ApplicationProfile Profile)>? profiles = null;
        if (options.Cloud == CloudTarget.Aws)
        {
            progress?.Report("Avaliando arquitetura alvo na AWS...");
            profiles = AdviseCloud(result, migrated, ordered); // may add Lambda packages/properties to the specs
            result.Deployment = CloudFormationGenerator.Guide(result, profiles);
        }

        foreach (var project in migrated.Where(p => p.Spec != null))
            project.Plan.Write(project.Result.OutputProjectPath!, ProjectFileWriter.Write(project.Spec!));

        if (profiles != null && !options.DryRun && options.GenerateInfrastructure)
        {
            var cloudFormation = options.EffectiveIac == IacTool.CloudFormation;
            progress?.Report($"Gerando infraestrutura como código ({options.EffectiveIac.Display()}) e pipeline...");
            var files = cloudFormation ? CloudFormationGenerator.Generate(result, profiles) : InfrastructureGenerator.Generate(result, profiles);
            if (!cloudFormation) foreach (var (path, content) in CloudFormationGenerator.PlatformFiles(result, profiles)) files[path] = content; // pipeline descriptor + TAAC specs come with any IaC
            var carrier = migrated.FirstOrDefault(m => m.Result.OutputProjectPath != null);
            if (files.Count > 0 && carrier != null)
            {
                foreach (var (path, content) in files) carrier.Plan.Write(path, content);
                result.GlobalItems.Add(cloudFormation
                    ? new InventoryItem
                    {
                        Project = "(solução)", Severity = InventorySeverity.Info, Category = InventoryCategory.ProjectFile, RuleId = "AWS-INFRA",
                        Title = $"Infraestrutura como código gerada: {files.Count(f => (f.Key.EndsWith(".yaml", StringComparison.Ordinal) || f.Key.EndsWith(".yml", StringComparison.Ordinal)) && f.Key.StartsWith("infra/", StringComparison.Ordinal))} templates CloudFormation + pipeline de deploy",
                        Description = options.KeepsFramework
                            ? "infra/service*.yml (EC2 Windows em Auto Scaling, regra no ALB compartilhado, CodeDeploy, Parameter Store, alarmes), infra/data.yml (RDS, S3/FSx, segredos), infra/{dev,hom,prod}/parameters*.json, infra/codedeploy/ (appspec + scripts PowerShell por projeto), .iupipes.yml, tests/ e .github/workflows/deploy.yml; o guia de implantação do relatório e o README da saída dizem o que preencher."
                            : "infra/service*.yml (ECS Fargate no padrão da plataforma), infra/lambda-*.yml, infra/data.yml (RDS, S3, filas, segredos), infra/{dev,hom,prod}/parameters*.json, .iupipes.yml, tests/ e .github/workflows/deploy.yml; o guia de implantação do relatório e o README da saída dizem o que preencher.",
                        Suggestion = "Preencha infra/<env>/parameters*.json e .iupipes.yml, rode infra/deploy.sh (ou deploy.ps1) na ordem indicada e coloque os valores nos segredos antes do primeiro deploy (seção Guia de implantação).", AutoMigrated = true, FilePath = "infra/README.md"
                    }
                    : new InventoryItem
                    {
                        Project = "(solução)", Severity = InventorySeverity.Info, Category = InventoryCategory.ProjectFile, RuleId = "AWS-INFRA",
                        Title = $"Infraestrutura como código gerada: {files.Count(f => f.Key.EndsWith(".tf", StringComparison.Ordinal))} arquivos Terraform + workflow de deploy",
                        Description = "infra/terraform/ (VPC, ECS/ALB, tarefas agendadas, workers, Lambda, RDS, S3, ElastiCache, IAM, alarmes) e .github/workflows/deploy.yml, parametrizados por variáveis; infra/README.md traz a ordem de execução.",
                        Suggestion = "Revise terraform.tfvars.example, configure o backend remoto e crie os segredos (_secrets/*/create-secrets.sh) antes do primeiro apply.", AutoMigrated = true, FilePath = "infra/README.md"
                    });
            }
        }

        if (!options.DryRun)
        {
            foreach (var project in migrated) await ApplyAsync(project.Plan, result.OutputDir!, result.SourceDir!);
            WriteSolution(result, workspace);
            CopyRootFiles(workspace.RootDir, result.SourceDir!, result);
            WriteNuGetConfig(nugetConfig, nugetSource, result);
            WriteRootGitIgnore(result.OutputDir!);
            WriteRootFiles(result, result.OutputDir!);
            await File.WriteAllTextAsync(Path.Combine(result.OutputDir!, WorkspaceLoader.OutputMarkerFile),
                $"Gerado pelo Migrator em {DateTime.Now:O} a partir de {workspace.RootDir}{Environment.NewLine}", cancellationToken);

            if (llm != null && !options.KeepsFramework) await new LlmCodeDrafter(llm, options.Llm, result.SourceDir!).DraftAsync(result, progress, cancellationToken);

            if (options.VerifyBuild && options.KeepsFramework)
            {
                // Old-style projects with packages.config need MSBuild + nuget restore on Windows; 'dotnet build' cannot verify them.
                result.BuildSkippedReason = "destino .NET Framework (compile com MSBuild/Visual Studio)";
                result.GlobalItems.Add(new InventoryItem
                {
                    Project = "(solução)", Severity = InventorySeverity.Info, Category = InventoryCategory.Build, RuleId = "BUILD-FX-SKIPPED",
                    Title = "Build de verificação não executado: projetos .NET Framework",
                    Description = "O código não foi alterado; a atualização para 4.8.1 é compatível em binário e exige MSBuild (Windows) para compilar.",
                    Suggestion = "Abra a saída no Visual Studio ou rode 'nuget restore' + 'msbuild /p:Configuration=Release' (o workflow gerado faz isso em um runner Windows)."
                });
            }
            else if (options.VerifyBuild && ordered.Count > 0 && !await FeedReachableAsync(nugetSource, result, progress, cancellationToken))
            {
                // Nothing restores without the feed: skipping is faster and clearer than a 30-minute cascade of timeouts.
            }
            else if (options.VerifyBuild && ordered.Count > 0)
            {
                await VerifyBuildAsync(result, ordered, options, progress, cancellationToken);
                if (llm != null && result.BuildSucceeded == false)
                    await FixBuildWithLlmAsync(llm, result, ordered, options, progress, cancellationToken);
                await VerifyRuntimeAsync(result, options, progress, cancellationToken);
            }
        }

        if (llm != null)
            result.LlmTriagedItems = await LlmTriage.TriageAsync(llm, result, progress, cancellationToken);
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
    /// <summary>Beyond compiling: run migrated tests, probe /health of web apps, optionally build the Dockerfiles.</summary>
    private static async Task VerifyRuntimeAsync(SolutionResult result, MigrationOptions options, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var built = result.Projects.Where(p => p.Build is { Succeeded: true } && p.OutputProjectPath != null).ToList();
        foreach (var project in built)
        {
            var projectPath = Path.Combine(result.SourceDir!, project.OutputProjectPath!);
            var projectDir = Path.GetDirectoryName(projectPath)!;
            var assembly = string.IsNullOrEmpty(project.Project.AssemblyName) ? project.Project.Name : project.Project.AssemblyName;

            if (options.RunTests && project.Project.Kind == ProjectKind.Test)
            {
                progress?.Report($"Executando testes: {project.Project.Name}...");
                var tests = await RuntimeVerifier.RunTestsAsync(projectPath, result.SourceDir!, TimeSpan.FromMinutes(10), cancellationToken);
                project.Tests = tests;
                project.Inventory.Add(tests.Succeeded
                    ? Runtime(project, InventorySeverity.Info, "TEST-RUN", $"Testes migrados executados: {tests.Passed} passaram{(tests.Skipped > 0 ? $", {tests.Skipped} ignorados" : "")}",
                        "O projeto de testes compilou e todos os testes passaram no .NET 10.", "Nenhuma ação necessária.", auto: true)
                    : Runtime(project, tests.Total == 0 && !tests.TimedOut ? InventorySeverity.Info : InventorySeverity.Warning, "TEST-FAILED",
                        tests.TimedOut ? "Execução dos testes excedeu 10 minutos" : tests.Total == 0 ? "Nenhum teste foi descoberto/executado" : $"Testes migrados: {tests.Failed} falharam, {tests.Passed} passaram",
                        tests.FailedTests.Count > 0 ? "Falhas: " + string.Join("; ", tests.FailedTests.Take(10)) : "Veja a saída do dotnet test.",
                        tests.Total == 0 ? "Confira se o adapter de testes (MSTest.TestAdapter / NUnit3TestAdapter / xunit.runner.visualstudio) foi adicionado e se os testes dependem de recursos locais (banco, arquivos)." : "Testes que falham após a migração costumam indicar mudança de comportamento (cultura, fuso, caminhos, serialização); compare com a execução no .NET Framework."));
            }

            if (options.SmokeTest && project.Project.Kind == ProjectKind.Web)
            {
                progress?.Report($"Smoke test (/health): {project.Project.Name}...");
                var dll = Path.Combine(projectDir, "bin", "Debug", "net10.0", assembly + ".dll");
                var smoke = await RuntimeVerifier.SmokeTestWebAsync(dll, TimeSpan.FromSeconds(45), cancellationToken);
                project.Smoke = smoke;
                project.Inventory.Add(smoke.Succeeded
                    ? Runtime(project, InventorySeverity.Info, "SMOKE-OK", "Aplicação sobe e responde em /health", smoke.Detail, "Nenhuma ação necessária.", auto: true)
                    : Runtime(project, InventorySeverity.Warning, "SMOKE-FAILED", "Aplicação não subiu ou /health não respondeu", smoke.Detail,
                        "Erros de inicialização vêm quase sempre de registros de DI faltando (serviços criados manualmente no Global.asax), configuração ausente ou conexão aberta no startup. Rode 'dotnet run' na pasta do projeto para ver a exceção completa."));
            }
        }

        if (options.VerifyDocker)
        {
            var withDockerfile = result.Projects.Where(p => p.Hosting is { DockerfileGenerated: true } && p.OutputProjectPath != null).ToList();
            if (withDockerfile.Count > 0 && !RuntimeVerifier.DockerAvailable())
                result.GlobalItems.Add(new InventoryItem
                {
                    Project = "(solução)", Severity = InventorySeverity.Warning, Category = InventoryCategory.Build, RuleId = "DOCKER-UNAVAILABLE",
                    Title = "Docker não disponível: imagens não foram construídas", Description = "--verify-docker foi pedido, mas 'docker version' falhou.",
                    Suggestion = "Inicie o Docker Desktop/daemon e execute novamente, ou valide as imagens no pipeline de CI."
                });
            else
                foreach (var project in withDockerfile)
                {
                    progress?.Report($"docker build: {project.Project.Name}...");
                    var dockerfile = Path.Combine(project.RelativeDir, "Dockerfile");
                    var tag = $"migrator/{project.Project.Name.ToLowerInvariant()}:verify";
                    var build = await RuntimeVerifier.DockerBuildAsync(dockerfile, result.SourceDir!, tag, TimeSpan.FromMinutes(20), cancellationToken);
                    project.DockerBuildSucceeded = build.ExitCode == 0;
                    project.Inventory.Add(build.ExitCode == 0
                        ? Runtime(project, InventorySeverity.Info, "DOCKER-OK", $"Imagem Docker construída ({tag})", "docker build concluído com o Dockerfile gerado.", "Nenhuma ação necessária.", auto: true)
                        : Runtime(project, InventorySeverity.Warning, "DOCKER-FAILED", build.TimedOut ? "docker build excedeu 20 minutos" : $"docker build falhou (código {build.ExitCode})",
                            RuntimeVerifier.Tail(build.Output), "Erros de restore/compilação dentro da imagem repetem os do build de verificação; erros de COPY indicam arquivo fora do contexto (raiz da solução)."));
                }
        }
    }

    private static InventoryItem Runtime(ProjectResult project, InventorySeverity severity, string rule, string title, string description, string suggestion, bool auto = false) => new()
    {
        Project = project.Project.Name, Severity = severity, Category = InventoryCategory.Build, RuleId = rule, Title = title, Description = description, Suggestion = suggestion, AutoMigrated = auto
    };

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
        var fixer = new LlmCodeFixer(llm, options.Llm, result.SourceDir!, result.ReportDir!);
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
            var hosting = AwsArchitect.Recommend(project.Result.Project, merged, result.Options.Target, result.Options.Serverless);
            project.Result.Hosting = hosting;
            profiles.Add((project.Result, merged));

            if (hosting.Primary is AwsHosting.NotDeployable or AwsHosting.Desktop || project.Result.OutputProjectPath == null || result.Options.KeepsFramework) continue;
            if (hosting.Primary == AwsHosting.Lambda)
            {
                if (project.Spec != null)
                    LambdaScaffolder.Scaffold(project.Result.Project, merged, project.Spec, project.Plan, project.Result.RelativeDir, project.Result.Inventory);
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
        foreach (var (_, profile) in profiles)
        {
            foreach (var db in profile.Databases) if (!result.Databases.Contains(db)) result.Databases.Add(db);
            foreach (var host in profile.ExternalEndpoints.Select(e => Uri.TryCreate(e, UriKind.Absolute, out var u) ? u.Host : e).Where(ModernizationAdvisor.IsInternalHost)) result.InternalHosts.Add(host);
            foreach (var db in profile.Databases.Select(d => (d.Server ?? "").Split(',')[0].Split('\\')[0]).Where(h => ModernizationAdvisor.IsInternalHost(h) && !h.Contains("localdb", StringComparison.OrdinalIgnoreCase))) result.InternalHosts.Add(db);
        }
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

    /// <summary>Repository-level artifacts (infra, pipeline, tests specs, secrets) stay at the root; everything else is the solution under app/src.</summary>
    internal static bool IsRepositoryRootPath(string relativePath)
    {
        var unix = relativePath.Replace('\\', '/');
        return unix.StartsWith("infra/", StringComparison.Ordinal) || unix.StartsWith(".github/", StringComparison.Ordinal) || unix.StartsWith("tests/", StringComparison.Ordinal)
            || unix.StartsWith(SecretsExtractor.RootFolder + "/", StringComparison.Ordinal) || unix is ".iupipes.yml" or "skip.ini";
    }

    private static async Task ApplyAsync(OutputPlan plan, string outputDir, string sourceDir)
    {
        foreach (var (source, relative) in plan.Copies)
        {
            var target = Path.Combine(IsRepositoryRootPath(relative) ? outputDir : sourceDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        foreach (var (relative, content) in plan.Writes)
        {
            var target = Path.Combine(IsRepositoryRootPath(relative) ? outputDir : sourceDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: WantsBom(relative)));
        }
    }

    /// <summary>Root files every repository of the platform carries.</summary>
    private static void WriteRootFiles(SolutionResult result, string outputDir)
    {
        var attributes = Path.Combine(outputDir, ".gitattributes");
        if (!File.Exists(attributes)) File.WriteAllText(attributes, "* text=auto\n*.sh text eol=lf\n*.ps1 text eol=crlf\n*.cs diff=csharp\n");
        var readme = Path.Combine(outputDir, "README.md");
        if (File.Exists(readme)) return;
        var sb = new System.Text.StringBuilder();
        sb.Append($"# {result.SolutionName}\n\nRepositório gerado pelo Migrator ({result.Options.Target.Display()}, infraestrutura em {result.Options.EffectiveIac.Display()}).\n\n");
        sb.Append("- `app/src/`: a solução (código e projetos); working-directory da esteira.\n");
        sb.Append(result.Options.EffectiveIac == IacTool.CloudFormation
            ? "- `infra/`: CloudFormation da aplicação (`service*.yml`, `data.yml`) e `dev/`, `hom/`, `prod/` com os parâmetros por ambiente; `infra/README.md` traz as convenções da plataforma.\n"
            : "- `infra/terraform/`: módulo Terraform da aplicação; `infra/README.md` traz a ordem de execução.\n");
        sb.Append("- `tests/`: specs dos testes de aceitação (TAAC) executados pela esteira.\n- `.iupipes.yml`: descritor da esteira.\n- `_secrets/`: valores das credenciais retiradas do código/configs (fora do git).\n- `_migration-report/`: inventário, modernização, dados acessados e arquitetura (não versionar).\n\n");
        if (result.Deployment != null) sb.Append(Reporting.MarkdownReport.RenderDeploymentGuide(result, 2));
        File.WriteAllText(readme, sb.ToString().Replace("\r\n", "\n"), new System.Text.UTF8Encoding(false));
    }

    private static void WriteRootGitIgnore(string outputDir)
    {
        var path = Path.Combine(outputDir, ".gitignore");
        var required = new[] { "bin/", "obj/", ".vs/", "*.user", "_migration-report/", SecretsExtractor.RootFolder + "/" };
        var existing = File.Exists(path) ? File.ReadAllLines(path).Select(l => l.Trim()).ToHashSet(StringComparer.Ordinal) : [];
        var missing = required.Where(r => !existing.Contains(r)).ToList();
        if (missing.Count == 0) return;
        var header = existing.Count == 0 ? "# Gerado pelo Migrator" + Environment.NewLine : Environment.NewLine + "# Acrescentado pelo Migrator" + Environment.NewLine;
        File.AppendAllText(path, header + string.Join(Environment.NewLine, missing) + Environment.NewLine);
    }

    /// <summary>Order of precedence: --nuget-config, MIGRATOR_NUGET_CONFIG, nuget.config at the source root. --nuget-source overrides the feed URL.</summary>
    private static (string? ConfigPath, NuGetSource Source) ResolveNuGetSource(MigrationOptions options, string rootDir, SolutionResult result)
    {
        var configPath = options.NuGetConfigPath ?? Environment.GetEnvironmentVariable("MIGRATOR_NUGET_CONFIG");
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            configPath = Path.GetFullPath(configPath);
            if (!File.Exists(configPath)) throw new FileNotFoundException($"nuget.config não encontrado: {configPath}");
        }
        else configPath = NuGetConfigFile.Find(rootDir);

        NuGetSource source;
        if (!string.IsNullOrWhiteSpace(options.NuGetSourceUrl)) source = new NuGetSource("feed configurado", options.NuGetSourceUrl.Trim());
        else if (configPath != null)
        {
            try { source = NuGetConfigFile.Primary(configPath); }
            catch (Exception ex) when (ex is System.Xml.XmlException or InvalidOperationException or IOException)
            {
                result.GlobalItems.Add(new InventoryItem
                {
                    Project = "(solução)", Severity = InventorySeverity.Warning, Category = InventoryCategory.Package, RuleId = "NUGET-CONFIG-INVALID",
                    Title = $"nuget.config inválido: {Path.GetFileName(configPath)}", Description = ex.Message, Suggestion = "Corrija o XML; enquanto isso a ferramenta usa nuget.org."
                });
                source = NuGetSource.NuGetOrg;
            }
        }
        else source = NuGetSource.NuGetOrg;
        return (configPath, source);
    }

    /// <summary>The output always carries a nuget.config when a private feed is involved: copied from the given file, or generated from --nuget-source.</summary>
    private static void WriteNuGetConfig(string? configPath, NuGetSource source, SolutionResult result)
    {
        var target = Path.Combine(result.SourceDir!, "nuget.config");
        var explicitConfig = configPath != null && !Path.GetDirectoryName(configPath)!.Equals(result.RootDir.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        if (explicitConfig)
        {
            foreach (var stale in Directory.EnumerateFiles(result.SourceDir!).Where(f => Path.GetFileName(f).Equals("nuget.config", StringComparison.OrdinalIgnoreCase))) File.Delete(stale);
            File.Copy(configPath!, target, overwrite: true);
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Info, Category = InventoryCategory.Package, RuleId = "NUGET-CONFIG",
                Title = "nuget.config do feed privado copiado para a raiz da saída", Description = $"{configPath} → nuget.config. O restore (local, Docker e CI) usa {source}.",
                Suggestion = "Se o arquivo tiver credenciais em texto claro, prefira variáveis de ambiente (%ARTIFACTORY_TOKEN%) ou o Credential Provider do feed.", AutoMigrated = true, FilePath = "nuget.config"
            });
        }
        else if (!source.IsNuGetOrg && NuGetConfigFile.Find(result.SourceDir!) == null)
        {
            File.WriteAllText(target, $"""
                <?xml version="1.0" encoding="utf-8"?>
                <configuration>
                  <packageSources>
                    <clear />
                    <add key="{source.Name}" value="{source.IndexUrl}" />
                  </packageSources>
                </configuration>

                """.Replace("\r\n", "\n"));
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Info, Category = InventoryCategory.Package, RuleId = "NUGET-CONFIG",
                Title = "nuget.config gerado a partir de --nuget-source", Description = $"Fonte única: {source.IndexUrl}.", Suggestion = "Acrescente credenciais (packageSourceCredentials) se o feed exigir.", AutoMigrated = true, FilePath = "nuget.config"
            });
        }
    }

    private static async Task<bool> FeedReachableAsync(NuGetSource source, SolutionResult result, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        progress?.Report($"Verificando acesso ao feed NuGet ({source.Name})...");
        var (ok, detail) = await NuGetClient.ProbeAsync(source, TimeSpan.FromSeconds(12), cancellationToken: cancellationToken);
        if (ok) return true;
        result.BuildSkippedReason = "feed NuGet inacessível";
        result.GlobalItems.Add(new InventoryItem
        {
            Project = "(solução)", Severity = InventorySeverity.Breaking, Category = InventoryCategory.Build, RuleId = "BUILD-NUGET-UNREACHABLE",
            Title = $"Feed NuGet inacessível: build de verificação não executado ({source.Name})",
            Description = $"{detail}. Sem o feed, cada restore esperaria o timeout do NuGet; a ferramenta pulou o build, os testes e o smoke test.",
            Suggestion = source.IsNuGetOrg
                ? "A rede bloqueia o nuget.org? Informe o nuget.config do feed privado (Artifactory/Nexus) com --nuget-config <arquivo> ou MIGRATOR_NUGET_CONFIG, ou a URL v3 com --nuget-source; ele é copiado para a raiz da saída e usado no restore."
                : "Confira a URL do service index (deve terminar em index.json), as credenciais do nuget.config e o acesso de rede (VPN/proxy)."
        });
        return false;
    }

    /// <summary>Source and project files keep the BOM Visual Studio writes; scripts, Terraform, YAML, Markdown and Docker files must not have one (a BOM breaks "#!" and terraform fmt).</summary>
    private static bool WantsBom(string relativePath)
    {
        var name = Path.GetFileName(relativePath);
        if (name is "Dockerfile" or ".dockerignore" or ".gitignore" or ".editorconfig") return false;
        var unix = relativePath.Replace('\\', '/');
        if (unix.StartsWith("infra/", StringComparison.Ordinal) || unix.StartsWith(".github/", StringComparison.Ordinal)) return false; // YAML/JSON/scripts consumed by the AWS CLI and PowerShell

        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".sh" or ".ps1" or ".tf" or ".tfvars" or ".example" or ".yml" or ".yaml" or ".md" or ".txt" or ".env" => false,
            _ => true
        };
    }

    private static string WriteSolution(SolutionResult result, Workspace workspace)
    {
        var solution = new XElement("Solution",
            result.Projects
                .Where(p => p.OutputProjectPath != null)
                .OrderBy(p => p.OutputProjectPath, StringComparer.OrdinalIgnoreCase)
                .Select(p => new XElement("Project", new XAttribute("Path", p.OutputProjectPath!.Replace('\\', '/')))));
        Directory.CreateDirectory(result.SourceDir!);
        if (result.Options.KeepsFramework && workspace.SolutionFile != null && workspace.SolutionFile.EndsWith(".sln", StringComparison.OrdinalIgnoreCase))
        {
            // Old-style projects stay old-style: the original .sln (same relative paths) is the natural solution file.
            var copy = Path.Combine(result.SourceDir!, Path.GetFileName(workspace.SolutionFile));
            File.Copy(workspace.SolutionFile, copy, overwrite: true);
            return copy;
        }
        var path = Path.Combine(result.SourceDir!, workspace.Name + ".slnx");
        Directory.CreateDirectory(result.SourceDir!);
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
            var path = Path.Combine(result.SourceDir!, project.Result.OutputProjectPath!);
            var outcome = await BuildVerifier.BuildAsync(path, result.SourceDir!, remaining, cancellationToken);
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
            .ToDictionary(p => Path.GetFullPath(Path.Combine(result.SourceDir!, p.OutputProjectPath!)), p => p, StringComparer.OrdinalIgnoreCase);
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

            var projectDir = owner != null ? Path.GetDirectoryName(Path.Combine(result.SourceDir!, owner.OutputProjectPath!))! : result.SourceDir!;
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
