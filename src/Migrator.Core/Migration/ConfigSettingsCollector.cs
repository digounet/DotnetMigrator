using System.Text.RegularExpressions;
using System.Xml.Linq;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>
/// appSettings whose value is a URL or an e-mail address are environment-specific by nature: they become IaC parameters,
/// with the per-environment values the config transforms (Web.Release.config → Production, Web.Debug.config → Development,
/// others by their configuration name) already declare.
/// </summary>
public static partial class ConfigSettingsCollector
{
    private static readonly XNamespace Xdt = "http://schemas.microsoft.com/XML-Document-Transform";

    public static List<ExternalizedSetting> Collect(ProjectInfo project, XElement? config)
    {
        var settings = new List<ExternalizedSetting>();
        if (config == null) return settings;
        var fileName = project.ConfigFilePath != null ? Path.GetFileName(project.ConfigFilePath) : "config";
        foreach (var add in config.Element("appSettings")?.Elements("add") ?? [])
        {
            var key = add.Attribute("key")?.Value;
            var value = add.Attribute("value")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(key)) continue;
            var kind = KindOf(value);
            if (kind == null) continue;
            var setting = new ExternalizedSetting { Project = project.Name, Kind = kind.Value, Key = "AppSettings:" + key, Value = value, Source = SettingSource.Config, Location = fileName };
            settings.Add(setting);
        }
        if (settings.Count == 0) return settings;

        foreach (var transform in project.ConfigTransformFiles)
        {
            var environment = EnvironmentOf(transform);
            if (environment == null) continue;
            XDocument doc;
            try { doc = XDocument.Load(transform); }
            catch (Exception) { continue; }
            foreach (var add in doc.Descendants("appSettings").Elements("add"))
            {
                if (add.Attribute(Xdt + "Transform") == null) continue;
                var key = add.Attribute("key")?.Value;
                var value = add.Attribute("value")?.Value;
                if (key == null || value == null) continue;
                var setting = settings.FirstOrDefault(s => s.Key.Equals("AppSettings:" + key, StringComparison.OrdinalIgnoreCase));
                if (setting != null) setting.EnvironmentValues[environment] = value;
            }
        }
        return settings;
    }

    /// <summary>Web.Release.config → Production, Web.Debug.config → Development, Web.Homolog.config → Homolog.</summary>
    internal static string? EnvironmentOf(string transformPath)
    {
        var parts = Path.GetFileName(transformPath).Split('.');
        if (parts.Length < 3) return null;
        return parts[1].ToLowerInvariant() switch { "debug" => "Development", "release" => "Production", var other => parts[1] };
    }

    private static SettingKind? KindOf(string value)
    {
        if (Url().IsMatch(value) && !value.Contains("localhost", StringComparison.OrdinalIgnoreCase) && !value.Contains("127.0.0.1", StringComparison.Ordinal)) return SettingKind.Url;
        if (Url().IsMatch(value)) return SettingKind.Url; // localhost in the base config still deserves a parameter: the transforms carry the real hosts
        if (Email().IsMatch(value)) return SettingKind.Email;
        return null;
    }

    [GeneratedRegex(@"^https?://[\w.\-]+(?::\d+)?(?:/\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"^[\w.+\-]+@[\w\-]+(?:\.[\w\-]+)+$")]
    private static partial Regex Email();
}
