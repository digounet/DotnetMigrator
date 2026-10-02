namespace Migrator.Core.Models;

public enum ProjectKind { ClassLibrary, Console, WindowsService, Web, Desktop, Test }

public sealed class ProjectInfo
{
    public required string ProjectPath { get; init; }
    public required string Name { get; init; }
    public string ProjectDir => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>"C#" or "VB". VB.NET projects are profiled and reported but not converted.</summary>
    public string Language { get; set; } = "C#";
    public bool IsVisualBasic => Language == "VB";
    public bool IsSdkStyle { get; set; }
    public string TargetFramework { get; set; } = string.Empty;
    public bool IsAlreadyModern { get; set; }
    public string OutputType { get; set; } = "Library";
    public string AssemblyName { get; set; } = string.Empty;
    public string RootNamespace { get; set; } = string.Empty;
    public ProjectKind Kind { get; set; }

    public bool HasMvc { get; set; }
    public bool HasWebApi { get; set; }
    public bool HasWebForms { get; set; }
    public bool UsesWinForms { get; set; }
    public bool UsesWpf { get; set; }
    public string? TestFramework { get; set; }

    public string? IisUrl { get; set; }
    public string? IisSslPort { get; set; }

    public List<PackageInfo> Packages { get; } = [];
    public List<string> FrameworkReferences { get; } = [];
    public List<BinaryReference> BinaryReferences { get; } = [];
    public List<string> ProjectReferences { get; } = [];

    /// <summary>All files that belong to the project (absolute paths), with their original item type.</summary>
    public List<ProjectItem> Items { get; } = [];

    public List<string> ComReferences { get; } = [];
    public List<string> CustomImports { get; } = [];
    public List<string> CustomTargets { get; } = [];
    public List<string> DroppedTargets { get; } = [];
    public string? PreBuildEvent { get; set; }
    public string? PostBuildEvent { get; set; }
    public Dictionary<string, string> CarriedProperties { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> DefineConstants { get; } = [];

    public string? ConfigFilePath { get; set; }
    public List<string> ConfigTransformFiles { get; } = [];

    public string SourceExtension => IsVisualBasic ? ".vb" : ".cs";

    public IEnumerable<ProjectItem> SourceFiles =>
        Items.Where(i => i.ItemType == "Compile" && i.FullPath.EndsWith(SourceExtension, StringComparison.OrdinalIgnoreCase));

    public bool IsExecutable =>
        OutputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
        OutputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase);
}

public sealed record BinaryReference(string Name, string HintPath, bool Exists);

public sealed class ProjectItem
{
    public required string ItemType { get; init; }
    public required string FullPath { get; init; }
    public string? Link { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsInside(string directory) =>
        FullPath.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
