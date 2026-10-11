using Migrator.Core.Models;

namespace Migrator.Core.Migration;

public static partial class ProjectMigrator
{
    /// <summary>
    /// Platform Terraform (<c>--iac terraform-platform</c>): console projects become workers (generated <c>BackgroundService</c> + /health).
    /// Also records what the IaC has to know about the project: environment variables read by the code and the names of its environment config files.
    /// Returns the (possibly adjusted) project file text.
    /// </summary>
    private static string ApplyPlatformWorker(ProjectInfo project, ProjectMigrationContext ctx, ProjectResult result, OutputPlan plan, IReadOnlyList<(string Relative, string Text)> code, string csproj)
    {
        result.EnvironmentVariablesRead.AddRange(PlatformWorkerRewriter.EnvironmentVariablesRead(code.Select(c => c.Text)));
        var files = AppSettingsAnalyzer.Load(project);
        if (files != null)
            foreach (var (environment, path) in files.EnvironmentFiles)
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                if (stem.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase)) stem = stem["appsettings.".Length..];
                if (environment is "Development" or "Staging" or "Production") result.ConfigEnvironmentNames[environment] = stem;
            }

        if (project.Kind != ProjectKind.Console) return csproj;
        // The literal externalizer may already have rewritten the entry file: convert what the plan holds, not the original text.
        var sources = code.Select(c =>
        {
            var planned = plan.Writes.LastOrDefault(w => string.Equals(w.RelativePath, Path.Combine(result.RelativeDir, c.Relative), StringComparison.OrdinalIgnoreCase));
            return (c.Relative, planned.Content ?? c.Text);
        }).ToList();
        var converted = PlatformWorkerRewriter.Convert(sources, project.RootNamespace ?? project.Name);
        if (converted == null)
        {
            result.Inventory.Add(Item(project, InventorySeverity.Warning, InventoryCategory.Code, "PLAT-WORKER-MANUAL",
                "Console não convertido em worker: ponto de entrada não reconhecido",
                "A plataforma hospeda automações como worker (BackgroundService com /health); não encontrei um static Main(string[]) simples (top-level statements ou nenhum Main).",
                "Converta o ponto de entrada em um BackgroundService e exponha /health antes do primeiro deploy."));
            return csproj;
        }
        plan.Write(Path.Combine(result.RelativeDir, converted.EntryFile), converted.EntryText);
        plan.Write(Path.Combine(result.RelativeDir, PlatformWorkerRewriter.WorkerFile), converted.WorkerText);
        result.ConvertedToWorker = true;
        result.Inventory.Add(Item(project, InventorySeverity.Info, InventoryCategory.Code, "PLAT-WORKER",
            "Console convertido em worker de longa duração (BackgroundService + /health)",
            $"{converted.EntryFile}: {converted.Reason}; Process.Kill()/Environment.Exit() viraram return (encerram o ciclo, não o processo). {PlatformWorkerRewriter.WorkerFile}: novo Main hospeda o worker e o health check; cada ciclo executa RunOnce e espera Worker:IntervalMinutes (padrão 15).",
            "Confira se uma execução repetida na mesma instância é segura (estado estático, conexões, arquivos temporários) e ajuste Worker:IntervalMinutes.", PlatformWorkerRewriter.WorkerFile, auto: true));
        return PlatformWorkerRewriter.AddAspNetCoreFramework(csproj);
    }
}
