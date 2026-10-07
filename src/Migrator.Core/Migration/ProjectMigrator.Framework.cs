using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Cloud;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>
/// <c>--target framework</c>: the code is not touched. Every project is copied as-is and only its target framework is raised to
/// .NET Framework 4.8.1 (project file and config runtime markers). Profiling, modernization advice, the data-access inventory and
/// the AWS architecture still run on the original sources, so the report is complete; the hosting becomes EC2 Windows and the
/// infrastructure is CloudFormation (see AwsArchitect.Framework / CloudFormationGenerator).
/// </summary>
public static partial class ProjectMigrator
{
    private static MigratedProject UpgradeFramework(ProjectInfo project, ProjectMigrationContext ctx)
    {
        var result = new ProjectResult { Project = project, RelativeDir = Relative(ctx.RootDir, project.ProjectDir) };
        var plan = new OutputPlan();
        var items = result.Inventory;
        result.OutputProjectPath = Path.Combine(result.RelativeDir, Path.GetFileName(project.ProjectPath));

        var sources = project.SourceFiles.Where(f => File.Exists(f.FullPath))
            .Select(f => (Path.GetRelativePath(project.ProjectDir, f.FullPath).Replace('\\', '/'), TextFiles.Read(f.FullPath).Text)).ToList();
        var profile = ApplicationProfiler.Analyze(project, sources, LoadConfig(project));
        var inspections = InspectBinaries(project, profile);
        // Behaviour changes on .NET 10/Linux (MOD-CS-*) and Windows dependencies with Linux replacements (MOD-WIN-*) do not apply when the code stays on .NET Framework/Windows.
        result.Modernizations.AddRange(ModernizationAdvisor.Analyze(project, profile, sources, ctx.Cloud)
            .Where(m => !m.RuleId.StartsWith("MOD-CS-", StringComparison.Ordinal) && !m.RuleId.StartsWith("MOD-WIN-", StringComparison.Ordinal)));
        result.DataScan = DataAccessAnalyzer.Scan(project, sources.Concat(DataFiles(project)));
        result.Settings.AddRange(ConfigSettingsCollector.Collect(project, LoadConfig(project)));

        // Fixed URLs/e-mails/credentials in C# literals → ConfigurationManager.AppSettings[...] (valid on .NET Framework); keys go to the config below.
        var session = new LiteralExternalizer.Session();
        var rewrittenSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!project.IsVisualBasic)
            foreach (var file in project.SourceFiles.Where(f => File.Exists(f.FullPath) && f.IsInside(project.ProjectDir)))
            {
                var (text, _) = TextFiles.Read(file.FullPath);
                var externalized = LiteralExternalizer.Externalize(text, Path.GetRelativePath(project.ProjectDir, file.FullPath).Replace('\\', '/'), session);
                if (externalized != text) rewrittenSources[file.FullPath] = externalized;
            }
        ReportExternalized(project, session, result, ctx);
        var codeSecrets = new SecretsPlan();
        var configAdditions = new List<(string Key, string Value)>();
        foreach (var literal in session.Literals.Where(l => l.Rewritten).DistinctBy(l => l.Key))
        {
            if (literal.Kind == SettingKind.Secret && !ctx.KeepSecrets)
            {
                var secret = SecretsExtractor.ForCode(ctx.SolutionName, project.Name, LiteralExternalizer.ConfigPath(literal.Key), literal.Value);
                codeSecrets.Secrets.Add(secret);
                configAdditions.Add((literal.Key, $"{SecretsExtractor.PlaceholderPrefix}{secret.SecretName}>"));
            }
            else configAdditions.Add((literal.Key, literal.Value));
        }
        if (codeSecrets.Any)
        {
            result.Secrets = codeSecrets;
            WriteSecretsArtifacts(project, codeSecrets, plan, result.RelativeDir, items);
        }
        if (project.Kind == ProjectKind.Web)
        {
            var markup = project.Items.Select(i => Path.GetRelativePath(project.ProjectDir, i.FullPath)).Where(r => WebFormsMarkup.Contains(Path.GetExtension(r))).ToList();
            var pages = markup.Count(m => Path.GetExtension(m).ToLowerInvariant() is ".aspx" or ".ascx" or ".master");
            if (pages > 0)
                items.Add(Item(project, InventorySeverity.Info, InventoryCategory.View, "WEB-WEBFORMS-KEPT",
                    $"Web Forms mantido ({pages} arquivo(s) .aspx/.ascx/.master)", "No destino .NET Framework 4.8.1 as páginas continuam funcionando no IIS sem alteração.",
                    "A reescrita (Razor Pages/Blazor) só é necessária na migração futura para .NET 10."));
        }

