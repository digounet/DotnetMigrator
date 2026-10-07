namespace Migrator.Core.Llm;

/// <summary>
/// The only contract the migrator needs from a language model: a system message and a user message in, text out.
/// Implement it for your provider (corporate SDK, Azure OpenAI, Bedrock...) and either register it in
/// <see cref="LlmAssistantFactory"/> or pass the instance to <c>new MigrationEngine(assistant)</c>.
/// The migrator works without any implementation: every LLM step is optional and skipped when none is configured.
/// </summary>
public interface ILlmAssistant
{
    /// <summary>Provider and model, for reports and cache keys (e.g. "ollama/qwen2.5-coder:3b").</summary>
    string Name { get; }

    Task<string> CompleteAsync(string systemMessage, string userMessage, CancellationToken cancellationToken = default);
}

/// <summary>Configuration for the optional LLM layer. <c>Provider = "none"</c> (default) disables everything.</summary>
public sealed record LlmOptions
{
    public const string None = "none";
    public const string Ollama = "ollama";
    /// <summary>Corporate LLM behind an HTTP API with OAuth2 client credentials (<see cref="CorporateApiAssistant"/>).</summary>
    public const string Api = "api";

    public string Provider { get; init; } = None;
    /// <summary>Model name as understood by the provider (Ollama: "qwen2.5-coder:3b").</summary>
    public string? Model { get; init; }
    /// <summary>Provider endpoint (Ollama: http://localhost:11434; api: URL do chat, ex. https://api-llm.empresa.com.br/v1/chat/completions; also MIGRATOR_LLM_ENDPOINT).</summary>
    public string? Endpoint { get; init; }
    /// <summary>api provider: OAuth2 client credentials and token endpoint (or the MIGRATOR_LLM_CLIENT_ID / _CLIENT_SECRET / _TOKEN_URL / _SCOPE variables).</summary>
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? TokenUrl { get; init; }
    public string? Scope { get; init; }
    /// <summary>Maximum build → fix → rebuild rounds after the verification build fails.</summary>
    public int MaxFixRounds { get; init; } = 3;
    /// <summary>Maximum files sent to the model per fix round (the ones with most errors first).</summary>
    public int MaxFilesPerRound { get; init; } = 25;
    /// <summary>Maximum legacy files (IHttpModule, ServiceBase, Global.asax...) for which a conversion draft is requested.</summary>
    public int MaxDrafts { get; init; } = 10;
    /// <summary>Per-call timeout (local models can be slow).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(6);
    /// <summary>Directory for the response cache (same prompt → same answer across runs). Null disables caching.</summary>
    public string? CacheDir { get; init; } = DefaultCacheDir;

    public bool Enabled => !string.Equals(Provider, None, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Provider);

    public static string DefaultCacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".migrator", "llm-cache");
}
