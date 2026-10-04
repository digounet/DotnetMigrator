using System.Xml.Linq;

namespace Migrator.Core.NuGet;

/// <summary>A NuGet v3 feed (nuget.org or a private Artifactory/Nexus/Azure Artifacts remote) with optional basic credentials.</summary>
public sealed record NuGetSource(string Name, string IndexUrl, string? UserName = null, string? Password = null)
{
    public static readonly NuGetSource NuGetOrg = new("nuget.org", "https://api.nuget.org/v3/index.json");
    public bool IsNuGetOrg => IndexUrl.Contains("api.nuget.org", StringComparison.OrdinalIgnoreCase);
    public bool HasCredentials => !string.IsNullOrEmpty(UserName) && !string.IsNullOrEmpty(Password);
    public override string ToString() => $"{Name} ({IndexUrl})";
}

/// <summary>Reads the sources of a nuget.config (packageSources + packageSourceCredentials). Environment variables in %VAR% form are expanded.</summary>
public static class NuGetConfigFile
{
    public static IReadOnlyList<NuGetSource> Parse(string path)
    {
        var doc = XDocument.Load(path);
        var root = doc.Root ?? throw new InvalidOperationException($"{path}: XML vazio.");
        var sources = new List<NuGetSource>();
        var disabled = root.Element("disabledPackageSources")?.Elements("add").Where(a => (a.Attribute("value")?.Value ?? "").Equals("true", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Attribute("key")?.Value ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var credentials = root.Element("packageSourceCredentials");
        foreach (var add in root.Element("packageSources")?.Elements("add") ?? [])
        {
            var key = add.Attribute("key")?.Value ?? "";
            var value = Expand(add.Attribute("value")?.Value ?? "");
            if (key.Length == 0 || !value.StartsWith("http", StringComparison.OrdinalIgnoreCase) || disabled.Contains(key)) continue;
            string? user = null, password = null;
            // Credential element names are the source key with spaces encoded (NuGet escapes them as _x0020_).
            var credentialElement = credentials?.Elements().FirstOrDefault(e => Decode(e.Name.LocalName).Equals(key, StringComparison.OrdinalIgnoreCase));
            if (credentialElement != null)
            {
                string? Get(string name) => credentialElement.Elements("add").FirstOrDefault(a => (a.Attribute("key")?.Value ?? "").Equals(name, StringComparison.OrdinalIgnoreCase))?.Attribute("value")?.Value;
                user = Expand(Get("Username") ?? "");
                password = Expand(Get("ClearTextPassword") ?? "");
                if (string.IsNullOrEmpty(password) && Get("Password") != null) password = null; // encrypted (DPAPI, Windows-only): cannot be used here
            }
            sources.Add(new NuGetSource(key, value, string.IsNullOrEmpty(user) ? null : user, string.IsNullOrEmpty(password) ? null : password));
        }
        return sources;
    }

    /// <summary>The source the migrator should use for compatibility lookups: the first enabled v3 feed, or nuget.org when the file has none.</summary>
    public static NuGetSource Primary(string path)
    {
        var sources = Parse(path);
        return sources.FirstOrDefault(s => s.IndexUrl.EndsWith("index.json", StringComparison.OrdinalIgnoreCase))
               ?? sources.FirstOrDefault()
               ?? NuGetSource.NuGetOrg;
    }

    public static string? Find(string directory)
    {
        if (!Directory.Exists(directory)) return null;
        return Directory.EnumerateFiles(directory).FirstOrDefault(f => Path.GetFileName(f).Equals("nuget.config", StringComparison.OrdinalIgnoreCase));
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value);

    private static string Decode(string name) => name.Replace("_x0020_", " ").Replace("_x002E_", ".").Replace("_x002D_", "-");
}
