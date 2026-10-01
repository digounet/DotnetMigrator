using System.Xml.Linq;
using Migrator.Core.Models;
using Migrator.Core.NuGet;

namespace Migrator.Core.Analysis;

public static class ProjectLoader
{
    private static readonly HashSet<string> NonFileItems = new(StringComparer.OrdinalIgnoreCase)
    {
        "Reference", "ProjectReference", "PackageReference", "COMReference", "COMFileReference", "Folder",
        "BootstrapperPackage", "Service", "WCFMetadata", "WCFMetadataStorage", "WebReferences", "WebReferenceUrl",
        "Analyzer", "CodeAnalysisDictionary", "Import", "FrameworkReference", "PackageVersion", "InternalsVisibleTo",
        "Using", "ProjectCapability", "SupportedPlatform"
    };

    private static readonly HashSet<string> IgnoredMetadata = new(StringComparer.OrdinalIgnoreCase)
    {
        "Include", "Condition", "Remove", "Update", "Exclude", "SubType", "Link"
    };

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".vs", "node_modules", "packages", "TestResults"
    };

    public static ProjectInfo Load(string projectPath, string rootDir)
    {
        var doc = XDocument.Load(projectPath);
        var root = doc.Root!;
        var dir = Path.GetDirectoryName(projectPath)!;
        var name = Path.GetFileNameWithoutExtension(projectPath);

        var info = new ProjectInfo { ProjectPath = projectPath, Name = name };
        info.IsSdkStyle = root.Attribute("Sdk") != null ||
                          root.Elements().Any(e => e.Name.LocalName == "Sdk") ||
                          root.Elements().Any(e => e.Name.LocalName == "Import" && e.Attribute("Sdk") != null);

        var tfm = Prop(root, "TargetFrameworkVersion") ?? Prop(root, "TargetFramework") ?? Prop(root, "TargetFrameworks") ?? "";
        info.TargetFramework = tfm;
        info.IsAlreadyModern = info.IsSdkStyle && tfm.Split(';').Any(TargetFrameworks.IsModern);
        info.OutputType = Prop(root, "OutputType") ?? "Library";
        info.AssemblyName = Prop(root, "AssemblyName") ?? name;
        info.RootNamespace = Prop(root, "RootNamespace") ?? name;
        info.IisUrl = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "IISUrl")?.Value;
        info.IisSslPort = Prop(root, "IISExpressSSLPort");

        CarryProperties(root, info, dir);
        ReadItems(root, info, dir, rootDir);
        ReadPackagesConfig(info, dir);
        ReadBuildCustomizations(root, info);

        if (info.IsSdkStyle) AddGlobbedFiles(info, dir);

        info.ConfigFilePath = FindFile(dir, "web.config") ?? FindFile(dir, "app.config");
        if (info.ConfigFilePath != null)
        {
            var baseName = Path.GetFileNameWithoutExtension(info.ConfigFilePath);
            info.ConfigTransformFiles.AddRange(Directory.EnumerateFiles(dir, baseName + ".*.config")
                .Where(f => !Path.GetFileName(f).Equals(Path.GetFileName(info.ConfigFilePath), StringComparison.OrdinalIgnoreCase)));
        }

        DetectKind(root, info, dir);
        return info;
    }

    private static string? Prop(XElement root, string name)
    {
        var candidates = root.Elements().Where(e => e.Name.LocalName == "PropertyGroup")
            .SelectMany(g => g.Elements().Where(e => e.Name.LocalName == name).Select(e => (Group: g, Element: e)))
            .ToList();
        var unconditioned = candidates.FirstOrDefault(c => c.Group.Attribute("Condition") == null && c.Element.Attribute("Condition") == null);
        var value = (unconditioned.Element ?? candidates.FirstOrDefault().Element)?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static void CarryProperties(XElement root, ProjectInfo info, string dir)
    {
        foreach (var name in new[] { "AllowUnsafeBlocks", "SignAssembly", "AssemblyOriginatorKeyFile", "DelaySign", "ApplicationIcon", "ApplicationManifest", "StartupObject", "NoWarn" })
            if (Prop(root, name) is { } value)
                info.CarriedProperties[name] = value;

        var platforms = root.Descendants().Where(e => e.Name.LocalName == "PlatformTarget")
            .Select(e => e.Value.Trim()).Where(v => v is "x86" or "x64" or "ARM64").Distinct().ToList();
        if (platforms.Count == 1) info.CarriedProperties["PlatformTarget"] = platforms[0];

        if (root.Descendants().Any(e => e.Name.LocalName == "DocumentationFile"))
            info.CarriedProperties["GenerateDocumentationFile"] = "true";

        var constantGroups = root.Descendants().Where(e => e.Name.LocalName == "DefineConstants")
            .Select(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(c => c is not ("DEBUG" or "TRACE") && !c.StartsWith("$(")).ToHashSet(StringComparer.Ordinal))
            .ToList();
        if (constantGroups.Count > 0)
            info.DefineConstants.AddRange(constantGroups.Skip(1).Aggregate(constantGroups[0], (acc, g) => { acc.IntersectWith(g); return acc; }));
    }

    private static void ReadItems(XElement root, ProjectInfo info, string dir, string rootDir)
    {
        foreach (var item in root.Elements().Where(e => e.Name.LocalName == "ItemGroup").SelectMany(g => g.Elements()))
        {
            var type = item.Name.LocalName;
            var include = item.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include)) continue;

            switch (type)
            {
                case "Reference":
                    ReadReference(item, include, info, dir, rootDir);
                    continue;
                case "ProjectReference":
                    info.ProjectReferences.Add(Path.GetFullPath(Path.Combine(dir, Normalize(include))));
                    continue;
                case "PackageReference":
                    var version = item.Attribute("Version")?.Value ?? Child(item, "Version") ?? "";
                    info.Packages.Add(new PackageInfo(include, version,
                        (item.Attribute("PrivateAssets")?.Value ?? Child(item, "PrivateAssets"))?.Contains("all", StringComparison.OrdinalIgnoreCase) == true));
                    continue;
                case "COMReference":
                    info.ComReferences.Add(StripNamespace(item).ToString());
                    continue;
            }

            if (NonFileItems.Contains(type)) continue;

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var attr in item.Attributes().Where(a => !IgnoredMetadata.Contains(a.Name.LocalName)))
                metadata[attr.Name.LocalName] = attr.Value;
            foreach (var child in item.Elements().Where(c => !IgnoredMetadata.Contains(c.Name.LocalName)))
                metadata[child.Name.LocalName] = child.Value.Trim();
            var link = item.Attribute("Link")?.Value ?? Child(item, "Link");

            foreach (var part in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                foreach (var file in Expand(dir, Normalize(part)))
                {
                    if (info.Items.Any(i => i.FullPath.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
                    info.Items.Add(new ProjectItem { ItemType = type, FullPath = file, Link = link, Metadata = metadata });
                }
            }
        }
    }

    private static void ReadReference(XElement item, string include, ProjectInfo info, string dir, string rootDir)
    {
        var refName = include.Split(',')[0].Trim();
        var hint = Child(item, "HintPath");
        if (string.IsNullOrWhiteSpace(hint))
        {
            info.FrameworkReferences.Add(refName);
            return;
        }

        var expanded = hint
            .Replace("$(SolutionDir)", rootDir.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$(ProjectDir)", dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildProjectDirectory)", dir, StringComparison.OrdinalIgnoreCase)
            .Replace("$(MSBuildThisFileDirectory)", dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        if (expanded.Contains("$("))
        {
            info.BinaryReferences.Add(new BinaryReference(refName, hint, false));
            return;
        }

        var full = Path.GetFullPath(Path.Combine(dir, Normalize(expanded)));
        info.BinaryReferences.Add(new BinaryReference(refName, full, File.Exists(full)));
    }

    private static void ReadPackagesConfig(ProjectInfo info, string dir)
    {
        var path = FindFile(dir, "packages.config");
        if (path == null) return;

        foreach (var p in XDocument.Load(path).Descendants("package"))
        {
            var id = p.Attribute("id")?.Value;
            if (string.IsNullOrWhiteSpace(id) || info.Packages.Any(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            info.Packages.Add(new PackageInfo(id, p.Attribute("version")?.Value ?? "",
                p.Attribute("developmentDependency")?.Value.Equals("true", StringComparison.OrdinalIgnoreCase) == true));
        }

        // Assemblies coming from packages.config live under the solution's packages folder; they are restored by PackageReference now.
        info.BinaryReferences.RemoveAll(b => IsFromPackagesFolder(b.HintPath, info.Packages));
    }

    private static bool IsFromPackagesFolder(string hintPath, List<PackageInfo> packages)
    {
        var marker = $"{Path.DirectorySeparatorChar}packages{Path.DirectorySeparatorChar}";
        var idx = hintPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return false;
        var folder = hintPath[(idx + marker.Length)..].Split(Path.DirectorySeparatorChar)[0];
        return packages.Any(p => folder.StartsWith(p.Id + ".", StringComparison.OrdinalIgnoreCase));
    }

    private static void ReadBuildCustomizations(XElement root, ProjectInfo info)
    {
        foreach (var import in root.Elements().Where(e => e.Name.LocalName == "Import"))
        {
            var project = import.Attribute("Project")?.Value ?? "";
            if (project.Length == 0 || IsStandardImport(project)) continue;
            info.CustomImports.Add(StripNamespace(import).ToString());
        }

        foreach (var target in root.Elements().Where(e => e.Name.LocalName == "Target"))
        {
            var targetName = target.Attribute("Name")?.Value ?? "";
            var xml = StripNamespace(target).ToString();
            if (targetName == "EnsureNuGetPackageBuildImports") continue;
            if (xml.Contains("AspNetCompiler") || xml.Contains("TransformXml") || targetName == "MvcBuildViews")
                info.DroppedTargets.Add(targetName);
            else
                info.CustomTargets.Add(xml);
        }

        info.PreBuildEvent = Prop(root, "PreBuildEvent");
        info.PostBuildEvent = Prop(root, "PostBuildEvent");
    }

    private static bool IsStandardImport(string project)
    {
        string[] markers =
        [
            "$(MSBuildExtensionsPath", "$(MSBuildToolsPath)", "$(MSBuildBinPath)", "$(VSToolsPath)", "Microsoft.Common.props",
            "Microsoft.CSharp.targets", "\\packages\\", "/packages/", "Microsoft.WebApplication.targets", "Microsoft.TestTools",
            "NuGet.targets", ".nuget\\", "Microsoft.TypeScript", "$(NuGetPackageRoot)"
        ];
        return markers.Any(m => project.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static void AddGlobbedFiles(ProjectInfo info, string dir)
    {
        foreach (var file in EnumerateProjectDirectory(dir))
        {
            if (info.Items.Any(i => i.FullPath.Equals(file, StringComparison.OrdinalIgnoreCase))) continue;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var type = ext switch { ".cs" => "Compile", ".resx" => "EmbeddedResource", _ => "None" };
            info.Items.Add(new ProjectItem { ItemType = type, FullPath = file });
        }
    }

    public static IEnumerable<string> EnumerateProjectDirectory(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var dirName = Path.GetFileName(sub);
            if (SkippedDirectories.Contains(dirName) || File.Exists(Path.Combine(sub, WorkspaceLoader.OutputMarkerFile))) continue;
            if (Directory.EnumerateFiles(sub, "*.csproj").Any()) continue;
            foreach (var file in EnumerateProjectDirectory(sub))
                yield return file;
        }
    }

    private static void DetectKind(XElement root, ProjectInfo info, string dir)
    {
        var guids = (Prop(root, "ProjectTypeGuids") ?? "").ToLowerInvariant();
        var refs = info.FrameworkReferences
            .Concat(info.BinaryReferences.Select(b => b.Name))
            .Concat(info.Packages.Select(p => p.Id))
            .Select(r => r.ToLowerInvariant())
            .ToHashSet();

        bool Has(params string[] names) => names.Any(refs.Contains);

        info.HasMvc = Has("system.web.mvc", "microsoft.aspnet.mvc");
        info.HasWebApi = Has("system.web.http", "microsoft.aspnet.webapi.core", "microsoft.aspnet.webapi");
        info.HasWebForms = info.Items.Any(i => Path.GetExtension(i.FullPath).ToLowerInvariant() is ".aspx" or ".ascx" or ".master");
        info.UsesWinForms = Has("system.windows.forms");
        info.UsesWpf = Has("presentationframework") || guids.Contains("60dc8134-eba5-43b8-bcc9-bb4bc16c2548");

        if (Has("microsoft.visualstudio.qualitytools.unittestframework", "mstest.testframework")) info.TestFramework = "MSTest";
        else if (Has("nunit", "nunit.framework")) info.TestFramework = "NUnit";
        else if (Has("xunit", "xunit.core", "xunit.assert")) info.TestFramework = "xUnit";

        var isWap = guids.Contains("349c5851-65df-11da-9384-00065b846f21");
        var hasWebConfig = FindFile(dir, "web.config") != null;
        var hasGlobalAsax = FindFile(dir, "Global.asax") != null;

        if (info.TestFramework != null || guids.Contains("3ac096d0-a1c2-e12c-1390-a8335801fdab"))
            info.Kind = ProjectKind.Test;
        else if (isWap || (hasWebConfig && (hasGlobalAsax || info.HasMvc || info.HasWebApi || info.HasWebForms)))
            info.Kind = ProjectKind.Web;
        else if (info.UsesWinForms || info.UsesWpf)
            info.Kind = ProjectKind.Desktop;
        else if (info.IsExecutable && Has("system.serviceprocess") && info.SourceFiles.Any(f => SafeRead(f.FullPath).Contains(": ServiceBase")))
            info.Kind = ProjectKind.WindowsService;
        else if (info.IsExecutable)
            info.Kind = ProjectKind.Console;
        else
            info.Kind = ProjectKind.ClassLibrary;
    }

    private static IEnumerable<string> Expand(string dir, string include)
    {
        if (!include.Contains('*'))
        {
            yield return Path.GetFullPath(Path.Combine(dir, include));
            yield break;
        }

        var recursive = include.Contains("**");
        var prefix = include[..include.IndexOf('*')];
        var baseDir = prefix.Length == 0 ? dir : Path.GetDirectoryName(Path.GetFullPath(Path.Combine(dir, prefix))) ?? dir;
        var pattern = Path.GetFileName(include);
        if (!Directory.Exists(baseDir)) yield break;
        foreach (var f in Directory.EnumerateFiles(baseDir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
            yield return Path.GetFullPath(f);
    }

    public static string? FindFile(string dir, string fileName) =>
        Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir).FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            : null;

    private static string Normalize(string path) => path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static string? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private static XElement StripNamespace(XElement element)
    {
        var copy = new XElement(element.Name.LocalName,
            element.Attributes().Where(a => !a.IsNamespaceDeclaration),
            element.Nodes().Select(n => n is XElement child ? StripNamespace(child) : n));
        return copy;
    }

    private static string SafeRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch (IOException) { return ""; }
    }
}
