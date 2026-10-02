using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Migrator.Core.Llm;

/// <summary>Local models through the Ollama chat API (http://localhost:11434/api/chat). Intended for development and tests.</summary>
public sealed class OllamaAssistant : ILlmAssistant, IDisposable
{
    public const string DefaultEndpoint = "http://localhost:11434";
    public const string DefaultModel = "qwen2.5-coder:3b";

    private readonly HttpClient _http;
    private readonly string _model;

    public OllamaAssistant(string? endpoint = null, string? model = null, TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri((string.IsNullOrWhiteSpace(endpoint) ? DefaultEndpoint : endpoint).TrimEnd('/') + "/");
        _http.Timeout = timeout ?? TimeSpan.FromMinutes(4);
    }

    public string Name => $"ollama/{_model}";

    public static JsonObject BuildRequest(string model, string systemMessage, string userMessage) => new()
    {
        ["model"] = model,
        ["stream"] = false,
        // Deterministic output: the same prompt must produce the same patch on every run of the migrator.
        ["options"] = new JsonObject { ["temperature"] = 0, ["seed"] = 42, ["num_ctx"] = 16384 },
        ["messages"] = new JsonArray(
            new JsonObject { ["role"] = "system", ["content"] = systemMessage },
            new JsonObject { ["role"] = "user", ["content"] = userMessage })
    };

    public async Task<string> CompleteAsync(string systemMessage, string userMessage, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsJsonAsync("api/chat", BuildRequest(_model, systemMessage, userMessage), cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Ollama respondeu {(int)response.StatusCode} para o modelo '{_model}': {Shorten(body)}. Confira 'ollama list' / 'ollama pull {_model}'.");
        }
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
        return doc.RootElement.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
            ? content.GetString() ?? ""
            : throw new InvalidOperationException("Resposta do Ollama sem 'message.content'.");
    }

    private static string Shorten(string text) => text.Length <= 200 ? text.Trim() : text[..200].Trim() + "...";

    public void Dispose() => _http.Dispose();
}
