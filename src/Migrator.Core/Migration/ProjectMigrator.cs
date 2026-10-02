using System.Text.RegularExpressions;
using System.Xml.Linq;
using Migrator.Core.Analysis;
using Migrator.Core.Cloud;
using Migrator.Core.Data;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

public sealed class OutputPlan
{
    public List<(string RelativePath, string Content)> Writes { get; } = [];
    public List<(string Source, string RelativePath)> Copies { get; } = [];

    public void Write(string relativePath, string content) => Writes.Add((Normalize(relativePath), content));
    public void Copy(string source, string relativePath) => Copies.Add((source, Normalize(relativePath)));

    private static string Normalize(string path) => path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
}

public sealed record ProjectMigrationContext(string RootDir, IReadOnlyDictionary<string, string> ProjectMap, PackagePlanner Planner, bool PreserveSqlEncryption, CloudTarget Cloud = CloudTarget.Aws, string SolutionName = "app", bool KeepSecrets = false);

public static partial class ProjectMigrator
{
    private enum Role { Code, LegacyCode, View, Static, LegacyMarkup, Copy, ViewsConfig }

    private sealed class Entry
    {
        public required ProjectItem Item { get; init; }
        public required string Relative { get; init; }
        public required Role Role { get; set; }
        public string? Destination { get; set; }
        public bool Linked { get; init; }
        public string? Text { get; set; }
        public bool WasAnsi { get; set; }
        public string Unix => Relative.Replace('\\', '/');
    }

