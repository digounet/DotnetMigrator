using System.Text;
using Migrator.Core.Analysis;
using Migrator.Core.Models;

namespace Migrator.Core.Llm;

/// <summary>Turns the structured architecture proposal and profiles into an executive narrative (pt-BR) written by the model.</summary>
public static class LlmNarrator
{
    public static async Task NarrateAsync(LlmSession session, SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (result.Architecture == null) return;
        progress?.Report("LLM: escrevendo o resumo executivo da arquitetura...");
        var answer = await session.TryCompleteAsync(LlmPrompts.NarrativeSystem, BuildDossier(result, profiles), cancellationToken);
        if (string.IsNullOrWhiteSpace(answer)) return;
        result.Architecture.ExecutiveSummary = answer.Trim();
        result.Architecture.ExecutiveSummaryModel = session.Assistant.Name;
    }

    public static string BuildDossier(SolutionResult result, IReadOnlyList<(ProjectResult Result, ApplicationProfile Profile)> profiles)
    {
        var a = result.Architecture!;
        var sb = new StringBuilder();
        sb.AppendLine($"# Dossiê: {result.SolutionName}");
        sb.AppendLine($"Projetos: {result.Projects.Count}. Resumo gerado por regras: {a.Summary}");
        sb.AppendLine();
        foreach (var (pr, profile) in profiles)
        {
            sb.AppendLine($"## {pr.Project.Name} — {pr.Project.Kind}, origem {pr.Project.TargetFramework}");
            if (pr.Hosting != null)
            {
                sb.AppendLine($"Hospedagem recomendada: {pr.Hosting.Primary.Display()}");
                foreach (var r in pr.Hosting.Rationale) sb.AppendLine($"- motivo: {r}");
                foreach (var p in pr.Hosting.Prerequisites) sb.AppendLine($"- pré-requisito: {p}");
                if (pr.Hosting.HardWindowsDependencies.Count > 0) sb.AppendLine($"- dependências Windows duras: {string.Join("; ", pr.Hosting.HardWindowsDependencies)}");
                if (pr.Hosting.SoftWindowsDependencies.Count > 0) sb.AppendLine($"- dependências Windows substituíveis: {string.Join("; ", pr.Hosting.SoftWindowsDependencies)}");
            }
            if (profile.Databases.Count > 0) sb.AppendLine("Bancos: " + string.Join("; ", profile.Databases.Select(d => $"{d.Provider} {d.Server}/{d.Database}{(d.IntegratedSecurity ? " (Integrated Security)" : "")}")));
            if (profile.ExternalEndpoints.Count > 0) sb.AppendLine("Endpoints externos: " + string.Join(", ", profile.ExternalEndpoints));
            var signals = profile.Signals.Values.OrderByDescending(s => s.Count).Take(25)
                .Select(s => $"{s.Signal} ×{s.Count}{(s.Details.Count > 0 ? $" ({string.Join(", ", s.Details.Take(3))})" : "")}");
            sb.AppendLine("Sinais: " + string.Join("; ", signals));
            if (pr.Project.Kind == ProjectKind.Web) sb.AppendLine($"Controllers MVC: {profile.MvcControllerCount}; controllers API: {profile.ApiControllerCount}; views: {profile.ViewCount}; arquivos estáticos: {profile.StaticFileCount}");
            sb.AppendLine($"Inventário: {pr.Breaking.Count()} bloqueantes, {pr.Warnings.Count()} atenção, {pr.Automatic.Count()} automáticos" + (pr.Build != null ? $"; build: {(pr.Build.Succeeded ? "ok" : pr.Build.BlockedBy != null ? "bloqueado por " + pr.Build.BlockedBy : pr.Build.Errors + " erro(s)")}" : ""));
            foreach (var m in pr.Modernizations.Where(m => m.Impact == Impact.High).Take(8))
                sb.AppendLine($"- modernização (impacto alto, {m.Kind.Display()}): {m.Title} — {m.Why}");
            sb.AppendLine();
        }
        sb.AppendLine("## Serviços AWS propostos");
        foreach (var c in a.Components) sb.AppendLine($"- {c.Service} ({(c.Required ? "obrigatório" : "recomendado")}): {c.Role}; substitui {c.Replaces}; usado por {string.Join(", ", c.UsedBy)}");
        sb.AppendLine();
        sb.AppendLine("## Riscos identificados por regras");
        foreach (var r in a.Risks) sb.AppendLine($"- {r}");
        return sb.ToString();
    }
}
