using Migrator.Core.Data;
using Migrator.Core.Models;
using Migrator.Core.NuGet;
using NuGet.Versioning;

namespace Migrator.Core.Migration;

public sealed record PackageRequirement(string Id, VersionPolicy Policy, string Reason);

public sealed class PackagePlan
{
    public List<PackageReferenceOut> References { get; } = [];
    public List<InventoryItem> Items { get; } = [];
}

public sealed class PackagePlanner(NuGetClient nuget)
{
    private enum Outcome { Kept, Patched, Upgraded, Pinned, Unchecked, NotFound, NoCompatible, Unknown }

    private sealed record Resolution(string Version, Outcome Outcome, string Detail = "");

    public async Task<PackagePlan> PlanAsync(ProjectInfo project, IReadOnlyList<PackageRequirement> required)
    {
        var plan = new PackagePlan();
        var results = await Task.WhenAll(project.Packages.Select(p => PlanExistingAsync(project, p)));
        foreach (var (refs, items) in results)
        {
            plan.References.AddRange(refs);
            plan.Items.AddRange(items);
        }

        foreach (var requirement in required)
        {
            if (plan.References.Any(r => r.Id.Equals(requirement.Id, StringComparison.OrdinalIgnoreCase))) continue;
            var resolution = await ResolveAsync(requirement.Id, null, requirement.Policy);
            plan.References.Add(new PackageReferenceOut(requirement.Id, resolution.Version));
            plan.Items.Add(Item(project, InventorySeverity.Info, "PKG-ADDED", $"Pacote adicionado: {requirement.Id} {resolution.Version}",
                requirement.Reason, "Nenhuma ação necessária.", auto: true));
        }

        var deduped = plan.References
            .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => NuGetVersion.TryParse(r.Version, out var v) ? v : new NuGetVersion(0, 0, 0)).First())
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        plan.References.Clear();
        plan.References.AddRange(deduped);
        return plan;
    }

    private async Task<(List<PackageReferenceOut> Refs, List<InventoryItem> Items)> PlanExistingAsync(ProjectInfo project, PackageInfo package)
    {
        var refs = new List<PackageReferenceOut>();
        var items = new List<InventoryItem>();
        var rule = PackageRules.Find(package.Id);
        var label = $"{package.Id} {package.Version}".Trim();

        switch (rule?.Action)
        {
            case PackageAction.Remove:
                items.Add(Item(project, rule.Severity ?? InventorySeverity.Info, "PKG-REMOVED", $"Pacote removido: {label}", rule.Guidance,
                    "Nenhuma ação necessária.", auto: (rule.Severity ?? InventorySeverity.Info) == InventorySeverity.Info));
                break;

            case PackageAction.Manual:
                items.Add(Item(project, InventorySeverity.Breaking, "PKG-MANUAL", $"Pacote sem equivalente direto: {label}",
                    "O pacote foi removido do projeto migrado; o código que depende dele precisa ser reescrito.", rule.Guidance));
                break;

            case PackageAction.Replace:
                var targets = new List<string>();
                foreach (var target in rule.Replacements!)
                {
                    var resolved = await ResolveAsync(target.Id, null, target.Version);
                    refs.Add(new PackageReferenceOut(target.Id, resolved.Version, package.DevelopmentDependency));
                    targets.Add($"{target.Id} {resolved.Version}");
                }
                items.Add(Item(project, rule.Severity ?? InventorySeverity.Warning, "PKG-REPLACED", $"Pacote substituído: {label} → {string.Join(" + ", targets)}",
                    "A referência foi trocada automaticamente; a API do pacote novo é diferente e o código precisa ser revisado.", rule.Guidance));
                break;

            default:
                var policy = rule?.Version ?? VersionPolicy.SameIfCompatible();
                var resolution = await ResolveAsync(package.Id, package.Version, policy);
                refs.Add(new PackageReferenceOut(package.Id, resolution.Version, package.DevelopmentDependency));
                items.Add(DescribeKeep(project, package, resolution, rule));
                break;
        }
        return (refs, items);
    }

    private static InventoryItem DescribeKeep(ProjectInfo project, PackageInfo package, Resolution resolution, PackageRule? rule)
    {
        var guidance = rule?.Guidance;
        var label = $"{package.Id} {package.Version}".Trim();
        var forcedWarning = rule?.Severity == InventorySeverity.Warning;

        return resolution.Outcome switch
        {
            Outcome.Kept => Item(project, forcedWarning ? InventorySeverity.Warning : InventorySeverity.Info, "PKG-KEPT",
                $"Pacote mantido: {label}", $"Versão compatível com .NET 10 ({resolution.Detail}).",
                guidance ?? "Nenhuma ação necessária.", auto: !forcedWarning),
            Outcome.Patched => Item(project, forcedWarning ? InventorySeverity.Warning : InventorySeverity.Info, "PKG-PATCHED",
                $"Pacote atualizado na mesma versão principal: {package.Id} {package.Version} → {resolution.Version}",
                $"A versão original já era compatível ({resolution.Detail}); foi atualizada para a última {NuGetVersion.Parse(resolution.Version).Major}.x, que traz correções de bugs e de segurança sem mudança de API pública esperada.",
                guidance ?? "Nenhuma ação necessária.", auto: !forcedWarning),
            Outcome.Pinned => Item(project, forcedWarning ? InventorySeverity.Warning : InventorySeverity.Info, "PKG-UPDATED",
                $"Pacote atualizado: {package.Id} {package.Version} → {resolution.Version}", "Versão alinhada ao .NET 10.",
                guidance ?? "Nenhuma ação necessária.", auto: !forcedWarning),
            Outcome.Upgraded => Item(project, InventorySeverity.Warning, "PKG-UPGRADED",
                $"Pacote atualizado: {package.Id} {package.Version} → {resolution.Version}",
                $"A versão original só tem binários para .NET Framework ({resolution.Detail}); a ferramenta escolheu a versão mais recente compatível.",
                (guidance != null ? guidance + " " : "") + "Revise as breaking changes entre as versões (release notes do pacote)."),
            Outcome.Unchecked => Item(project, InventorySeverity.Warning, "PKG-UNCHECKED",
                $"Compatibilidade não verificada: {label}", "Modo offline ou feed NuGet indisponível: a versão original foi mantida.",
                guidance ?? "Execute novamente sem --offline ou confira o build de verificação (NU1701 indica pacote só para .NET Framework)."),
            Outcome.NotFound => Item(project, InventorySeverity.Warning, "PKG-NOTFOUND",
                $"Pacote não encontrado no feed NuGet: {label}", "Provavelmente vem de outro feed (privado/corporativo) ou foi retirado do ar (unlisted).",
                "Verifique no feed interno se existe versão para netstandard2.0/net8+/net10; se só existir para .NET Framework, recompile a biblioteca para netstandard2.0 ou net10.0."),
            Outcome.NoCompatible => Item(project, InventorySeverity.Breaking, "PKG-INCOMPATIBLE",
                $"Sem versão compatível com .NET 10: {label}",
                $"Nenhuma versão publicada tem binários para .NET moderno ({resolution.Detail}). A referência foi mantida (restaura em modo de compatibilidade, com aviso NU1701) mas pode falhar em tempo de execução.",
                guidance ?? "Procure um pacote substituto, isole a funcionalidade num serviço .NET Framework ou peça ao fornecedor uma versão para .NET."),
            _ => Item(project, InventorySeverity.Warning, "PKG-UNKNOWN",
                $"Compatibilidade indeterminada: {label}", $"O pacote não declara frameworks de forma padronizada ({resolution.Detail}).",
                guidance ?? "Confira o build de verificação e teste a funcionalidade que usa o pacote.")
        };
    }

    private async Task<Resolution> ResolveAsync(string id, string? current, VersionPolicy policy)
    {
        NuGetVersion.TryParse(current, out var currentVersion);
        NuGetVersion.TryParse(policy.MaxExclusive, out var max);
        bool BelowMax(NuGetVersion v) => max == null || v < max;

        switch (policy.Kind)
        {
            case VersionPolicyKind.Fixed:
                return new Resolution(policy.Fallback, Outcome.Pinned);

            case VersionPolicyKind.DotNet or VersionPolicyKind.LatestMajor or VersionPolicyKind.Latest:
            {
                var fallback = policy.Fallback.Length > 0 ? policy.Fallback : current ?? "1.0.0";
                if (!nuget.IsOnline) return new Resolution(fallback, Outcome.Pinned);
                var versions = await nuget.GetVersionsAsync(id);
                if (versions.Status != LookupStatus.Found) return new Resolution(fallback, Outcome.Pinned);
                var pick = policy.Kind == VersionPolicyKind.Latest
                    ? versions.Latest(BelowMax)
                    : versions.Latest(v => v.Major == policy.Major && BelowMax(v));
                if (pick == null && policy.Kind == VersionPolicyKind.DotNet) pick = versions.Latest(BelowMax);
                return new Resolution(pick?.ToNormalizedString() ?? fallback, Outcome.Pinned);
            }

            default:
            {
                if (currentVersion == null)
                    return await ResolveAsync(id, null, VersionPolicy.Latest(current ?? "1.0.0", policy.MaxExclusive));
                if (!nuget.IsOnline) return new Resolution(current!, Outcome.Unchecked);

                var compat = await nuget.GetCompatibilityAsync(id, currentVersion);
                switch (compat.Status)
                {
                    case CompatStatus.Compatible:
                        var frameworks = string.Join(", ", compat.Frameworks.Where(TargetFrameworks.IsModern).Take(3));
                        var sameMajor = (await nuget.GetVersionsAsync(id)).Latest(v => v.Major == currentVersion.Major && v > currentVersion && BelowMax(v));
                        if (sameMajor != null && (await nuget.GetCompatibilityAsync(id, sameMajor)).Status == CompatStatus.Compatible)
                            return new Resolution(sameMajor.ToNormalizedString(), Outcome.Patched, frameworks);
                        return new Resolution(current!, Outcome.Kept, frameworks);
                    case CompatStatus.Unknown:
                        return new Resolution(current!, Outcome.Unknown, string.Join(", ", compat.Frameworks));
                    case CompatStatus.Unavailable:
                        return new Resolution(current!, Outcome.Unchecked);
                    case CompatStatus.NotFound:
                        var all = await nuget.GetVersionsAsync(id);
                        return all.Status == LookupStatus.NotFound
                            ? new Resolution(current!, Outcome.NotFound)
                            : new Resolution(current!, Outcome.Unknown, "versão original não publicada no feed NuGet");
                }

                var versions = await nuget.GetVersionsAsync(id);
                var latest = versions.Latest(BelowMax);
                if (latest == null || latest <= currentVersion)
                    return new Resolution(current!, Outcome.NoCompatible, string.Join(", ", compat.Frameworks));

                var latestCompat = await nuget.GetCompatibilityAsync(id, latest);
                return latestCompat.Status is CompatStatus.Compatible or CompatStatus.Unknown
                    ? new Resolution(latest.ToNormalizedString(), Outcome.Upgraded, string.Join(", ", compat.Frameworks))
                    : new Resolution(current!, Outcome.NoCompatible, string.Join(", ", compat.Frameworks));
            }
        }
    }

    private static InventoryItem Item(ProjectInfo project, InventorySeverity severity, string rule, string title, string description, string suggestion, bool auto = false) => new()
    {
        Project = project.Name, Severity = severity, Category = InventoryCategory.Package, RuleId = rule,
        Title = title, Description = description, Suggestion = suggestion, AutoMigrated = auto
    };
}
