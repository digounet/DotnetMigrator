using System.Text;
using System.Xml;
using System.Xml.Linq;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

public sealed class ProjectFileSpec
{
    public string Sdk { get; set; } = "Microsoft.NET.Sdk";
    public List<(string Name, string Value)> Properties { get; } = [];
    public List<string> FrameworkReferences { get; } = [];
    public List<PackageReferenceOut> Packages { get; } = [];
    public List<string> ProjectReferences { get; } = [];
    public List<(string Name, string HintPath)> References { get; } = [];
    public List<XElement> Items { get; } = [];
    public List<string> RawElements { get; } = [];
    public string? PreBuildCommand { get; set; }
    public string? PostBuildCommand { get; set; }
}

public static class ProjectFileWriter
{
    public static string Write(ProjectFileSpec spec)
    {
        var project = new XElement("Project", new XAttribute("Sdk", spec.Sdk));

        project.Add(new XElement("PropertyGroup", spec.Properties.Select(p => new XElement(p.Name, p.Value))));

        AddGroup(project, spec.FrameworkReferences.Select(f => new XElement("FrameworkReference", new XAttribute("Include", f))));
        foreach (var item in spec.Items) NormalizePaths(item);
        AddGroup(project, spec.Items);
        AddGroup(project, spec.Packages.Select(p =>
        {
            var element = new XElement("PackageReference", new XAttribute("Include", p.Id), new XAttribute("Version", p.Version));
            if (p.PrivateAssetsAll) element.Add(new XAttribute("PrivateAssets", "all"));
            return element;
        }));
        AddGroup(project, spec.ProjectReferences.Select(r => new XElement("ProjectReference", new XAttribute("Include", MsBuildPath(r)))));
        AddGroup(project, spec.References.Select(r => new XElement("Reference", new XAttribute("Include", r.Name), new XElement("HintPath", MsBuildPath(r.HintPath)))));

        foreach (var raw in spec.RawElements)
        {
            var element = XElement.Parse(raw);
            if (element.Name.LocalName is "COMReference") AddGroup(project, [element]);
            else project.Add(element);
        }

        if (spec.PreBuildCommand != null)
            project.Add(new XElement("Target", new XAttribute("Name", "PreBuild"), new XAttribute("BeforeTargets", "PreBuildEvent"),
                new XElement("Exec", new XAttribute("Command", spec.PreBuildCommand))));
        if (spec.PostBuildCommand != null)
            project.Add(new XElement("Target", new XAttribute("Name", "PostBuild"), new XAttribute("AfterTargets", "PostBuildEvent"),
                new XElement("Exec", new XAttribute("Command", spec.PostBuildCommand))));

        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "  ", NewLineChars = "\n", Encoding = new UTF8Encoding(false) };
        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
            new XDocument(project).Save(writer);

        return (AddBlankLinesBetweenGroups(sb.ToString()) + "\n").Replace("\n", Environment.NewLine);
    }

    /// <summary>MSBuild accepts both separators, but the generated project should look the same whether the tool ran on Windows or Linux/macOS.</summary>
    private static string MsBuildPath(string path) => path.Replace('/', '\\');

    private static void NormalizePaths(XElement item)
    {
        foreach (var name in new[] { "Include", "Update", "Remove", "Link", "DependentUpon", "LastGenOutput" })
            if (item.Attribute(name) is { } attribute && !attribute.Value.Contains("://", StringComparison.Ordinal))
                attribute.Value = MsBuildPath(attribute.Value);
        foreach (var child in item.Elements().Where(e => e.Name.LocalName is "Link" or "DependentUpon" or "LastGenOutput" or "HintPath"))
            child.Value = MsBuildPath(child.Value);
    }

    private static void AddGroup(XElement project, IEnumerable<XElement> items)
    {
        var list = items.ToList();
        if (list.Count > 0) project.Add(new XElement("ItemGroup", list));
    }

    private static string AddBlankLinesBetweenGroups(string xml) =>
        xml.Replace("\n  <ItemGroup>", "\n\n  <ItemGroup>")
           .Replace("\n  <Target ", "\n\n  <Target ")
           .Replace("\n  <Import ", "\n\n  <Import ")
           .Replace("\n</Project>", "\n\n</Project>");
}
