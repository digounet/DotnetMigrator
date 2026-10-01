using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Migrator.Core.Analysis;

public sealed record Workspace(
    string RootDir,
    string Name,
    string? SolutionFile,
    IReadOnlyList<string> Projects,
    IReadOnlyList<(string Path, string Reason)> Skipped);

public static partial class WorkspaceLoader
{
    public const string OutputMarkerFile = ".migrator-output";

    private static readonly HashSet<string> IgnoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", ".git", ".vs", ".idea", "TestResults"
    };

    public static Workspace Load(string inputPath)
    {
        var full = Path.GetFullPath(inputPath);

        if (Directory.Exists(full))
            return FromDirectory(full);

        if (!File.Exists(full))
            throw new FileNotFoundException($"Caminho não encontrado: {inputPath}");

        var ext = Path.GetExtension(full).ToLowerInvariant();
        return ext switch
        {
            ".sln" => FromSln(full),
            ".slnx" => FromSlnx(full),
            ".csproj" => new Workspace(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full), null, [full], []),
            ".vbproj" or ".fsproj" => throw new NotSupportedException("Apenas projetos C# (.csproj) são suportados."),
            _ => throw new NotSupportedException($"Tipo de entrada não suportado: {ext}. Informe .sln, .slnx, .csproj ou um diretório.")
        };
    }

    private static Workspace FromSln(string slnPath)
    {
        var root = Path.GetDirectoryName(slnPath)!;
        var projects = new List<string>();
        var skipped = new List<(string, string)>();

        foreach (var line in File.ReadLines(slnPath))
        {
            var m = SlnProjectLine().Match(line);
            if (!m.Success) continue;
            var relative = m.Groups["path"].Value;
            if (m.Groups["type"].Value.Equals("2150E333-8FDC-42A3-9474-1A3956D46DE8", StringComparison.OrdinalIgnoreCase))
                continue;
            if (relative.Contains("://") || m.Groups["type"].Value.Equals("E24C65DC-7377-472B-9ABA-BC803B73C61A", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add((relative, "Web Site (sem arquivo de projeto): converta para Web Application antes de migrar."));
                continue;
            }
            Classify(Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar))), projects, skipped, relative);
        }

        return new Workspace(root, Path.GetFileNameWithoutExtension(slnPath), slnPath, projects.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), skipped);
    }

    private static Workspace FromSlnx(string slnxPath)
    {
        var root = Path.GetDirectoryName(slnxPath)!;
        var projects = new List<string>();
        var skipped = new List<(string, string)>();

        foreach (var p in XDocument.Load(slnxPath).Descendants().Where(e => e.Name.LocalName == "Project"))
        {
            var relative = p.Attribute("Path")?.Value;
            if (string.IsNullOrWhiteSpace(relative)) continue;
            Classify(Path.GetFullPath(Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar))), projects, skipped, relative);
        }

        return new Workspace(root, Path.GetFileNameWithoutExtension(slnxPath), slnxPath, projects, skipped);
    }

    private static Workspace FromDirectory(string dir)
    {
        if (File.Exists(Path.Combine(dir, OutputMarkerFile)))
            throw new InvalidOperationException("O diretório informado é uma saída gerada pelo Migrator.");

        var projects = new List<string>();
        var skipped = new List<(string, string)>();
        foreach (var file in EnumerateProjectFiles(dir))
            Classify(file, projects, skipped, Path.GetRelativePath(dir, file));

        var name = new DirectoryInfo(dir).Name;
        return new Workspace(dir, name, null, projects, skipped);
    }

    private static IEnumerable<string> EnumerateProjectFiles(string dir)
    {
        var pending = new Stack<string>();
        pending.Push(dir);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (File.Exists(Path.Combine(current, OutputMarkerFile))) continue;

            foreach (var file in Directory.EnumerateFiles(current, "*.*proj"))
                yield return file;

            foreach (var sub in Directory.EnumerateDirectories(current))
                if (!IgnoredDirs.Contains(Path.GetFileName(sub)))
                    pending.Push(sub);
        }
    }

    private static void Classify(string fullPath, List<string> projects, List<(string, string)> skipped, string display)
    {
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        if (ext == ".csproj")
        {
            if (File.Exists(fullPath)) projects.Add(fullPath);
            else skipped.Add((display, "Arquivo de projeto referenciado na solução não existe."));
            return;
        }

        var reason = ext switch
        {
            ".vbproj" => "Projeto VB.NET: não suportado pela ferramenta. Use o .NET Upgrade Assistant ou converta manualmente.",
            ".fsproj" => "Projeto F#: não suportado pela ferramenta.",
            ".sqlproj" => "Projeto de banco de dados (SSDT): permanece como está; compile com o Visual Studio/MSBuild.",
            ".wixproj" => "Projeto de instalador WiX: revise manualmente para publicar os binários .NET 10.",
            ".vcxproj" => "Projeto C++: fora do escopo.",
            ".shproj" => "Shared Project: os arquivos são incluídos pelos projetos que o importam.",
            ".njsproj" or ".esproj" => "Projeto JavaScript: fora do escopo.",
            "" => "Web Site (sem arquivo de projeto): converta para Web Application antes de migrar.",
            _ => $"Tipo de projeto '{ext}' não suportado."
        };
        skipped.Add((display, reason));
    }

    [GeneratedRegex(@"^Project\(""\{(?<type>[^}]+)\}""\)\s*=\s*""(?<name>[^""]+)"",\s*""(?<path>[^""]+)""")]
    private static partial Regex SlnProjectLine();
}
