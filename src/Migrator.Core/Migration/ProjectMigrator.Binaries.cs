using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Migration;

/// <summary>
/// Local DLLs (Reference with HintPath): what they use decides whether the project can run on .NET 10 and on Linux, so
/// the inspection feeds the architecture profile (hosting, MOD-WIN-* items), the packages of the migrated project and
/// the PRJ-DLL inventory item. Runs on every target: on lift-and-shift the result is informational (the DLL keeps
/// running on Windows) but tells what blocks the later modernization.
/// </summary>
public static partial class ProjectMigrator
{
    /// <summary>Inspects every existing local DLL and records the signals it implies in the profile (evidence "DLL X").</summary>
    private static Dictionary<string, AssemblyInspection> InspectBinaries(ProjectInfo project, ApplicationProfile profile)
    {
        var inspections = new Dictionary<string, AssemblyInspection>(StringComparer.OrdinalIgnoreCase);
        foreach (var binary in project.BinaryReferences.Where(b => b.Exists))
        {
            var inspection = AssemblyInspector.Inspect(binary.HintPath);
            inspections[binary.HintPath] = inspection;
            var location = Path.GetRelativePath(project.ProjectDir, binary.HintPath).Replace('\\', '/');
            foreach (var signal in inspection.Findings.Where(f => f.Signal != null).Select(f => f.Signal!.Value).Distinct())
                profile.Add(signal, location, $"DLL {binary.Name}");
        }
        return inspections;
    }

    /// <summary>Packages the migrated project needs because a DLL it references uses APIs that moved to NuGet packages.</summary>
    private static void ApplyBinaryFacts(ProjectInfo project, CodeFacts facts)
    {
        foreach (var binary in project.BinaryReferences.Where(b => b.Exists))
        {
            var inspection = AssemblyInspector.Inspect(binary.HintPath);
            foreach (var finding in inspection.Findings)
            {
                if (finding.Api.StartsWith("System.Configuration", StringComparison.Ordinal)) facts.UsesLegacyConfigurationApi = true;
                else if (finding.Api.StartsWith("System.ServiceModel", StringComparison.Ordinal) && finding.Kind == CompatibilityKind.Package) facts.UsesWcfClient = true;
                else if (finding.Api.StartsWith("System.Drawing", StringComparison.Ordinal)) facts.UsesSystemDrawing = true;
                else if (finding.Api.StartsWith("System.Diagnostics.EventLog", StringComparison.Ordinal)) facts.UsesEventLog = true;
                else if (finding.Api.StartsWith("System.Diagnostics.PerformanceCounter", StringComparison.Ordinal)) facts.UsesPerformanceCounter = true;
                else if (finding.Api.StartsWith("System.Runtime.Caching", StringComparison.Ordinal)) facts.UsesMemoryCache = true;
            }
        }
    }

