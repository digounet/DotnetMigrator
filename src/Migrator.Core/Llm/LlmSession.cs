using Migrator.Core.Models;

namespace Migrator.Core.Llm;

/// <summary>
/// Wraps the assistant for one migration run: counts calls, and after the first infrastructure failure (server down,
/// model missing, timeout) stops calling it and records a single warning so the migration continues without the LLM.
/// </summary>
public sealed class LlmSession(ILlmAssistant assistant, SolutionResult result)
{
    public ILlmAssistant Assistant { get; } = assistant;
    public bool Available { get; private set; } = true;
    public int Calls { get; private set; }
    public string? Failure { get; private set; }

    /// <summary>Returns null when the model could not be called; the caller must then behave as if no LLM were configured.</summary>
    public async Task<string?> TryCompleteAsync(string systemMessage, string userMessage, CancellationToken cancellationToken)
    {
        if (!Available) return null;
        try
        {
            Calls++;
            return await Assistant.CompleteAsync(systemMessage, userMessage, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or NotSupportedException)
        {
            Available = false;
            Failure = ex.Message;
            result.GlobalItems.Add(new InventoryItem
            {
                Project = "(solução)", Severity = InventorySeverity.Warning, Category = InventoryCategory.Code, RuleId = "LLM-UNAVAILABLE",
                Title = $"LLM indisponível ({Assistant.Name}): etapas assistidas puladas",
                Description = ex.Message,
                Suggestion = "A migração seguiu sem a LLM. Verifique o servidor/modelo (ex.: 'ollama serve' e 'ollama pull <modelo>') ou use --llm none para silenciar."
            });
            return null;
        }
    }
}
