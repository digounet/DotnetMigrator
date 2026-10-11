using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>
/// appsettings*.json of projects that are already on .NET (Core/5+/netstandard): what the XML config analysis does for
/// web.config/app.config. Connection strings, credential-looking keys, URLs, e-mails and UNC paths feed the profile
/// (databases, signals), the externalized settings (one IaC parameter per URL/e-mail, with the per-environment values the
/// appsettings.{Environment}.json files already declare) and the secrets plan (through <see cref="SecretsExtractor.Extract"/>,
/// which also puts the placeholder in the file).
/// </summary>
public static partial class AppSettingsAnalyzer
{
    /// <param name="EnvironmentFiles">environment → full path.</param>
    /// <param name="RelativePaths">environment → path relative to the project folder (where the scrubbed copy is written back); the base file is under the key "".</param>
    /// <param name="Originals">relative path → original text, so only files that really changed are rewritten.</param>
    public sealed record AppSettingsFiles(ConfigMigrationResult Config, string BaseFile, Dictionary<string, string> EnvironmentFiles,
        Dictionary<string, string> RelativePaths, Dictionary<string, string> Originals);

    private static readonly string[] ConfigFolders = ["Resources", "Config", "Configs", "Configuration", "Settings"];
    private static readonly string[] IgnoredFolders = ["bin", "obj", "node_modules", ".vs", "wwwroot", "Properties"];
    private static readonly string[] IgnoredFiles = ["launchSettings.json", "package.json", "package-lock.json", "global.json", "libman.json", "bundleconfig.json", "nuget.json"];

    /// <summary>Environment names the platform uses (DES/PRD...) mapped to the ASP.NET Core ones the IaC generators already understand.</summary>
    internal static string NormalizeEnvironment(string name) => name.ToUpperInvariant() switch
    {
        "DES" or "DEV" or "DEVELOPMENT" => "Development",
        "HML" or "HOM" or "HOMOLOG" or "HOMOLOGACAO" or "QA" or "TST" or "TEST" or "UAT" or "STG" or "STAGING" => "Staging",
        "PRD" or "PROD" or "PRODUCTION" => "Production",
        _ => name
    };