    /// <summary>Inventory item text for a local DLL on the .NET 10 target: severity follows the worst finding.</summary>
    private static (InventorySeverity Severity, string Description, string Suggestion) DescribeBinary(AssemblyInspection inspection)
    {
        if (inspection.Flavor == AssemblyFlavor.NotManaged)
            return (InventorySeverity.Warning, "DLL nativa referenciada como assembly .NET.",
                "Copie a DLL nativa para a saída (<None Include=... CopyToOutputDirectory=PreserveNewest />) e acesse via P/Invoke.");

        var compiled = inspection.Flavor switch
        {
            AssemblyFlavor.Modern => $"Compilada para {inspection.TargetFramework ?? "netstandard/.NET"}",
            AssemblyFlavor.NetFramework => $"Compilada para {inspection.TargetFramework ?? ".NET Framework"}",
            _ => "Framework da DLL não identificado"
        };
        var removed = Summarize(inspection.Of(CompatibilityKind.Removed));
        var windows = Summarize(inspection.Of(CompatibilityKind.WindowsOnly));
        var risky = Summarize(inspection.Of(CompatibilityKind.Risky));
        var packages = Summarize(inspection.Of(CompatibilityKind.Package));

        var parts = new List<string>();
        if (removed.Length > 0) parts.Add("usa APIs que não existem no .NET 10: " + removed);
        if (windows.Length > 0) parts.Add("usa APIs que só funcionam em Windows: " + windows);
        if (risky.Length > 0) parts.Add("usa APIs que mudam de comportamento no .NET 10/Linux: " + risky);
        if (packages.Length > 0) parts.Add("precisa de pacotes no projeto migrado (adicionados): " + packages);
        if (parts.Count == 0)
            parts.Add(inspection.Flavor == AssemblyFlavor.Modern
                ? "compatível."
                : "nenhuma API removida ou exclusiva do Windows encontrada nos metadados: costuma carregar no .NET 10.");
        if (inspection.Dependencies.Count > 0) parts.Add($"DLLs dependentes inspecionadas na mesma pasta: {string.Join(", ", inspection.Dependencies)}");
        var description = compiled + "; " + string.Join("; ", parts);

        if (removed.Length > 0)
            return (InventorySeverity.Breaking, description,
                "A DLL lança FileNotFoundException/TypeLoadException quando esse código executar. Recompile-a a partir do fonte para netstandard2.0 (o compilador aponta cada ponto) ou obtenha uma versão para .NET moderno; sem fonte, isole o uso atrás de uma interface e reimplemente.");
        if (windows.Length > 0)
            return (InventorySeverity.Warning, description,
                "Em container Linux esses pontos lançam PlatformNotSupportedException: substitua-os (itens MOD-WIN-* da modernização) ou hospede em Windows (container Windows no ECS ou EC2). Pacotes de compatibilidade foram adicionados ao projeto quando existem.");
        if (risky.Length > 0)
            return (InventorySeverity.Warning, description,
                "Teste os fluxos que usam a DLL no .NET 10 e em Linux; veja os itens MOD-CS-* correspondentes.");
        if (inspection.Flavor == AssemblyFlavor.Unknown)
            return (InventorySeverity.Warning, description, "Teste os fluxos que usam a DLL.");
        return (InventorySeverity.Info, description,
            inspection.Flavor == AssemblyFlavor.Modern
                ? "Considere publicar a DLL como pacote NuGet interno."
                : "Teste os fluxos que usam a DLL; o ideal continua sendo recompilá-la para netstandard2.0 ou publicá-la como pacote NuGet interno.");
    }

    /// <summary>Informational PRJ-DLL items for targets that keep the code on .NET Framework (lift-and-shift, VB.NET): what each DLL would block later.</summary>
    private static void ReportBinaryCompatibility(ProjectInfo project, IReadOnlyDictionary<string, AssemblyInspection> inspections, List<InventoryItem> items,
        string context = "Nada muda no destino .NET Framework; é o que impede esta DLL de rodar em .NET 10/Linux depois.")
    {
        foreach (var binary in project.BinaryReferences.Where(b => b.Exists && inspections.ContainsKey(b.HintPath)))
        {
            var inspection = inspections[binary.HintPath];
            var relevant = inspection.Findings.Where(f => f.Kind != CompatibilityKind.Package).ToList();
            if (relevant.Count == 0) continue;
            var (_, description, _) = DescribeBinary(inspection);
            items.Add(Item(project, InventorySeverity.Info, InventoryCategory.ProjectFile, "PRJ-DLL",
                $"DLL local {binary.Name}: {Summarize(relevant.Take(3))}{(relevant.Count > 3 ? "..." : "")}",
                description.TrimEnd('.') + ". " + context,
                "Para a modernização, recompile a DLL para netstandard2.0 a partir do fonte ou substitua-a; a hospedagem proposta já considera essas dependências.",
                Path.GetRelativePath(project.ProjectDir, binary.HintPath)));
        }
    }

    /// <summary>"Label (api1, api2) via chain" per distinct label, in finding order.</summary>
    private static string Summarize(IEnumerable<CompatibilityFinding> findings)
    {
        var groups = findings.GroupBy(f => f.Label, StringComparer.OrdinalIgnoreCase).Select(g =>
        {
            var apis = g.Select(f => f.Api).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var text = g.Key;
            if (apis.Count > 1) text += $" ({string.Join(", ", apis.Take(4))}{(apis.Count > 4 ? ", ..." : "")})";
            var via = g.Select(f => f.Via).FirstOrDefault(v => v != null);
            if (via != null && g.All(f => f.Via != null)) text += $" via {via}";
            return text;
        });
        return string.Join("; ", groups);
    }
}
