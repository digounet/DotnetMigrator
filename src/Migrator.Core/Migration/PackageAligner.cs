using Migrator.Core.Models;
using Migrator.Core.NuGet;
using NuGet.Versioning;

namespace Migrator.Core.Migration;

public sealed record MigratedProject(ProjectResult Result, ProjectFileSpec? Spec, OutputPlan Plan, Analysis.ApplicationProfile Profile);

/// <summary>Raises direct package versions so that no project downgrades a dependency (NU1605).</summary>
public static class PackageAligner
{
    public static IReadOnlyList<MigratedProject> TopologicalOrder(IReadOnlyList<MigratedProject> projects)
    {
        var byPath = projects.ToDictionary(p => p.Result.Project.ProjectPath, StringComparer.OrdinalIgnoreCase);
        var ordered = new List<MigratedProject>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(MigratedProject project)
        {
            if (!visited.Add(project.Result.Project.ProjectPath)) return;
            foreach (var reference in project.Result.Project.ProjectReferences)
                if (byPath.TryGetValue(reference, out var dependency)) Visit(dependency);
            ordered.Add(project);
        }

        foreach (var project in projects) Visit(project);
        return ordered;
    }

    public static IEnumerable<MigratedProject> Closure(MigratedProject project, IReadOnlyList<MigratedProject> all)
    {
        var byPath = all.ToDictionary(p => p.Result.Project.ProjectPath, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>(project.Result.Project.ProjectReferences);
        while (stack.Count > 0)
        {
            var path = stack.Pop();
            if (!seen.Add(path) || !byPath.TryGetValue(path, out var dependency)) continue;
            yield return dependency;
            foreach (var next in dependency.Result.Project.ProjectReferences) stack.Push(next);
        }
    }

    public static async Task AlignAsync(IReadOnlyList<MigratedProject> ordered, NuGetClient nuget)
    {
        foreach (var project in ordered.Where(p => p.Spec != null))
        {
            for (var pass = 0; pass < 3; pass++)
            {
                var floors = new Dictionary<string, (NuGetVersion Version, string Source)>(StringComparer.OrdinalIgnoreCase);
                void Floor(string id, NuGetVersion version, string source)
                {
                    if (!floors.TryGetValue(id, out var current) || version > current.Version) floors[id] = (version, source);
                }

                foreach (var source in Closure(project, ordered).Prepend(project))
                {
                    var via = source == project ? "" : $" (via {source.Result.Project.Name})";
                    foreach (var package in source.Spec?.Packages ?? [])
                    {
                        if (!NuGetVersion.TryParse(package.Version, out var version)) continue;
                        if (source != project) Floor(package.Id, version, $"projeto referenciado {source.Result.Project.Name}");
                        foreach (var (dependency, min) in await nuget.GetDependenciesAsync(package.Id, version))
                            Floor(dependency, min, $"{package.Id} {package.Version}{via}");
                    }
                }

                var changed = false;
                var packages = project.Spec!.Packages;
                for (var i = 0; i < packages.Count; i++)
                {
                    var package = packages[i];
                    if (!floors.TryGetValue(package.Id, out var floor) || !NuGetVersion.TryParse(package.Version, out var version) || version >= floor.Version)
                        continue;
                    packages[i] = package with { Version = floor.Version.ToNormalizedString() };
                    changed = true;
                    project.Result.Inventory.Add(new InventoryItem
                    {
                        Project = project.Result.Project.Name, Severity = InventorySeverity.Info, Category = InventoryCategory.Package,
                        RuleId = "PKG-ALIGNED", AutoMigrated = true,
                        Title = $"Versão ajustada: {package.Id} {package.Version} → {packages[i].Version}",
                        Description = $"Exigido por {floor.Source}; sem o ajuste o restore falharia com NU1605 (downgrade de pacote).",
                        Suggestion = "Nenhuma ação necessária."
                    });
                }
                if (!changed) break;
            }
        }
    }
}