    /// <summary>
    /// Reads appsettings.json, appsettings.&lt;Environment&gt;.json next to the project file and the environment JSON files that
    /// projects keep under Resources/Config/Settings (<c>Resources/DES.json</c>, <c>Resources/PRD.json</c>, <c>DES.local.json</c>), which the
    /// code loads with AddJsonFile($"{environment}.json"); null when the project has none.
    /// </summary>
    public static AppSettingsFiles? Load(ProjectInfo project)
    {
        var dir = project.ProjectDir;
        if (!Directory.Exists(dir)) return null;
        var environments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? basePath = null;
        foreach (var file in Directory.EnumerateFiles(dir, "appsettings*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase)) { basePath = file; continue; }
            var parts = name.Split('.');
            if (parts.Length != 3 || parts[1].Equals("Secrets", StringComparison.OrdinalIgnoreCase)) continue;
            environments[parts[1]] = file;
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var relativeParts = Path.GetRelativePath(dir, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (relativeParts.Length < 2 || relativeParts.Take(relativeParts.Length - 1).Any(p => IgnoredFolders.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            if (!relativeParts.Take(relativeParts.Length - 1).Any(p => ConfigFolders.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
            var name = relativeParts[^1];
            if (IgnoredFiles.Contains(name, StringComparer.OrdinalIgnoreCase) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) || name.Contains("runtimeconfig", StringComparison.OrdinalIgnoreCase) || name.StartsWith("tsconfig", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = Path.GetFileNameWithoutExtension(name);
            if (stem.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)) stem = stem["appsettings.".Length..];
            else if (stem.Equals("appsettings", StringComparison.OrdinalIgnoreCase)) continue;
            // Only objects count as configuration (a JSON array of test data is not).
            if (!TextFiles.Read(file).Text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('{')) continue;
            environments.TryAdd(NormalizeEnvironment(stem), file);
        }
        if (basePath == null && environments.Count == 0) return null;
        var config = new ConfigMigrationResult();
        var relative = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (basePath != null)
        {
            config.AppSettingsJson = TextFiles.Read(basePath).Text;
            relative[""] = Path.GetRelativePath(dir, basePath);
            originals[relative[""]] = config.AppSettingsJson;
        }
        foreach (var (environment, file) in environments)
        {
            var text = TextFiles.Read(file).Text;
            config.EnvironmentJson[environment] = text;
            relative[environment] = Path.GetRelativePath(dir, file);
            originals[relative[environment]] = text;
        }
        return new AppSettingsFiles(config, basePath != null ? Path.GetFileName(basePath) : "appsettings.json", environments, relative, originals);
    }

    /// <summary>Databases, UNC/Windows paths, external endpoints and credentials, as the XML config scan records them.</summary>
    public static void Profile(ApplicationProfile profile, ProjectInfo project, AppSettingsFiles files)
    {
        var providerHint = project.Packages.Select(p => p.Id).FirstOrDefault(id => id.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) || id.Contains("MySql", StringComparison.OrdinalIgnoreCase) || id.Contains("Oracle", StringComparison.OrdinalIgnoreCase) || id.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)) ?? "";
        foreach (var (fileName, json) in Texts(files))
            foreach (var (path, key, value) in Leaves(json))
            {
                if (path.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase) || key.EndsWith("ConnectionString", StringComparison.OrdinalIgnoreCase))
                {
                    if (profile.Databases.Any(d => d.Name.Equals(key, StringComparison.OrdinalIgnoreCase))) continue;
                    var db = ApplicationProfiler.ParseConnectionString(key, value, providerHint, project.Name);
                    profile.Databases.Add(db);
                    profile.Add(ApplicationProfiler.DatabaseSignal(db.Provider), fileName, db.Server != null ? $"{db.Server}/{db.Database}" : db.Database);
                    if (SecretsExtractor.HasCredentialInConnectionString(value)) profile.Add(Signal.SecretsInConfig, fileName, path);
                    continue;
                }
                if (Url().IsMatch(value) && !value.Contains("localhost", StringComparison.OrdinalIgnoreCase)) profile.ExternalEndpoints.Add(UrlHost().Match(value).Value);
                if (value.StartsWith(@"\\", StringComparison.Ordinal)) profile.Add(Signal.UncPaths, fileName, value.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
                else if (WindowsPath().IsMatch(value)) profile.Add(Signal.WindowsPaths, fileName, value);
                if (SecretsExtractor.IsSecretKey(key) && value.Length >= 3 && !value.StartsWith("<secret:", StringComparison.Ordinal)) profile.Add(Signal.SecretsInConfig, fileName, path);
            }
    }

    /// <summary>URLs and e-mails become parameters (Key = the IConfiguration path as the code reads it), with the value of each environment file.</summary>
    public static List<ExternalizedSetting> Settings(ProjectInfo project, AppSettingsFiles files)
    {
        var settings = new List<ExternalizedSetting>();
        if (files.Config.AppSettingsJson != null)
            foreach (var (path, _, value) in Leaves(files.Config.AppSettingsJson))
            {
                var kind = KindOf(value);
                if (kind == null || path.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)) continue;
                settings.Add(new ExternalizedSetting { Project = project.Name, Kind = kind.Value, Key = path, Value = value, Source = SettingSource.Config, Location = files.BaseFile });
            }
        foreach (var (environment, json) in files.Config.EnvironmentJson)
            foreach (var (path, _, value) in Leaves(json))
            {
                var setting = settings.FirstOrDefault(s => s.Key.Equals(path, StringComparison.OrdinalIgnoreCase));
                if (setting != null) { setting.EnvironmentValues[environment] = value; continue; }
                var kind = KindOf(value);
                if (kind == null || path.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)) continue;
                var created = new ExternalizedSetting { Project = project.Name, Kind = kind.Value, Key = path, Value = value, Source = SettingSource.Config, Location = files.RelativePaths.TryGetValue(environment, out var rel) ? rel : $"appsettings.{environment}.json", EnvironmentOnly = true };
                created.EnvironmentValues[environment] = value;
                settings.Add(created);
            }
        return settings;
    }

    /// <summary>Counts for the inventory item: connection strings, credentials, URLs/e-mails, UNC paths.</summary>
    public static (int ConnectionStrings, int Secrets, int Endpoints, int UncPaths) Summary(AppSettingsFiles files)
    {
        int cs = 0, secrets = 0, endpoints = 0, unc = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, json) in Texts(files))
            foreach (var (path, key, value) in Leaves(json))
            {
                if (!seen.Add(path)) continue;
                if (path.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)) { cs++; if (SecretsExtractor.HasCredentialInConnectionString(value)) secrets++; continue; }
                if (SecretsExtractor.IsSecretKey(key) && value.Length >= 3) secrets++;
                else if (KindOf(value) is { } kind && kind != SettingKind.Path) endpoints++;
                if (value.StartsWith(@"\\", StringComparison.Ordinal)) unc++;
            }
        return (cs, secrets, endpoints, unc);
    }

    private static IEnumerable<(string FileName, string Json)> Texts(AppSettingsFiles files)
    {
        if (files.Config.AppSettingsJson != null) yield return (files.BaseFile, files.Config.AppSettingsJson);
        foreach (var (environment, json) in files.Config.EnvironmentJson) yield return (files.RelativePaths.TryGetValue(environment, out var rel) ? rel : $"appsettings.{environment}.json", json);
    }

    /// <summary>Every string leaf as (path "A:B:C", key "C", value).</summary>
    internal static IEnumerable<(string Path, string Key, string Value)> Leaves(string json)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException) { yield break; }
        if (root is not JsonObject obj) yield break;
        var stack = new Stack<(JsonObject Node, string Path)>();
        stack.Push((obj, ""));
        while (stack.Count > 0)
        {
            var (node, path) = stack.Pop();
            foreach (var (key, child) in node)
            {
                var childPath = path.Length == 0 ? key : $"{path}:{key}";
                switch (child)
                {
                    case JsonObject o: stack.Push((o, childPath)); break;
                    case JsonValue v when v.TryGetValue<string>(out var text) && text.Length > 0: yield return (childPath, key, text); break;
                }
            }
        }
    }

    private static SettingKind? KindOf(string value)
    {
        if (Url().IsMatch(value) && Url().Match(value).Index == 0 && !value.Contains(' ')) return SettingKind.Url;
        if (Email().IsMatch(value)) return SettingKind.Email;
        if (UncPath().IsMatch(value)) return SettingKind.Path;
        return null;
    }

    [GeneratedRegex(@"https?://[\w.\-]+(?::\d+)?(?:/\S*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"https?://[\w.\-]+(?::\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlHost();

    [GeneratedRegex(@"^[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+(?:\s*[;,]\s*[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+)*$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^\\\\[\w.\-]+\\\S")]
    private static partial Regex UncPath();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex WindowsPath();


}