        // Every file of the project folder goes to the output; the project file and the config files are rewritten in memory.
        var rewritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.ProjectPath };
        var configFiles = new List<string>();
        if (project.ConfigFilePath != null) configFiles.Add(project.ConfigFilePath);
        configFiles.AddRange(project.ConfigTransformFiles);
        foreach (var c in configFiles) rewritten.Add(c);

        foreach (var file in ProjectLoader.EnumerateProjectDirectory(project.ProjectDir))
        {
            if (rewritten.Contains(file)) continue;
            if (Path.GetExtension(file).ToLowerInvariant() is ".user" or ".suo" or ".vspscc" or ".vssscc") continue;
            if (rewrittenSources.TryGetValue(file, out var text)) plan.Write(Path.Combine(result.RelativeDir, Path.GetRelativePath(project.ProjectDir, file)), text);
            else plan.Copy(file, Path.Combine(result.RelativeDir, Path.GetRelativePath(project.ProjectDir, file)));
        }
        // Linked files and local DLLs living elsewhere inside the solution keep their relative paths.
        var root = ctx.RootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var item in project.Items.Where(i => !i.IsInside(project.ProjectDir) && File.Exists(i.FullPath) && i.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            plan.Copy(item.FullPath, Path.GetRelativePath(ctx.RootDir, item.FullPath));
        foreach (var binary in project.BinaryReferences.Where(b => b.Exists && !b.HintPath.StartsWith(project.ProjectDir, StringComparison.OrdinalIgnoreCase) && b.HintPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
            foreach (var sibling in new[] { binary.HintPath, Path.ChangeExtension(binary.HintPath, ".xml"), Path.ChangeExtension(binary.HintPath, ".pdb") }.Where(File.Exists))
                plan.Copy(sibling, Path.GetRelativePath(ctx.RootDir, sibling));

        var (projectText, _) = TextFiles.Read(project.ProjectPath);
        var (upgraded, before) = FrameworkProjectRewriter.Rewrite(projectText, project.IsSdkStyle);
        if (rewrittenSources.Count > 0) upgraded = EnsureSystemConfigurationReference(upgraded, project.IsSdkStyle);
        plan.Write(result.OutputProjectPath, upgraded);
        if (project.IsAlreadyModern)
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.ProjectFile, "PRJ-FX-MODERN",
                $"Projeto já é .NET moderno ({project.TargetFramework}): copiado sem alteração",
                "O destino .NET Framework 4.8.1 só se aplica a projetos .NET Framework; este projeto segue no framework atual.",
                "Confirme se os demais projetos que o referenciam compilam contra ele (multi-target netstandard2.0/net481 se necessário)."));
        else if (before == null)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-FX-CURRENT",
                "Projeto já está em .NET Framework 4.8.1", "Nenhuma alteração no arquivo de projeto.", "Nenhuma ação necessária.", auto: true));
        else
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-FX-UPGRADE",
                $"{Path.GetFileName(project.ProjectPath)}: {before} → {TargetText.FrameworkVersion} (código não alterado)",
                "Apenas o TargetFrameworkVersion foi atualizado; packages.config, referências e arquivos permanecem como no original. O .NET Framework 4.8.1 é compatível em binário com as versões 4.x anteriores.",
                "Compile com MSBuild/Visual Studio (o build de verificação com 'dotnet build' não se aplica a projetos .NET Framework). Confira avisos de pacotes que exigem versão mínima do framework e comportamentos que mudaram desde a versão de origem (TLS 1.2 padrão, criptografia, Regex).", auto: true));

        if (configAdditions.Count > 0 && project.ConfigFilePath == null)
        {
            // No config file at all: create a minimal one next to the project so the keys exist (web.config for web, app.config otherwise).
            var name = project.Kind == ProjectKind.Web ? "web.config" : "app.config";
            plan.Write(Path.Combine(result.RelativeDir, name), AddAppSettings("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n</configuration>\n", configAdditions));
        }
        foreach (var config in configFiles)
        {
            var (text, _) = TextFiles.Read(config);
            var (updated, changed) = FrameworkProjectRewriter.RewriteConfig(text);
            if (config.Equals(project.ConfigFilePath, StringComparison.OrdinalIgnoreCase)) updated = AddAppSettings(updated, configAdditions);
            plan.Write(Path.Combine(result.RelativeDir, Path.GetRelativePath(project.ProjectDir, config)), updated);
            if (changed.Count > 0)
                items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Configuration, "CFG-FX-RUNTIME",
                    $"{Path.GetFileName(config)}: runtime apontado para 4.8.1 ({string.Join(", ", changed)})",
                    "supportedRuntime/httpRuntime/compilation targetFramework atualizados; o restante do arquivo foi mantido.",
                    "Nenhuma ação necessária.", Path.GetFileName(config), auto: true));
        }

        ReportBinaryCompatibility(project, inspections, items);
        if (profile.HasAny(Signal.SecretsInConfig, Signal.SecretsInCode))
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Configuration, "CFG-FX-SECRETS",
                "Credenciais permanecem no web.config/app.config",
                "No destino .NET Framework o arquivo de configuração não é convertido, então as senhas continuam no arquivo copiado.",
                "Use os scripts do CodeDeploy gerados em infra/codedeploy/ (after-install.ps1 lê o segredo <app>/<projeto>/config no Secrets Manager e grava no config da instância) e remova as senhas do repositório.",
                project.ConfigFilePath != null ? Path.GetFileName(project.ConfigFilePath) : null));

        return new MigratedProject(result, null, plan, profile);
    }
}

