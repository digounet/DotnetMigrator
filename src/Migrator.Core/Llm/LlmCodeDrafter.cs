using System.Text;
using Migrator.Core.Models;

namespace Migrator.Core.Llm;

/// <summary>
/// For legacy constructs the deterministic migration cannot translate (IHttpModule/IHttpHandler, HttpApplication,
/// ServiceBase, WCF ServiceHost, custom System.Web filters) asks the model for an ASP.NET Core / .NET 10 draft and
/// saves it next to the file as <c>&lt;Name&gt;.Migrator.cs.txt</c>. Drafts never take part in the build.
/// </summary>
public sealed class LlmCodeDrafter(LlmSession session, LlmOptions options, string outputDir)
{
    /// <summary>Inventory rules whose presence in a file makes it a candidate for a draft.</summary>
    public static readonly IReadOnlySet<string> TriggerRules = new HashSet<string>(StringComparer.Ordinal)
    {
        "WEB016", // IHttpModule / IHttpHandler
        "WEB017", // HttpApplication
        "WEB018", // custom System.Web.Mvc / Http filters
        "NET006", // WCF ServiceHost
        "NET022"  // ServiceBase.Run
    };

    public int Drafts { get; private set; }

    public async Task DraftAsync(SolutionResult result, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var candidates = result.Projects
            .SelectMany(p => p.Inventory
                .Where(i => i.FilePath != null && TriggerRules.Contains(i.RuleId) && i.FilePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .GroupBy(i => i.FilePath!, StringComparer.OrdinalIgnoreCase)
                .Select(g => (Project: p, File: g.Key, Items: g.ToList())))
            .OrderByDescending(c => c.Items.Max(i => i.Severity == InventorySeverity.Breaking ? 2 : 1))
            .Take(options.MaxDrafts)
            .ToList();

        foreach (var (project, relativeFile, items) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!session.Available) return;
            var absolute = Path.GetFullPath(Path.Combine(outputDir, project.RelativeDir, relativeFile));
            if (!File.Exists(absolute)) continue;

            progress?.Report($"LLM: rascunho de conversão para {project.Project.Name}/{relativeFile}...");
            var original = await File.ReadAllTextAsync(absolute, cancellationToken);
            var response = await session.TryCompleteAsync(LlmPrompts.DraftSystem, BuildUserMessage(project, relativeFile, original, items), cancellationToken);
            if (response == null) return;
            var code = LlmPrompts.ExtractCode(response);
            if (code == null) continue;

            var draftPath = Path.Combine(Path.GetDirectoryName(absolute)!, Path.GetFileNameWithoutExtension(relativeFile) + ".Migrator.cs.txt");
            var header = $"// Rascunho gerado pela LLM ({session.Assistant.Name}) a partir de {Path.GetFileName(relativeFile)}.{Environment.NewLine}" +
                         $"// Itens do inventário: {string.Join(", ", items.Select(i => i.RuleId).Distinct())}. Revise antes de usar; não é compilado.{Environment.NewLine}{Environment.NewLine}";
            await File.WriteAllTextAsync(draftPath, header + code.Replace("\r\n", "\n").Replace("\n", Environment.NewLine), new UTF8Encoding(true), cancellationToken);
            Drafts++;
            project.Inventory.Add(new InventoryItem
            {
                Project = project.Project.Name, Severity = InventorySeverity.Info, Category = InventoryCategory.Code, RuleId = "LLM-DRAFT",
                Title = $"Rascunho da versão ASP.NET Core/.NET 10 de {Path.GetFileName(relativeFile)}",
                Description = $"Gerado pela LLM para os itens {string.Join(", ", items.Select(i => i.RuleId).Distinct())}; salvo como {Path.GetFileName(draftPath)} ao lado do arquivo (não compilado).",
                Suggestion = "Compare com o original, ajuste os TODOs, renomeie para .cs e registre no Program.cs conforme o comentário do topo.",
                FilePath = Path.GetRelativePath(Path.Combine(outputDir, project.RelativeDir), draftPath).Replace('\\', '/')
            });
        }
    }

    private static string BuildUserMessage(ProjectResult project, string relativeFile, string content, List<InventoryItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Projeto: {project.Project.Name} (tipo: {project.Project.Kind}, destino net10.0)");
        sb.AppendLine($"Arquivo legado: {relativeFile}");
        sb.AppendLine();
        sb.AppendLine("O que a ferramenta detectou neste arquivo:");
        foreach (var i in items)
        {
            sb.AppendLine($"- [{i.RuleId}] {i.Title}: {i.Description}");
            if (!string.IsNullOrWhiteSpace(i.Suggestion)) sb.AppendLine($"  sugestão da ferramenta: {i.Suggestion}");
        }
        sb.AppendLine();
        sb.AppendLine("Código legado:");
        sb.AppendLine("```csharp");
        sb.AppendLine(content);
        sb.AppendLine("```");
        return sb.ToString();
    }
}
