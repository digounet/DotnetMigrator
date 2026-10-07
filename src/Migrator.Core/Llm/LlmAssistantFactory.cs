namespace Migrator.Core.Llm;

/// <summary>
/// Resolves the configured provider. To add the corporate SDK: implement <see cref="ILlmAssistant"/> (two strings in,
/// one out) and add a case here, or build the instance yourself and pass it to <c>new MigrationEngine(assistant)</c>.
/// </summary>
public static class LlmAssistantFactory
{
    public static ILlmAssistant? Create(LlmOptions options)
    {
        if (!options.Enabled) return null;
        ILlmAssistant assistant = options.Provider.Trim().ToLowerInvariant() switch
        {
            LlmOptions.Ollama => new OllamaAssistant(options.Endpoint, options.Model, options.Timeout),
            LlmOptions.Api => new CorporateApiAssistant(CorporateApiAssistant.SettingsFrom(options.Endpoint, options.Model, options.ClientId, options.ClientSecret, options.TokenUrl, options.Scope), timeout: options.Timeout),
            var other => throw new NotSupportedException(
                $"Provedor de LLM desconhecido: '{other}'. Provedores disponíveis: {string.Join(", ", Providers)}. " +
                "Para um provedor próprio, implemente ILlmAssistant e registre em LlmAssistantFactory ou passe a instância para MigrationEngine.")
        };
        return Wrap(assistant, options);
    }

    /// <summary>Applies the response cache (when configured) to any assistant, including custom ones.</summary>
    public static ILlmAssistant Wrap(ILlmAssistant assistant, LlmOptions options) =>
        string.IsNullOrWhiteSpace(options.CacheDir) || assistant is CachedLlmAssistant ? assistant : new CachedLlmAssistant(assistant, options.CacheDir);

    public static IReadOnlyList<string> Providers => [LlmOptions.None, LlmOptions.Ollama, LlmOptions.Api];
}
