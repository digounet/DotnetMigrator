using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>
/// SDK-style projects that already target netstandard/netcoreapp/net5+ are not rewritten by the legacy pipeline, but they
/// must still end on .NET 10: this rewrites TargetFramework(s) (dropping .NET Framework monikers from multi-target lists,
/// keeping OS suffixes such as -windows) and aligns PackageReference versions through the same planner as the other projects.
/// </summary>
public static partial class ModernProjectUpdater
{
    public const string Target = "net10.0";

    public static async Task<(string Csproj, string TfmBefore, string TfmAfter, int UpdatedPackages, List<InventoryItem> Items)> UpdateAsync(ProjectInfo project, PackagePlanner planner)
    {
        var text = File.ReadAllText(project.ProjectPath);
        var doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        var root = doc.Root!;

        var tfmElements = root.Descendants().Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks").ToList();
        var before = string.Join(";", tfmElements.Select(e => e.Value.Trim()).Where(v => v.Length > 0).Distinct());
        var after = Upgrade(before);
        foreach (var element in tfmElements)
        {
            var upgraded = Upgrade(element.Value);
            var multiple = upgraded.Contains(';');
            element.Name = XName.Get(multiple ? "TargetFrameworks" : "TargetFramework", element.Name.NamespaceName);
            element.Value = upgraded;
        }

        var updated = 0;
        var items = new List<InventoryItem>();
        if (project.Packages.Count > 0)
        {
            var plan = await planner.PlanAsync(project, []);
            items.AddRange(plan.Items);
            var versions = plan.References.ToDictionary(r => r.Id, r => r.Version, StringComparer.OrdinalIgnoreCase);
            foreach (var reference in root.Descendants().Where(e => e.Name.LocalName == "PackageReference"))
            {
                var id = reference.Attribute("Include")?.Value;
                if (id == null || !versions.TryGetValue(id, out var version)) continue;
                var attribute = reference.Attribute("Version");
                var child = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "Version");
                var current = attribute?.Value ?? child?.Value;
                if (current == null || current == version || current.Contains('$')) continue;
                if (attribute != null) attribute.Value = version; else child!.Value = version;
                updated++;
            }
        }

        var settings = new XmlWriterSettings { OmitXmlDeclaration = doc.Declaration == null, Indent = false, Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings)) doc.Save(writer);
        var csproj = sb.ToString();
        if (!csproj.EndsWith('\n')) csproj += Environment.NewLine;
        return (csproj, before, after, updated, items);
    }

    /// <summary>"net48;netstandard2.0" → "net10.0"; "net6.0-windows" → "net10.0-windows"; "netcoreapp3.1" → "net10.0"; already net10.0 stays.</summary>
    public static string Upgrade(string tfms)
    {
        var result = new List<string>();
        foreach (var raw in tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = Moniker().Match(raw);
            if (!m.Success) { if (!result.Contains(raw)) result.Add(raw); continue; } // conditions/variables: leave untouched
            var family = m.Groups["family"].Value.ToLowerInvariant();
            var major = int.TryParse(m.Groups["major"].Value, out var mj) ? mj : 0;
            var suffix = m.Groups["suffix"].Value; // -windows, -windows10.0.19041.0, -android...
            var legacy = family == "net" && !raw.Contains('.') && major < 5; // net45, net472, net48
            if (legacy) continue;
            var upgraded = Target + suffix;
            if (!result.Contains(upgraded)) result.Add(upgraded);
        }
        return result.Count == 0 ? Target : string.Join(";", result);
    }

    [GeneratedRegex(@"^(?<family>netstandard|netcoreapp|net)(?<major>\d+)(?:\.\d+)?(?<suffix>-[A-Za-z]+[\d.]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Moniker();
}
