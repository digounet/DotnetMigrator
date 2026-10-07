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
    public sealed record AppSettingsFiles(ConfigMigrationResult Config, string BaseFile, Dictionary<string, string> EnvironmentFiles);

    /// <summary>Reads appsettings.json and appsettings.&lt;Environment&gt;.json next to the project file; null when the project has none.</summary>
    public static AppSettingsFiles? Load(ProjectInfo project)
    {
        var dir = project.ProjectDir;
        if (!Directory.Exists(dir)) return null;
        var files = Directory.EnumerateFiles(dir, "appsettings*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var basePath = files.FirstOrDefault(f => Path.GetFileName(f).Equals("appsettings.json", StringComparison.OrdinalIgnoreCase));
        var environments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            var parts = Path.GetFileName(file).Split('.');
            if (parts.Length != 3 || parts[1].Equals("Secrets", StringComparison.OrdinalIgnoreCase)) continue;
            environments[parts[1]] = file;
        }
        if (basePath == null && environments.Count == 0) return null;
        var config = new ConfigMigrationResult { AppSettingsJson = basePath != null ? TextFiles.Read(basePath).Text : null };
        foreach (var (environment, file) in environments) config.EnvironmentJson[environment] = TextFiles.Read(file).Text;
        return new AppSettingsFiles(config, basePath != null ? Path.GetFileName(basePath) : "appsettings.json", environments);
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
                    if (PasswordInConnectionString().IsMatch(value)) profile.Add(Signal.SecretsInConfig, fileName, path);
                    continue;
                }
                if (Url().IsMatch(value) && !value.Contains("localhost", StringComparison.OrdinalIgnoreCase)) profile.ExternalEndpoints.Add(UrlHost().Match(value).Value);
                if (value.StartsWith(@"\\", StringComparison.Ordinal)) profile.Add(Signal.UncPaths, fileName, value.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
                else if (WindowsPath().IsMatch(value)) profile.Add(Signal.WindowsPaths, fileName, value);
                if (SecretKey().IsMatch(key) && value.Length >= 3 && !value.StartsWith("<secret:", StringComparison.Ordinal)) profile.Add(Signal.SecretsInConfig, fileName, path);
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
                var created = new ExternalizedSetting { Project = project.Name, Kind = kind.Value, Key = path, Value = value, Source = SettingSource.Config, Location = $"appsettings.{environment}.json" };
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
                if (path.StartsWith("ConnectionStrings:", StringComparison.OrdinalIgnoreCase)) { cs++; if (PasswordInConnectionString().IsMatch(value)) secrets++; continue; }
                if (SecretKey().IsMatch(key) && value.Length >= 3) secrets++;
                else if (KindOf(value) != null) endpoints++;
                if (value.StartsWith(@"\\", StringComparison.Ordinal)) unc++;
            }
        return (cs, secrets, endpoints, unc);
    }

    private static IEnumerable<(string FileName, string Json)> Texts(AppSettingsFiles files)
    {
        if (files.Config.AppSettingsJson != null) yield return (files.BaseFile, files.Config.AppSettingsJson);
        foreach (var (environment, json) in files.Config.EnvironmentJson) yield return ($"appsettings.{environment}.json", json);
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
        return null;
    }

    [GeneratedRegex(@"https?://[\w.\-]+(?::\d+)?(?:/\S*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"https?://[\w.\-]+(?::\d+)?", RegexOptions.IgnoreCase)]
    private static partial Regex UrlHost();

    [GeneratedRegex(@"^[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+$")]
    private static partial Regex Email();

    [GeneratedRegex(@"^[A-Za-z]:\\")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"password|pwd|secret|apikey|api_key|api-key|clientsecret|accesskey|access_key|token|senha|chave|(?-i:Pw)(?=[A-Z_\d]|$)|_pw$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKey();

    [GeneratedRegex(@"(password|pwd)\s*=\s*[^;]{1,}", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordInConnectionString();
}