/// <summary>Text-level rewrites that keep the original formatting of the project/config files.</summary>
public static partial class FrameworkProjectRewriter
{
    /// <summary>Returns the rewritten project file and the framework it had before (null when nothing changed).</summary>
    public static (string Text, string? Before) Rewrite(string projectXml, bool sdkStyle)
    {
        string? before = null;
        var text = OldStyleVersion().Replace(projectXml, m =>
        {
            if (m.Groups["v"].Value.Equals(TargetText.FrameworkVersion, StringComparison.OrdinalIgnoreCase)) return m.Value;
            before ??= m.Groups["v"].Value;
            return m.Groups["open"].Value + TargetText.FrameworkVersion + m.Groups["close"].Value;
        });
        if (sdkStyle)
            text = SdkStyleMoniker().Replace(text, m =>
            {
                if (m.Groups["tfm"].Value.Equals(TargetText.FrameworkMoniker, StringComparison.OrdinalIgnoreCase)) return m.Value;
                before ??= m.Groups["tfm"].Value;
                return TargetText.FrameworkMoniker;
            });
        // Multi-targeting: net45;net48 → a single net481.
        text = Regex.Replace(text, @"(?<=<TargetFrameworks>)[^<]+(?=</TargetFrameworks>)", m =>
            string.Join(";", m.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct()));
        return (text, before);
    }

    public static (string Text, List<string> Changed) RewriteConfig(string configXml)
    {
        var changed = new List<string>();
        var text = SupportedRuntimeSku().Replace(configXml, m =>
        {
            if (m.Groups["v"].Value == "v4.8.1") return m.Value;
            changed.Add($"supportedRuntime {m.Groups["v"].Value}→v4.8.1");
            return m.Groups["open"].Value + "v4.8.1" + m.Groups["close"].Value;
        });
        text = TargetFrameworkAttribute().Replace(text, m =>
        {
            if (m.Groups["v"].Value == "4.8.1") return m.Value;
            changed.Add($"{m.Groups["element"].Value} {m.Groups["v"].Value}→4.8.1");
            return m.Groups["open"].Value + "4.8.1" + m.Groups["close"].Value;
        });
        return (text, changed);
    }

    [GeneratedRegex(@"(?<open><TargetFrameworkVersion(?:\s[^>]*)?>)(?<v>v\d+(?:\.\d+){1,2})(?<close></TargetFrameworkVersion>)")]
    private static partial Regex OldStyleVersion();

    [GeneratedRegex(@"(?<=<TargetFrameworks?>[^<]*)(?<tfm>net4\d{1,2})(?=[^<]*</TargetFrameworks?>)")]
    private static partial Regex SdkStyleMoniker();

    [GeneratedRegex(@"(?<open><supportedRuntime\b[^>]*sku=""\.NETFramework,Version=)(?<v>v[\d.]+)(?<close>"")")]
    private static partial Regex SupportedRuntimeSku();

    [GeneratedRegex(@"(?<open><(?<element>httpRuntime|compilation)\b[^>]*targetFramework="")(?<v>[\d.]+)(?<close>"")")]
    private static partial Regex TargetFrameworkAttribute();
}