    private static readonly HashSet<string> StaticFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content", "Scripts", "fonts", "Images", "img", "css", "js", "lib", "assets", "media", "static", "dist", "Styles", "icons"
    };

    private static readonly HashSet<string> StaticExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".css", ".js", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg", ".ico", ".woff", ".woff2", ".ttf", ".eot", ".otf", ".webp",
        ".bmp", ".html", ".htm", ".json", ".mp4", ".webm", ".mp3", ".pdf", ".less", ".scss", ".sass", ".txt", ".xml", ".swf", ".cur"
    };

    private static readonly HashSet<string> RootStaticFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "favicon.ico", "robots.txt", "apple-touch-icon.png", "browserconfig.xml", "site.webmanifest", "manifest.json", "sitemap.xml", "humans.txt"
    };

    private static readonly string[] WebFormsCodeSuffixes =
    [
        ".aspx.cs", ".aspx.designer.cs", ".ascx.cs", ".ascx.designer.cs", ".master.cs", ".master.designer.cs",
        ".ashx.cs", ".asmx.cs", ".svc.cs", ".asax.cs"
    ];

    private static readonly HashSet<string> WebFormsMarkup = new(StringComparer.OrdinalIgnoreCase)
    {
        ".aspx", ".ascx", ".master", ".ashx", ".asmx", ".svc", ".asax", ".skin", ".browser"
    };

    private static readonly HashSet<string> NeverCopied = new(StringComparer.OrdinalIgnoreCase)
    {
        "packages.config", ".suo"
    };

    private static readonly string[] InterestingMetadata =
    [
        "Generator", "LastGenOutput", "CustomToolNamespace", "DependentUpon", "AutoGen", "DesignTime", "DesignTimeSharedInput",
        "CopyToOutputDirectory", "CopyToPublishDirectory", "LogicalName"
    ];

    public static async Task<MigratedProject> MigrateAsync(ProjectInfo project, ProjectMigrationContext ctx)
    {
        var result = new ProjectResult { Project = project, RelativeDir = Relative(ctx.RootDir, project.ProjectDir) };
        var plan = new OutputPlan();
        var items = result.Inventory;
        var csprojName = Path.GetFileName(project.ProjectPath);
        result.OutputProjectPath = Path.Combine(result.RelativeDir, csprojName);

        if (project.IsAlreadyModern)
        {
            plan.Copy(project.ProjectPath, result.OutputProjectPath);
            foreach (var file in ProjectLoader.EnumerateProjectDirectory(project.ProjectDir))
                if (!file.Equals(project.ProjectPath, StringComparison.OrdinalIgnoreCase))
                    plan.Copy(file, Path.Combine(result.RelativeDir, Path.GetRelativePath(project.ProjectDir, file)));
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-MODERN",
                $"Projeto já é SDK-style moderno ({project.TargetFramework})", "Copiado sem alterações.",
                "Se ainda não usa net10.0, atualize o TargetFramework e os pacotes Microsoft.* para a linha 10.0.", auto: true));
            var modernCode = project.SourceFiles.Where(f => File.Exists(f.FullPath)).Select(f => (Path.GetRelativePath(project.ProjectDir, f.FullPath).Replace('\\', '/'), TextFiles.Read(f.FullPath).Text)).ToList();
            var modernProfile = ApplicationProfiler.Analyze(project, modernCode, LoadConfig(project));
            result.Modernizations.AddRange(ModernizationAdvisor.Analyze(project, modernProfile, modernCode, ctx.Cloud));
            return new MigratedProject(result, null, plan, modernProfile);
        }

        var isWeb = project.Kind == ProjectKind.Web;
        var entries = Classify(project, ctx, items);
        foreach (var e in entries.Where(e => e.Role is Role.Code or Role.LegacyCode && e.Text == null))
            (e.Text, e.WasAnsi) = TextFiles.Read(e.Item.FullPath);
        MarkInstallerDesigners(entries);
        var codeEntries = entries.Where(e => e.Role == Role.Code).ToList();

        // Architectural profile and modernization advice are computed on the ORIGINAL code (before rewrites),
        // where legacy APIs (System.Messaging, SmtpClient, Session[...]) are still recognizable.
        var originalCode = entries.Where(e => e.Role is Role.Code or Role.LegacyCode).Select(e => (e.Unix, e.Text!)).ToList();
        var profile = ApplicationProfiler.Analyze(project, originalCode, LoadConfig(project));
        result.Modernizations.AddRange(ModernizationAdvisor.Analyze(project, profile, originalCode, ctx.Cloud));

        var catalog = ControllerCatalog.Build(codeEntries.Select(e => e.Text!));
        var legacyCode = entries.Where(e => e.Role == Role.LegacyCode).ToList();
        var startup = isWeb
            ? StartupAnalyzer.Analyze(legacyCode.Select(e => (e.Unix, e.Text!)), project.Name)
            : new StartupPlan();
        items.AddRange(startup.Items);

        var projectFilesUnix = project.Items.Where(i => i.IsInside(project.ProjectDir))
            .Select(i => Path.GetRelativePath(project.ProjectDir, i.FullPath).Replace('\\', '/')).ToList();
        var bundles = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var bundleFile in entries.Where(e => e.Role is Role.Code or Role.LegacyCode && e.Text!.Contains("BundleCollection", StringComparison.Ordinal)))
            foreach (var (key, value) in BundleConfigParser.Parse(bundleFile.Text!, projectFilesUnix))
                bundles[key] = value;

        var config = ConfigMigrator.Migrate(project, ctx.PreserveSqlEncryption);
        var secrets = ctx.KeepSecrets ? new SecretsPlan() : SecretsExtractor.Extract(config, ctx.SolutionName, project.Name);
        if (secrets.Any)
        {
            config.Items.RemoveAll(i => i.RuleId == "CFG-SECRETS");
            WriteSecretsArtifacts(project, secrets, plan, result.RelativeDir, items);
            foreach (var m in result.Modernizations.Where(m => m.RuleId == "MOD-SEC-SECRETS"))
                m.Why = m.Why.Replace("foram copiadas para o appsettings.json e acabariam na imagem Docker/repositório.",
                    $"foram retiradas do appsettings.json e ficaram em {SecretsExtractor.RootFolder}/{project.Name}/ (fora do repositório e da imagem).");
        }
        items.AddRange(config.Items);

        var facts = new CodeFacts();
        var changes = new Dictionary<(string Id, string Description), (int Count, HashSet<string> Files)>();
        void Track(IEnumerable<CodeChange> list, string file)
        {
            foreach (var c in list)
            {
                var key = (c.RuleId, c.Description);
                var current = changes.GetValueOrDefault(key, (0, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                current.Item2.Add(file);
                changes[key] = (current.Item1 + c.Count, current.Item2);
            }
        }

        foreach (var entry in codeEntries)
        {
            var area = AreaController().Match(entry.Unix) is { Success: true } m ? m.Groups[1].Value : null;
            var (rewritten, controllerChanges, rewriter) = ControllerRewriter.Rewrite(entry.Text!, catalog, startup.ApiRouteTemplate, area);
            var (transformed, regexChanges, fileFacts) = CodeTransformer.Transform(rewritten, new CodeTransformOptions(config.Log4NetExtracted, isWeb));
            facts.Merge(fileFacts);
            Track(controllerChanges.Concat(regexChanges), entry.Unix);

            foreach (var warning in rewriter.RouteWarnings)
                items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Code, "CS-WEBAPI-AMBIGUOUS",
                    $"Rotas possivelmente ambíguas em {warning.Controller}",
                    $"{warning.Verb} \"{warning.Template}\": {string.Join(", ", warning.Actions)}.",
                    "No Web API 2 a seleção da action também considerava os nomes dos parâmetros da query string; no ASP.NET Core essas actions colidem (AmbiguousMatchException). Dê templates distintos, ex.: [HttpGet(\"por-nome/{nome}\")].",
                    entry.Unix));

            ApplyRules(CodeRules.CSharp, transformed, entry.Unix, InventoryCategory.Code, project, items);
            if (entry.Destination != null) plan.Write(entry.Destination, transformed);
        }

        foreach (var entry in entries.Where(e => e.Role == Role.View))
        {
            (var original, entry.WasAnsi) = TextFiles.Read(entry.Item.FullPath);
            var (transformed, viewChanges) = RazorTransformer.Transform(original, bundles);
            Track(viewChanges, entry.Unix);
            ApplyRules(CodeRules.Razor, transformed, entry.Unix, InventoryCategory.View, project, items);
            plan.Write(entry.Destination!, transformed);
        }

        foreach (var entry in entries.Where(e => e.Role is Role.Static or Role.Copy or Role.LegacyMarkup or Role.LegacyCode && e.Destination != null))
            plan.Copy(entry.Item.FullPath, entry.Destination!);

        foreach (var ((id, description), (count, files)) in changes)
            items.Add(new InventoryItem
            {
                Project = project.Name, Severity = InventorySeverity.Info,
                Category = id.StartsWith("VW", StringComparison.Ordinal) ? InventoryCategory.View : InventoryCategory.Code,
                RuleId = id, Title = description, Description = $"{count} ocorrência(s) em {files.Count} arquivo(s).",
                Suggestion = "Nenhuma ação necessária.", Occurrences = count, AutoMigrated = true,
                FilePath = files.Count == 1 ? files.First() : null
            });

        var ansiFiles = entries.Count(e => e.WasAnsi && e.Role is Role.Code or Role.View);
        if (ansiFiles > 0)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Code, "CS-ENCODING",
                $"{ansiFiles} arquivo(s) convertidos de ANSI (Windows-1252) para UTF-8",
                "Os arquivos não tinham BOM e continham bytes inválidos em UTF-8 (acentuação em codificação legada); foram lidos como Windows-1252 e gravados em UTF-8 com BOM.",
                "Nenhuma ação necessária; confira a acentuação em um ou dois arquivos.", auto: true));

        GenerateViewImports(project, entries, plan, items, result.RelativeDir);
        ReportLegacy(project, entries, items);

        var keepAppConfig = !isWeb && project.ConfigFilePath != null &&
                            Path.GetFileName(project.ConfigFilePath).Equals("app.config", StringComparison.OrdinalIgnoreCase) &&
                            (config.NeedsSystemConfiguration || facts.UsesLegacyConfigurationApi);
        if (keepAppConfig)
        {
            plan.Copy(project.ConfigFilePath!, Path.Combine(result.RelativeDir, Path.GetFileName(project.ConfigFilePath!)));
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Configuration, "CFG-KEEP-APPCONFIG",
                "App.config mantido por compatibilidade",
                "O código ainda usa APIs de System.Configuration (Settings.settings, seções customizadas ou ConfigurationManager não reescrito). O .NET 10 lê o App.config como <assembly>.dll.config via System.Configuration.ConfigurationManager.",
                "Depois de migrar esses usos para IConfiguration/IOptions<T>, remova o App.config e o pacote System.Configuration.ConfigurationManager.",
                Path.GetFileName(project.ConfigFilePath!)));
        }

        WriteConfigFiles(project, config, plan, result.RelativeDir, items);

        var requirements = BuildRequirements(project, facts, config, catalog, keepAppConfig, codeEntries.Select(e => e.Text!).ToList(), items);
        var packages = await ctx.Planner.PlanAsync(project, requirements);
        items.AddRange(packages.Items);

        var spec = BuildProjectSpec(project, ctx, entries, facts, config, packages, plan, items, result.RelativeDir);
        if (secrets.Any && project.Kind != ProjectKind.ClassLibrary && spec.Properties.All(p => p.Name != "UserSecretsId"))
            spec.Properties.Add(("UserSecretsId", SecretsExtractor.UserSecretsId(ctx.SolutionName, project.Name)));
        items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-SDK",
            $"{csprojName} convertido para SDK-style ({spec.Sdk}, {spec.Properties.First(p => p.Name == "TargetFramework").Value})",
            project.IsSdkStyle ? "TargetFramework atualizado." : "packages.config virou PackageReference; itens explícitos foram substituídos pelos globs do SDK.",
            "Nenhuma ação necessária.", auto: true));

        if (isWeb)
        {
            var programInput = new WebProgramInput(project, startup, config.Hints,
                HasApiControllers: catalog.Api.Count > 0,
                HasMvcControllers: catalog.Mvc.Count > 0,
                UsesHttpContextAccessor: facts.UsesHttpContextCurrent,
                KeepNewtonsoft: catalog.Api.Count > 0,
                HasSwagger: packages.References.Any(r => r.Id.Equals("Swashbuckle.AspNetCore", StringComparison.OrdinalIgnoreCase)),
                UsesOutputCache: facts.UsesOutputCache,
                Log4NetConfigFile: config.Log4NetExtracted,
                CloudReady: ctx.Cloud != CloudTarget.None);
            var hasProgram = entries.Any(e => e.Unix.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) && e.Role == Role.Code);
            var programPath = Path.Combine(result.RelativeDir, hasProgram ? "Program.Migrator.cs.txt" : "Program.cs");
            plan.Write(programPath, ProgramGenerator.GenerateWeb(programInput));
            items.Add(Item(project, hasProgram ? InventorySeverity.Warning : InventorySeverity.Info, InventoryCategory.Startup, "STARTUP-PROGRAM",
                hasProgram ? "Program.cs já existia: versão gerada salva em Program.Migrator.cs.txt" : "Program.cs gerado (pipeline ASP.NET Core)",
                $"Rotas: {Math.Max(1, startup.Routes.Count)}; autenticação: {(config.Hints.FormsAuth != null ? "cookie (Forms)" : config.Hints.WindowsAuth ? "Windows" : "nenhuma")}; registros de DI convertidos: {startup.DiRegistrations.Count}.",
                "Revise os comentários 'TODO Migrator' e registre no DI os serviços que antes eram criados manualmente.", auto: !hasProgram));

            if (!entries.Any(e => e.Unix.Equals("Properties/launchSettings.json", StringComparison.OrdinalIgnoreCase)))
                plan.Write(Path.Combine(result.RelativeDir, "Properties", "launchSettings.json"), ProgramGenerator.GenerateLaunchSettings(project));
        }

        return new MigratedProject(result, spec, plan, profile);
    }

    private static void WriteSecretsArtifacts(ProjectInfo project, SecretsPlan secrets, OutputPlan plan, string projectOut, List<InventoryItem> items)
    {
        var folder = Path.Combine(SecretsExtractor.RootFolder, project.Name);
        plan.Write(Path.Combine(folder, "README.md"), SecretsExtractor.Readme(secrets, project.Name));
        plan.Write(Path.Combine(folder, "secrets.template.json"), SecretsExtractor.TemplateJson(secrets));
        foreach (var environment in SecretsExtractor.Environments(secrets))
            plan.Write(Path.Combine(folder, environment == null ? "appsettings.Secrets.json" : $"appsettings.{environment}.Secrets.json"), SecretsExtractor.AppSettingsShapedJson(secrets, environment));
        plan.Write(Path.Combine(folder, "create-secrets.sh"), SecretsExtractor.CreateScriptBash(secrets, project.Name));
        plan.Write(Path.Combine(folder, "create-secrets.ps1"), SecretsExtractor.CreateScriptPowerShell(secrets, project.Name));
        plan.Write(Path.Combine(folder, "ecs-task-secrets.json"), SecretsExtractor.EcsTaskSecretsJson(secrets));
        plan.Write(Path.Combine(folder, "set-user-secrets.sh"), SecretsExtractor.UserSecretsScript(secrets, Path.Combine(projectOut, Path.GetFileName(project.ProjectPath))));
        items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Configuration, "CFG-SECRETS-EXTRACTED",
            $"{secrets.Secrets.Count} segredo(s) retirados do appsettings*.json",
            $"Marcador '<secret: nome>' no lugar de {string.Join(", ", secrets.Secrets.Select(s => s.ConfigPath).Distinct().Take(6))}. Valores, scripts para o Secrets Manager, bloco da task definition e user-secrets em {folder}/ (ignorado pelo git e pelo Docker).",
            "Rode create-secrets.sh para criar no AWS Secrets Manager e set-user-secrets.sh para desenvolvimento local; depois rotacione as credenciais que estavam em texto claro.", "appsettings.json", auto: true));
    }

    private static XElement? LoadConfig(ProjectInfo project)
    {
        if (project.ConfigFilePath == null || !File.Exists(project.ConfigFilePath)) return null;
        try { return XDocument.Load(project.ConfigFilePath).Root; }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException) { return null; }
    }

    private static List<Entry> Classify(ProjectInfo project, ProjectMigrationContext ctx, List<InventoryItem> items)
    {
        var entries = new List<Entry>();
        var missing = new List<string>();
        var ignoredWebConfigs = new List<string>();
        var root = ctx.RootDir;
        var isWeb = project.Kind == ProjectKind.Web;
        var skip = new HashSet<string>(project.ConfigTransformFiles, StringComparer.OrdinalIgnoreCase);
        if (project.ConfigFilePath != null) skip.Add(project.ConfigFilePath);

        foreach (var item in project.Items.DistinctBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            var full = item.FullPath;
            if (skip.Contains(full)) continue;
            var name = Path.GetFileName(full);
            var ext = Path.GetExtension(full).ToLowerInvariant();
            if (NeverCopied.Contains(name) || ext is ".user" or ".vspscc" or ".vssscc" or ".csproj") continue;
            if (!File.Exists(full))
            {
                missing.Add(Path.GetRelativePath(project.ProjectDir, full));
                continue;
            }

            var relative = Path.GetRelativePath(project.ProjectDir, full);
            if (!item.IsInside(project.ProjectDir))
            {
                var insideRoot = full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                var linkedRole = ext == ".cs" && item.ItemType == "Compile" ? Role.Code : Role.Copy;
                entries.Add(new Entry
                {
                    Item = item, Relative = relative, Role = linkedRole, Linked = true,
                    Destination = insideRoot ? Path.GetRelativePath(root, full) : null
                });
                continue;
            }

            var unix = relative.Replace('\\', '/');
            Role role;
            string destination = relative;
            if (name.Equals("web.config", StringComparison.OrdinalIgnoreCase))
            {
                if (Regex.IsMatch(unix, @"(^|/)Views/", RegexOptions.IgnoreCase)) role = Role.ViewsConfig;
                else { ignoredWebConfigs.Add(unix); continue; }
            }
            else if (name.Equals("Global.asax", StringComparison.OrdinalIgnoreCase) && !unix.Contains('/'))
                continue;
            else if (ext == ".cs")
            {
                var (text, ansi) = TextFiles.Read(full);
                var legacy = IsLegacyCode(unix, name, text);
                role = legacy ? Role.LegacyCode : Role.Code;
                if (legacy) destination = Path.Combine("_Legacy", relative);
                entries.Add(new Entry { Item = item, Relative = relative, Role = role, Text = text, WasAnsi = ansi, Destination = Path.Combine(Relative(root, project.ProjectDir), destination) });
                continue;
            }
            else if (ext == ".cshtml") role = Role.View;
            else if (ext == ".vbhtml" || WebFormsMarkup.Contains(ext))
            {
                role = Role.LegacyMarkup;
                destination = Path.Combine("_Legacy", relative);
            }
            else if (isWeb && IsStatic(unix, ext))
            {
                role = Role.Static;
                destination = Path.Combine("wwwroot", relative);
            }
            else role = Role.Copy;

            entries.Add(new Entry { Item = item, Relative = relative, Role = role, Destination = Path.Combine(Relative(root, project.ProjectDir), destination) });
        }

        if (missing.Count > 0)
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.ProjectFile, "PRJ-MISSING",
                $"{missing.Count} arquivo(s) listados no .csproj não existem no disco",
                string.Join(", ", missing.Take(15)) + (missing.Count > 15 ? ", ..." : ""),
                "Confirme se não fazem falta; projetos SDK-style só incluem arquivos existentes."));
        if (ignoredWebConfigs.Count > 0)
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Configuration, "CFG-SUBFOLDER",
                "web.config de subpastas não migrados",
                string.Join(", ", ignoredWebConfigs),
                "Regras por pasta (ex.: <authorization> em Uploads/web.config) devem virar [Authorize]/policies ou app.UseWhen(...) no Program.cs."));

        if (!project.IsSdkStyle)
            ReportOrphans(project, items);

        return entries;
    }

    private static bool IsLegacyCode(string unix, string name, string text)
    {
        if (WebFormsCodeSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        if (name.Equals("Global.asax.cs", StringComparison.OrdinalIgnoreCase)) return true;
        if (unix.StartsWith("App_Start/", StringComparison.OrdinalIgnoreCase) && LegacyAppStart().IsMatch(text)) return true;
        if (text.Contains("IAppBuilder", StringComparison.Ordinal) || text.Contains("OwinStartup", StringComparison.Ordinal)) return true;
        if (AreaRegistrationClass().IsMatch(text)) return true;
        return text.Contains("[RunInstaller(true)]", StringComparison.Ordinal);
    }

    private static void MarkInstallerDesigners(List<Entry> entries)
    {
        var legacyBases = entries.Where(e => e.Role == Role.LegacyCode)
            .Select(e => Path.Combine(Path.GetDirectoryName(e.Relative) ?? "", Path.GetFileNameWithoutExtension(e.Relative)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.Where(e => e.Role == Role.Code && e.Relative.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)))
        {
            var baseName = entry.Relative[..^".Designer.cs".Length];
            if (!legacyBases.Contains(baseName)) continue;
            entry.Role = Role.LegacyCode;
            var projectOut = entry.Destination![..^entry.Relative.Length];
            entry.Destination = Path.Combine(projectOut, "_Legacy", entry.Relative);
        }
    }

    private static bool IsStatic(string unix, string ext)
    {
        var segments = unix.Split('/');
        if (segments.Length == 1) return RootStaticFiles.Contains(segments[0]) || ext is ".html" or ".htm";
        return StaticFolders.Contains(segments[0]) && StaticExtensions.Contains(ext);
    }

    private static void ReportOrphans(ProjectInfo project, List<InventoryItem> items)
    {
        var known = project.Items.Select(i => i.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        known.Add(project.ProjectPath);
        if (project.ConfigFilePath != null) known.Add(project.ConfigFilePath);
        foreach (var t in project.ConfigTransformFiles) known.Add(t);

        var orphans = ProjectLoader.EnumerateProjectDirectory(project.ProjectDir)
            .Where(f => !known.Contains(f))
            .Select(f => Path.GetRelativePath(project.ProjectDir, f))
            .Where(r => !r.StartsWith('.') && Path.GetFileName(r) is not ("packages.config" or "Global.asax") &&
                        Path.GetExtension(r).ToLowerInvariant() is not (".user" or ".suo" or ".vspscc" or ".vssscc" or ".csproj"))
            .ToList();
        if (orphans.Count == 0) return;
        items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-ORPHANS",
            $"{orphans.Count} arquivo(s) no disco não pertencem ao .csproj e não foram copiados",
            string.Join(", ", orphans.Take(15)) + (orphans.Count > 15 ? ", ..." : ""),
            "Projetos SDK-style incluem todos os arquivos da pasta; por isso só foram copiados os itens do projeto original. Copie manualmente o que ainda for necessário."));
    }

    private static void ApplyRules(IReadOnlyList<CodeRule> rules, string text, string file, InventoryCategory category, ProjectInfo project, List<InventoryItem> items)
    {
        LineIndex? index = null;
        foreach (var rule in rules)
        {
            if (rule.FileFilter != null && !rule.FileFilter.IsMatch(text)) continue;
            if (rule.FileExclusion != null && rule.FileExclusion.IsMatch(text)) continue;

            var count = 0;
            int? first = null;
            foreach (Match m in rule.Regex.Matches(text))
            {
                index ??= new LineIndex(text);
                var (line, content) = index.Locate(m.Index);
                if (IsComment(content)) continue;
                count++;
                first ??= line;
            }
            if (count == 0) continue;
            items.Add(new InventoryItem
            {
                Project = project.Name, Severity = rule.Severity, Category = category, RuleId = rule.Id,
                Title = rule.Title, Description = rule.Description, Suggestion = rule.Suggestion,
                FilePath = file, Line = first, Occurrences = count, AutoMigrated = false
            });
        }
    }

    private static bool IsComment(string line)
    {
        var t = line.TrimStart();
        return t.StartsWith("//", StringComparison.Ordinal) || t.StartsWith("/*", StringComparison.Ordinal) ||
               t.StartsWith('*') || t.StartsWith("@*", StringComparison.Ordinal);
    }

    private static void GenerateViewImports(ProjectInfo project, List<Entry> entries, OutputPlan plan, List<InventoryItem> items, string projectOut)
    {
        var existing = entries.Where(e => e.Item.FullPath.EndsWith("_ViewImports.cshtml", StringComparison.OrdinalIgnoreCase))
            .Select(e => Path.GetDirectoryName(e.Unix.Replace('/', Path.DirectorySeparatorChar)) ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var generated = 0;
        foreach (var config in entries.Where(e => e.Role == Role.ViewsConfig))
        {
            var dir = Path.GetDirectoryName(config.Relative) ?? "";
            if (existing.Contains(dir)) continue;
            IEnumerable<string> namespaces;
            try
            {
                namespaces = XDocument.Load(config.Item.FullPath).Descendants()
                    .Where(e => e.Name.LocalName == "add" && e.Parent?.Name.LocalName == "namespaces")
                    .Select(e => e.Attribute("namespace")?.Value).OfType<string>().ToList();
            }
            catch (Exception) { namespaces = []; }
            plan.Write(Path.Combine(projectOut, dir, "_ViewImports.cshtml"), RazorTransformer.BuildViewImports(namespaces));
            generated++;
        }

        if (generated == 0 && project.Kind == ProjectKind.Web && entries.Any(e => e.Role == Role.View) &&
            !existing.Contains("Views") && entries.All(e => e.Role != Role.ViewsConfig))
        {
            plan.Write(Path.Combine(projectOut, "Views", "_ViewImports.cshtml"), RazorTransformer.BuildViewImports([]));
            generated++;
        }

        if (generated > 0)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.View, "VW-VIEWIMPORTS",
                $"{generated} _ViewImports.cshtml gerado(s) a partir de Views/web.config",
                "Os namespaces de <system.web.webPages.razor><pages><namespaces> viraram @using e os Tag Helpers foram habilitados.",
                "Nenhuma ação necessária.", auto: true));
    }

    private static void ReportLegacy(ProjectInfo project, List<Entry> entries, List<InventoryItem> items)
    {
        var legacy = entries.Where(e => e.Role is Role.LegacyCode or Role.LegacyMarkup).ToList();
        if (legacy.Count == 0) return;

        var webForms = legacy.Where(e => WebFormsMarkup.Contains(Path.GetExtension(e.Relative)) && !e.Relative.EndsWith(".asax", StringComparison.OrdinalIgnoreCase)).ToList();
        var groups = webForms.GroupBy(e => Path.GetExtension(e.Relative).ToLowerInvariant()).ToList();
        foreach (var group in groups)
        {
            var (title, hint) = group.Key switch
            {
                ".aspx" or ".ascx" or ".master" => ("Páginas/controles WebForms", "WebForms não existe no .NET 10. Reescreva como Razor Pages ou MVC (ou Blazor para telas ricas). Os code-behinds estão em _Legacy para referência."),
                ".ashx" => ("Generic handlers (.ashx)", "Reescreva cada handler como endpoint: app.MapGet(\"/caminho\", async context => { ... }) ou uma action de controller."),
                ".asmx" => ("Web services ASMX", "Reescreva como controllers Web API ou hospede os contratos com CoreWCF (BasicHttpBinding é compatível com clientes SOAP existentes)."),
                ".svc" => ("Serviços WCF (.svc)", "Hospede com CoreWCF (pacotes CoreWCF.Http/CoreWCF.Primitives) mantendo os contratos, ou migre para gRPC/Web API."),
                _ => ($"Arquivos {group.Key}", "Sem equivalente no ASP.NET Core.")
            };
            items.Add(Item(project, InventorySeverity.Breaking, InventoryCategory.Code, "LEGACY-WEBFORMS",
                $"{group.Count()} {title} movidos para _Legacy",
                string.Join(", ", group.Take(20).Select(e => e.Unix)) + (group.Count() > 20 ? ", ..." : ""), hint));
        }

        var startupFiles = legacy.Where(e => e.Role == Role.LegacyCode && !WebFormsCodeSuffixes.Any(s => e.Relative.EndsWith(s, StringComparison.OrdinalIgnoreCase)) &&
                                             !e.Text!.Contains("RunInstaller", StringComparison.Ordinal) &&
                                             !e.Relative.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)).ToList();
        if (startupFiles.Count > 0)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.Startup, "LEGACY-STARTUP",
                $"{startupFiles.Count} arquivo(s) de inicialização movidos para _Legacy (fora do build)",
                string.Join(", ", startupFiles.Select(e => e.Unix)),
                "Rotas, filtros, CORS, áreas e registros de DI reconhecidos foram portados para o Program.cs; os demais aparecem como itens 'Inicialização' neste relatório. Apague a pasta _Legacy quando terminar a revisão.", auto: true));

        var installers = legacy.Where(e => e.Text?.Contains("RunInstaller", StringComparison.Ordinal) == true).ToList();
        if (installers.Count > 0)
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Code, "LEGACY-INSTALLER",
                "Instalador de Windows Service (ProjectInstaller) movido para _Legacy",
                "System.Configuration.Install (installutil) não existe no .NET 10.",
                "Instale o serviço com: sc.exe create NomeDoServico binPath= \"C:\\caminho\\app.exe\" start= auto (ou New-Service no PowerShell). O nome/conta/descrição estavam no ProjectInstaller.Designer.cs.",
                installers[0].Unix));
    }

    private static void WriteConfigFiles(ProjectInfo project, ConfigMigrationResult config, OutputPlan plan, string projectOut, List<InventoryItem> items)
    {
        if (config.AppSettingsJson != null)
        {
            plan.Write(Path.Combine(projectOut, "appsettings.json"), config.AppSettingsJson);
            foreach (var (environment, json) in config.EnvironmentJson)
                plan.Write(Path.Combine(projectOut, $"appsettings.{environment}.json"), json);

            if (project.Kind == ProjectKind.ClassLibrary)
                items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Configuration, "CFG-LIBRARY",
                    "Configuração de biblioteca: consolidar no projeto host",
                    "Bibliotecas não têm arquivo de configuração próprio em tempo de execução; o appsettings.json gerado aqui serve de referência e não é copiado para a saída.",
                    "Copie as seções para o appsettings.json da aplicação web/console que consome esta biblioteca.", "appsettings.json"));
        }
        else if (config.EnvironmentJson.Count > 0)
        {
            foreach (var (environment, json) in config.EnvironmentJson)
                plan.Write(Path.Combine(projectOut, $"appsettings.{environment}.json"), json);
        }

        foreach (var (file, content) in config.ExtraFiles)
            plan.Write(Path.Combine(projectOut, file), content);
    }

    private static List<PackageRequirement> BuildRequirements(ProjectInfo project, CodeFacts facts, ConfigMigrationResult config, ControllerCatalog catalog, bool keepAppConfig,
        IReadOnlyList<string> codeTexts, List<InventoryItem> items)
    {
        var list = new List<PackageRequirement>();
        void Need(string id, VersionPolicy policy, string reason) => list.Add(new PackageRequirement(id, policy, reason));
        var isWeb = project.Kind == ProjectKind.Web;

        if (isWeb && catalog.Api.Count > 0)
            Need("Microsoft.AspNetCore.Mvc.NewtonsoftJson", VersionPolicy.DotNet, "Mantém a serialização JSON do Web API 2 (Newtonsoft, nomes de propriedades sem camelCase) para não quebrar clientes existentes.");
        if (project.Kind == ProjectKind.WindowsService)
            Need("System.ServiceProcess.ServiceController", VersionPolicy.DotNet, "ServiceBase continua funcionando no .NET 10 (Windows).");
        if (facts.UsesLegacyConfigurationApi || keepAppConfig)
            Need("System.Configuration.ConfigurationManager", VersionPolicy.DotNet, "Compatibilidade para APIs de System.Configuration que ainda são usadas no código.");
        if (facts.RewroteConfiguration && !isWeb)
            Need(project.Kind is ProjectKind.ClassLibrary ? "Microsoft.Extensions.Configuration.Abstractions" : "Microsoft.Extensions.Configuration.Json",
                VersionPolicy.DotNet, "Fornece IConfiguration para as leituras de configuração reescritas.");
        if (facts.UsesSqlClient)
            Need("Microsoft.Data.SqlClient", VersionPolicy.Latest("7.1.1"), "O código usava System.Data.SqlClient e foi migrado para Microsoft.Data.SqlClient.");
        if (facts.UsesWcfClient)
        {
            Need("System.ServiceModel.Http", VersionPolicy.Latest("8.1.2"), "Cliente WCF para .NET.");
            Need("System.ServiceModel.Primitives", VersionPolicy.Latest("8.1.2"), "Cliente WCF para .NET.");
            if (facts.UsesNetTcp) Need("System.ServiceModel.NetTcp", VersionPolicy.Latest("8.1.2"), "Cliente WCF com NetTcpBinding.");
        }
        if (facts.UsesSystemDrawing && project.Kind != ProjectKind.Desktop)
            Need("System.Drawing.Common", VersionPolicy.DotNet, "System.Drawing (suportado apenas no Windows).");
        if (facts.UsesEventLog) Need("System.Diagnostics.EventLog", VersionPolicy.DotNet, "EventLog (Windows).");
        if (facts.UsesPerformanceCounter) Need("System.Diagnostics.PerformanceCounter", VersionPolicy.DotNet, "PerformanceCounter (Windows).");
        if (facts.UsesMemoryCache) Need("System.Runtime.Caching", VersionPolicy.DotNet, "MemoryCache/ObjectCache de System.Runtime.Caching.");
        if (config.Hints.WindowsAuth) Need("Microsoft.AspNetCore.Authentication.Negotiate", VersionPolicy.DotNet, "Windows Authentication (Negotiate/Kerberos/NTLM).");

        var unused = new List<string>();
        foreach (var reference in project.FrameworkReferences.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var rule = FrameworkReferenceRules.Find(reference, project.Kind);
            if (rule.Action == FrameworkRefAction.Ignore) continue;
            var mentioned = codeTexts.Any(t => t.Contains(reference + ".", StringComparison.Ordinal) || t.Contains("using " + reference + ";", StringComparison.Ordinal));
            if (!mentioned)
            {
                unused.Add(reference);
                continue;
            }
            switch (rule.Action)
            {
                case FrameworkRefAction.Package:
                    Need(rule.PackageId!, rule.PackageId!.StartsWith("System.", StringComparison.Ordinal) ? VersionPolicy.DotNet : VersionPolicy.Latest("1.0.0"),
                        $"Substitui a referência de framework {reference}.");
                    break;
                case FrameworkRefAction.Report:
                    items.Add(Item(project, rule.Severity, InventoryCategory.ProjectFile, "PRJ-FRAMEWORKREF", $"Referência de framework: {reference}",
                        "Assembly do .NET Framework/GAC usado pelo código e sem pacote NuGet equivalente.", rule.Guidance));
                    break;
            }
        }
        if (unused.Count > 0)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-FRAMEWORKREF-UNUSED",
                $"{unused.Count} referência(s) de framework sem uso no código foram removidas",
                string.Join(", ", unused),
                "Nenhuma ação necessária (eram referências padrão do template ou sobras).", auto: true));

        if (project.Kind == ProjectKind.Test)
        {
            bool HasPackage(string prefix) => project.Packages.Any(p => p.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            Need("Microsoft.NET.Test.Sdk", VersionPolicy.Latest("18.10.1"), "Necessário para executar testes com 'dotnet test'.");
            if (project.TestFramework == "MSTest" && !HasPackage("MSTest.TestFramework"))
            {
                Need("MSTest.TestFramework", VersionPolicy.Latest("3.11.0", "4.0.0"), "Substitui a referência Microsoft.VisualStudio.QualityTools.UnitTestFramework.");
                Need("MSTest.TestAdapter", VersionPolicy.Latest("3.11.0", "4.0.0"), "Adapter do MSTest para 'dotnet test'.");
            }
            if (project.TestFramework == "NUnit" && !HasPackage("NUnit3TestAdapter"))
                Need("NUnit3TestAdapter", VersionPolicy.Latest("5.0.0"), "Adapter do NUnit para 'dotnet test'.");
            if (project.TestFramework == "xUnit" && !HasPackage("xunit.runner.visualstudio"))
                Need("xunit.runner.visualstudio", VersionPolicy.Latest("3.1.5", "4.0.0"), "Runner do xUnit para 'dotnet test'.");
        }

        return list;
    }

    private static ProjectFileSpec BuildProjectSpec(ProjectInfo project, ProjectMigrationContext ctx, List<Entry> entries, CodeFacts facts,
        ConfigMigrationResult config, PackagePlan packages, OutputPlan plan, List<InventoryItem> items, string projectOut)
    {
        var spec = new ProjectFileSpec();
        var isWeb = project.Kind == ProjectKind.Web;
        var hasViews = entries.Any(e => e.Role == Role.View);
        var windows = project.Kind is ProjectKind.Desktop or ProjectKind.WindowsService;

        spec.Sdk = isWeb ? "Microsoft.NET.Sdk.Web" : hasViews ? "Microsoft.NET.Sdk.Razor" : "Microsoft.NET.Sdk";
        var props = spec.Properties;
        props.Add(("TargetFramework", windows ? "net10.0-windows" : "net10.0"));
        if (!isWeb && project.IsExecutable) props.Add(("OutputType", project.OutputType));
        props.Add(("RootNamespace", project.RootNamespace));
        props.Add(("AssemblyName", project.AssemblyName));
        props.Add(("Nullable", "disable"));
        props.Add(("ImplicitUsings", "disable"));

        var codeTexts = entries.Where(e => e.Role == Role.Code).Select(e => e.Text!).ToList();
        if (codeTexts.Any(t => AssemblyAttribute().IsMatch(t)))
        {
            props.Add(("GenerateAssemblyInfo", "false"));
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-ASSEMBLYINFO",
                "AssemblyInfo.cs preservado (GenerateAssemblyInfo=false)",
                "Os atributos de assembly existentes continuam valendo e não conflitam com os gerados pelo SDK.",
                "Opcional: mova versão/empresa/produto para propriedades do .csproj (Version, Company, Product) e apague o AssemblyInfo.cs.", auto: true));
        }
        if (codeTexts.Any(t => WildcardVersion().IsMatch(t))) props.Add(("Deterministic", "false"));

        foreach (var (name, value) in project.CarriedProperties) props.Add((name, value));
        if (project.DefineConstants.Count > 0) props.Add(("DefineConstants", "$(DefineConstants);" + string.Join(";", project.DefineConstants)));
        if (project.UsesWinForms) props.Add(("UseWindowsForms", "true"));
        if (project.UsesWpf) props.Add(("UseWPF", "true"));
        if (hasViews && !isWeb) props.Add(("AddRazorSupportForMvc", "true"));
        if (project.Kind == ProjectKind.Test)
        {
            props.Add(("IsPackable", "false"));
            props.Add(("IsTestProject", "true"));
        }

        if (!isWeb && (facts.UsesAspNetCore || hasViews))
        {
            spec.FrameworkReferences.Add("Microsoft.AspNetCore.App");
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-ASPNETCORE-REF",
                "FrameworkReference Microsoft.AspNetCore.App adicionada",
                "A biblioteca usa tipos do ASP.NET Core (antes System.Web.Mvc/Http).", "Nenhuma ação necessária.", auto: true));
        }

        if (entries.Any(e => e.Role is Role.LegacyCode or Role.LegacyMarkup))
        {
            spec.Items.Add(new XElement("Compile", new XAttribute("Remove", @"_Legacy\**")));
            spec.Items.Add(new XElement("Content", new XAttribute("Remove", @"_Legacy\**")));
        }

        foreach (var entry in entries.Where(e => e.Linked))
        {
            var include = (entry.Destination != null ? entry.Relative : entry.Item.FullPath).Replace('/', '\\');
            var link = (entry.Item.Link ?? Path.GetFileName(entry.Item.FullPath)).Replace('/', '\\');
            var type = entry.Role == Role.Code ? "Compile" : "None";
            spec.Items.Add(new XElement(type, new XAttribute("Include", include), new XAttribute("Link", link)));
            if (entry.Destination == null)
                items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.ProjectFile, "PRJ-LINK-OUTSIDE",
                    $"Arquivo linkado fora da raiz da solução: {Path.GetFileName(entry.Item.FullPath)}",
                    $"{entry.Item.FullPath} é referenciado por caminho absoluto e não foi transformado.",
                    "Copie o arquivo para dentro da solução ou migre também o projeto que o contém.", entry.Relative));
        }

        foreach (var entry in entries.Where(e => !e.Linked && e.Role is Role.Code or Role.View or Role.Copy))
            AddItemMetadata(spec, entry, isWeb);

        if (project.Kind is ProjectKind.Console or ProjectKind.WindowsService or ProjectKind.Desktop or ProjectKind.Test && config.AppSettingsJson != null)
            spec.Items.Add(new XElement("None", new XAttribute("Update", "appsettings*.json"), new XAttribute("CopyToOutputDirectory", "PreserveNewest")));
        foreach (var extra in config.ExtraFiles.Keys.Where(k => k is "log4net.config" or "nlog.config"))
            spec.Items.Add(new XElement(isWeb ? "Content" : "None", new XAttribute("Update", extra), new XAttribute("CopyToOutputDirectory", "PreserveNewest")));

        spec.Packages.AddRange(packages.References);

        foreach (var reference in project.ProjectReferences.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ctx.ProjectMap.ContainsKey(reference))
            {
                spec.ProjectReferences.Add(Path.GetRelativePath(project.ProjectDir, reference));
                continue;
            }
            spec.ProjectReferences.Add(reference);
            items.Add(Item(project, InventorySeverity.Breaking, InventoryCategory.ProjectFile, "PRJ-REF-NOTMIGRATED",
                $"Projeto referenciado não migrado: {Path.GetFileName(reference)}",
                $"{reference} não faz parte da migração (fora da solução/diretório ou tipo não suportado) e continua em .NET Framework: o build falhará (NU1201).",
                "Migre também esse projeto (informe a .sln que contém os dois) ou multi-target a biblioteca para netstandard2.0."));
        }

        foreach (var binary in project.BinaryReferences)
            AddBinaryReference(project, ctx, binary, spec, plan, items, projectOut);

        foreach (var com in project.ComReferences)
        {
            spec.RawElements.Add(com);
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.ProjectFile, "PRJ-COMREF",
                $"COMReference: {XElement.Parse(com).Attribute("Include")?.Value}",
                "<COMReference> foi mantido, mas não é suportado pelo 'dotnet build' (MSB4803).",
                "Compile com o MSBuild do Visual Studio (msbuild.exe) ou gere o interop com tlbimp.exe e referencie a DLL gerada."));
        }

        foreach (var import in project.CustomImports)
        {
            spec.RawElements.Add(import);
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.ProjectFile, "PRJ-IMPORT",
                $"Import customizado mantido: {XElement.Parse(import).Attribute("Project")?.Value}",
                "Targets/props customizados podem depender de propriedades do formato antigo.", "Verifique se o arquivo importado funciona com projetos SDK-style."));
        }
        foreach (var target in project.CustomTargets)
        {
            spec.RawElements.Add(target);
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.ProjectFile, "PRJ-TARGET",
                $"Target customizado mantido: {XElement.Parse(target).Attribute("Name")?.Value}",
                "Targets BeforeBuild/AfterBuild e similares continuam funcionando, mas caminhos de saída mudaram (bin\\Debug\\net10.0\\).", "Revise os caminhos usados no target."));
        }
        if (project.DroppedTargets.Count > 0)
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-TARGET-DROPPED",
                "Targets do formato antigo removidos", string.Join(", ", project.DroppedTargets),
                "MvcBuildViews/AspNetCompiler/TransformXml não se aplicam: o Razor SDK compila as views no build e as transformações viraram appsettings.{Ambiente}.json.", auto: true));

        var usesSolutionDir = new[] { project.PreBuildEvent, project.PostBuildEvent }
            .Concat(project.CustomTargets).Concat(project.CustomImports)
            .Concat(project.BinaryReferences.Select(b => b.HintPath))
            .Any(s => s?.Contains("$(SolutionDir)", StringComparison.OrdinalIgnoreCase) == true);
        if (usesSolutionDir)
        {
            var toRoot = Path.GetRelativePath(project.ProjectDir, ctx.RootDir);
            var fallback = toRoot == "." ? @"$(MSBuildProjectDirectory)\" : $@"$(MSBuildProjectDirectory)\{toRoot}\";
            spec.RawElements.Insert(0,
                $"<PropertyGroup><SolutionDir Condition=\"'$(SolutionDir)' == '' Or '$(SolutionDir)' == '*Undefined*'\">{fallback}</SolutionDir></PropertyGroup>");
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-SOLUTIONDIR",
                "Fallback para $(SolutionDir) adicionado",
                "O projeto usa $(SolutionDir), que fica indefinido ao compilar o projeto isoladamente (dotnet build do .csproj, pipelines de CI).",
                "Nenhuma ação necessária.", auto: true));
        }

        if (project.PreBuildEvent != null || project.PostBuildEvent != null)
        {
            spec.PreBuildCommand = project.PreBuildEvent;
            spec.PostBuildCommand = project.PostBuildEvent;
            items.Add(Item(project, InventorySeverity.Warning, InventoryCategory.ProjectFile, "PRJ-BUILDEVENTS",
                "Pre/Post-build events convertidos em targets",
                "Os comandos foram mantidos em targets PreBuild/PostBuild.",
                "A pasta de saída agora inclui o framework (bin\\Debug\\net10.0\\); confira os usos de $(TargetDir)/$(OutDir) e caminhos fixos."));
        }

        return spec;
    }

    private static void AddItemMetadata(ProjectFileSpec spec, Entry entry, bool isWeb)
    {
        var item = entry.Item;
        var ext = Path.GetExtension(item.FullPath).ToLowerInvariant();
        var include = entry.Relative;
        var defaultType = ext switch
        {
            ".cs" => "Compile",
            ".resx" => "EmbeddedResource",
            ".cshtml" or ".razor" => "Content",
            ".config" or ".json" when isWeb => "Content",
            _ => "None"
        };
        var metadata = InterestingMetadata
            .Where(item.Metadata.ContainsKey)
            .Select(k => new XAttribute(k, item.Metadata[k]))
            .ToList();

        var originalType = item.ItemType;
        if (originalType == "Content" && !isWeb)
        {
            if (metadata.Count > 0) spec.Items.Add(new XElement("None", new XAttribute("Update", include), metadata));
            return;
        }
        if (originalType is "Content" && isWeb && defaultType == "None")
        {
            spec.Items.Add(new XElement("None", new XAttribute("Remove", include)));
            spec.Items.Add(new XElement("Content", new XAttribute("Include", include), metadata));
            return;
        }
        if (originalType == "None" && defaultType == "Content")
        {
            spec.Items.Add(new XElement("Content", new XAttribute("Remove", include)));
            spec.Items.Add(new XElement("None", new XAttribute("Include", include), metadata));
            return;
        }
        if (originalType == defaultType || originalType is "Compile" or "Content" or "None")
        {
            if (metadata.Count > 0) spec.Items.Add(new XElement(defaultType, new XAttribute("Update", include), metadata));
            return;
        }

        if (originalType is "Page" or "ApplicationDefinition" && ext == ".xaml") return;
        if (originalType == "EntityDeploy" || originalType == "TypeScriptCompile" || originalType == "EmbeddedResource" || originalType == "Resource" ||
            originalType == "SplashScreen" || originalType == "Analyzer" || originalType == "AdditionalFiles")
        {
            spec.Items.Add(new XElement(defaultType, new XAttribute("Remove", include)));
            spec.Items.Add(new XElement(originalType, new XAttribute("Include", include), metadata));
        }
    }

    private static void AddBinaryReference(ProjectInfo project, ProjectMigrationContext ctx, BinaryReference binary, ProjectFileSpec spec, OutputPlan plan,
        List<InventoryItem> items, string projectOut)
    {
        if (!binary.Exists)
        {
            spec.References.Add((binary.Name, binary.HintPath));
            items.Add(Item(project, InventorySeverity.Breaking, InventoryCategory.ProjectFile, "PRJ-DLL-MISSING",
                $"DLL referenciada não encontrada: {binary.Name}", binary.HintPath,
                "Restaure a DLL (pasta lib/ do repositório) ou substitua por um pacote NuGet."));
            return;
        }

        var root = ctx.RootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string hint;
        if (binary.HintPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            var fromRoot = Path.GetRelativePath(ctx.RootDir, binary.HintPath);
            plan.Copy(binary.HintPath, fromRoot);
            foreach (var sibling in new[] { ".xml", ".pdb" }.Select(e => Path.ChangeExtension(binary.HintPath, e)).Where(File.Exists))
                plan.Copy(sibling, Path.GetRelativePath(ctx.RootDir, sibling));
            hint = Path.GetRelativePath(project.ProjectDir, binary.HintPath);
        }
        else hint = binary.HintPath;
        spec.References.Add((binary.Name, hint));

        var inspection = AssemblyInspector.Inspect(binary.HintPath);
        var (severity, description, suggestion) = inspection switch
        {
            { ReferencesSystemWeb: true } => (InventorySeverity.Breaking,
                $"A DLL depende de System.Web ({inspection.TargetFramework ?? ".NET Framework"}) e não funcionará no .NET 10.",
                "Obtenha uma versão da biblioteca para .NET moderno ou reescreva a funcionalidade."),
            { Flavor: AssemblyFlavor.Modern } => (InventorySeverity.Info,
                $"Compilada para {inspection.TargetFramework ?? "netstandard/.NET"}: compatível.",
                "Considere publicar a DLL como pacote NuGet interno."),
            { Flavor: AssemblyFlavor.NetFramework } => (InventorySeverity.Warning,
                $"Compilada para {inspection.TargetFramework ?? ".NET Framework"}. Costuma carregar no .NET 10, mas falha se usar APIs removidas (AppDomain, Remoting, WCF server, System.Web...).",
                "Teste os fluxos que usam a DLL; o ideal é recompilá-la para netstandard2.0/net10.0 ou trocar por pacote NuGet."),
            { Flavor: AssemblyFlavor.NotManaged } => (InventorySeverity.Warning,
                "DLL nativa referenciada como assembly .NET.",
                "Copie a DLL nativa para a saída (<None Include=... CopyToOutputDirectory=PreserveNewest />) e acesse via P/Invoke."),
            _ => (InventorySeverity.Warning, "Não foi possível identificar o framework da DLL.", "Teste os fluxos que usam a DLL.")
        };
        items.Add(Item(project, severity, InventoryCategory.ProjectFile, "PRJ-DLL", $"Referência a DLL local: {binary.Name}", description, suggestion, hint,
            auto: severity == InventorySeverity.Info));
    }

    private static string Relative(string root, string dir)
    {
        var rel = Path.GetRelativePath(root, dir);
        return rel == "." ? "" : rel;
    }

    private static InventoryItem Item(ProjectInfo project, InventorySeverity severity, InventoryCategory category, string rule, string title,
        string description, string suggestion, string? file = null, bool auto = false) => new()
    {
        Project = project.Name, Severity = severity, Category = category, RuleId = rule, Title = title,
        Description = description, Suggestion = suggestion, FilePath = file, AutoMigrated = auto
    };

    private sealed class LineIndex
    {
        private readonly string _text;
        private readonly List<int> _starts = [0];

        public LineIndex(string text)
        {
            _text = text;
            for (var i = 0; i < text.Length; i++)
                if (text[i] == '\n') _starts.Add(i + 1);
        }

        public (int Line, string Content) Locate(int offset)
        {
            var index = _starts.BinarySearch(offset);
            if (index < 0) index = ~index - 1;
            var start = _starts[index];
            var end = index + 1 < _starts.Count ? _starts[index + 1] - 1 : _text.Length;
            return (index + 1, _text[start..Math.Max(start, end)]);
        }
    }

    [GeneratedRegex(@"^Areas/([^/]+)/Controllers/", RegexOptions.IgnoreCase)]
    private static partial Regex AreaController();

    [GeneratedRegex(@"using\s+System\.Web|using\s+Owin|using\s+Microsoft\.Owin|IAppBuilder|HttpConfiguration|RouteCollection|BundleCollection|GlobalFilterCollection|WebActivatorEx|PreApplicationStartMethod|\bUnity\b|Ninject|AreaRegistration")]
    private static partial Regex LegacyAppStart();

    [GeneratedRegex(@":\s*(System\.Web\.Mvc\.)?AreaRegistration\b")]
    private static partial Regex AreaRegistrationClass();

    [GeneratedRegex(@"^\s*\[assembly:\s*(System\.Reflection\.)?Assembly(Title|Description|Configuration|Company|Product|Copyright|Trademark|Culture|Version|FileVersion|InformationalVersion)(Attribute)?\s*\(", RegexOptions.Multiline)]
    private static partial Regex AssemblyAttribute();

    [GeneratedRegex(@"Assembly(File)?Version(Attribute)?\s*\(\s*""[^""]*\*")]
    private static partial Regex WildcardVersion();
}
