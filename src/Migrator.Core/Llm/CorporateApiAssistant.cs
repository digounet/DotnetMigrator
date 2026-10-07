using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Migrator.Core.Llm;

/// <summary>
/// Corporate LLM behind an HTTP API protected by OAuth2 client credentials. The URLs are fixed here (<see cref="DefaultEndpoint"/>,
/// <see cref="DefaultTokenUrl"/>): at run time only the client id and the client secret are required (options or environment
/// variables), and the model is optional. The request/response shape defaults to an OpenAI-compatible chat-completions
/// contract; the two places that usually need adjusting for a given gateway are <see cref="BuildRequest"/> and <see cref="ExtractText"/>.
/// </summary>
public sealed class CorporateApiAssistant : ILlmAssistant, IDisposable
{
    /// <summary>URL do chat da LLM corporativa. Fixe aqui o endereço do gateway do banco; --llm-endpoint / MIGRATOR_LLM_ENDPOINT só sobrepõem para testes.</summary>
    public const string DefaultEndpoint = "https://api-llm.empresa.com.br/v1/chat/completions";
    /// <summary>Endpoint OAuth2 (client credentials) que emite o token usado no chat. Vazio = enviar o client_secret como Bearer fixo.</summary>
    public const string DefaultTokenUrl = "https://api-llm.empresa.com.br/oauth/token";
    /// <summary>Modelo padrão quando nenhum é informado (--llm-model / MIGRATOR_LLM_MODEL).</summary>
    public const string DefaultModel = "gpt-4o";

    /// <summary>Environment variables read when the options do not carry the value.</summary>
    public const string EnvEndpoint = "MIGRATOR_LLM_ENDPOINT";
    public const string EnvTokenUrl = "MIGRATOR_LLM_TOKEN_URL";
    public const string EnvClientId = "MIGRATOR_LLM_CLIENT_ID";
    public const string EnvClientSecret = "MIGRATOR_LLM_CLIENT_SECRET";
    public const string EnvScope = "MIGRATOR_LLM_SCOPE";
    public const string EnvModel = "MIGRATOR_LLM_MODEL";

    private readonly HttpClient _http;
    private readonly CorporateApiSettings _settings;
    private string? _token;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    public CorporateApiAssistant(CorporateApiSettings settings, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _settings = settings.Validate();
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = timeout ?? TimeSpan.FromMinutes(4);
    }

    public string Name => $"api/{_settings.Model ?? new Uri(_settings.Endpoint).Host}";

    /// <summary>Builds the settings from explicit values, falling back to the MIGRATOR_LLM_* environment variables.</summary>
    public static CorporateApiSettings SettingsFrom(string? endpoint, string? model, string? clientId, string? clientSecret, string? tokenUrl, string? scope) => new()
    {
        Endpoint = Pick(endpoint, EnvEndpoint) ?? DefaultEndpoint,
        Model = Pick(model, EnvModel) ?? DefaultModel,
        ClientId = Pick(clientId, EnvClientId) ?? "",
        ClientSecret = Pick(clientSecret, EnvClientSecret) ?? "",
        TokenUrl = Pick(tokenUrl, EnvTokenUrl) ?? DefaultTokenUrl,
        Scope = Pick(scope, EnvScope)
    };

    private static string? Pick(string? value, string variable) => !string.IsNullOrWhiteSpace(value) ? value.Trim() : Environment.GetEnvironmentVariable(variable) is { Length: > 0 } env ? env.Trim() : null;

    public async Task<string> CompleteAsync(string systemMessage, string userMessage, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint) { Content = JsonContent.Create(BuildRequest(_settings.Model, systemMessage, userMessage)) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(cancellationToken));
        foreach (var (name, value) in _settings.ExtraHeaders) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"A API de LLM respondeu {(int)response.StatusCode} em {_settings.Endpoint}: {Shorten(body)}. Confira a URL, o escopo do token e o formato do corpo (CorporateApiAssistant.BuildRequest).");
        return ExtractText(body);
    }

    /// <summary>
    /// Request body. Default: OpenAI-compatible chat completions (what most corporate gateways expose). Adjust here if the
    /// gateway expects another shape (e.g. {"prompt": ..., "system": ...} or Bedrock's {"messages": [...]} with content blocks).
    /// </summary>
    public static JsonObject BuildRequest(string? model, string systemMessage, string userMessage)
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemMessage },
                new JsonObject { ["role"] = "user", ["content"] = userMessage }),
            // Deterministic output: the same prompt must produce the same patch on every run of the migrator.
            ["temperature"] = 0
        };
        if (!string.IsNullOrWhiteSpace(model)) request["model"] = model;
        return request;
    }

    /// <summary>
    /// Response text. Tries the usual shapes in order: OpenAI (choices[0].message.content), Anthropic (content[0].text),
    /// Bedrock Converse (output.message.content[0].text), simple gateways (text / response / result / content). Adjust here otherwise.
    /// </summary>
    public static string ExtractText(string body)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(body); }
        catch (JsonException) { return body; }
        if (root is null) return body;
        var candidates = new Func<JsonNode?>[]
        {
            () => root["choices"]?[0]?["message"]?["content"],
            () => root["choices"]?[0]?["text"],
            () => root["content"]?[0]?["text"],
            () => root["output"]?["message"]?["content"]?[0]?["text"],
            () => root["message"]?["content"],
            () => root["text"], () => root["response"], () => root["result"], () => root["answer"], () => root["content"]
        };
        foreach (var candidate in candidates)
        {
            JsonNode? node;
            try { node = candidate(); }
            catch (InvalidOperationException) { continue; }
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0) return text;
        }
        throw new InvalidOperationException("Resposta da API de LLM sem texto reconhecível (choices[0].message.content, content[0].text, output.message.content[0].text, text/response/result). Ajuste CorporateApiAssistant.ExtractText ao contrato do gateway: " + Shorten(body));
    }

    /// <summary>OAuth2 client credentials (RFC 6749 §4.4) with the token cached until shortly before it expires. Without a token URL, the client secret is sent as a static Bearer token (API key gateways).</summary>
    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.TokenUrl)) return _settings.ClientSecret;
        if (_token != null && DateTimeOffset.UtcNow < _tokenExpiresAt) return _token;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (_token != null && DateTimeOffset.UtcNow < _tokenExpiresAt) return _token;
            var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
            if (!string.IsNullOrWhiteSpace(_settings.Scope)) form["scope"] = _settings.Scope;
            using var request = new HttpRequestMessage(HttpMethod.Post, _settings.TokenUrl) { Content = new FormUrlEncodedContent(form) };
            if (_settings.ClientCredentialsInBody)
            {
                form["client_id"] = _settings.ClientId;
                form["client_secret"] = _settings.ClientSecret;
                request.Content = new FormUrlEncodedContent(form);
            }
            else request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_settings.ClientId}:{_settings.ClientSecret}")));
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"O endpoint de token respondeu {(int)response.StatusCode} em {_settings.TokenUrl}: {Shorten(body)}. Confira client_id/client_secret, o escopo e se as credenciais vão no corpo (ClientCredentialsInBody) ou em Basic Auth.");
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("access_token", out var token)) throw new InvalidOperationException("Resposta do endpoint de token sem 'access_token': " + Shorten(body));
            var expires = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 300;
            _token = token.GetString();
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, expires - 30));
            return _token!;
        }
        finally { _tokenLock.Release(); }
    }

    private static string Shorten(string text) => text.Length <= 300 ? text.Trim() : text[..300].Trim() + "...";

    public void Dispose() { _http.Dispose(); _tokenLock.Dispose(); }
}

/// <summary>Everything the corporate API needs. Only Endpoint, ClientId and ClientSecret are mandatory (TokenUrl too when the gateway issues OAuth tokens).</summary>
public sealed record CorporateApiSettings
{
    /// <summary>URL that receives the chat request (e.g. https://api-llm.empresa.com.br/v1/chat/completions).</summary>
    public required string Endpoint { get; init; }
    /// <summary>Model name when the gateway expects one in the body (null = the gateway's default).</summary>
    public string? Model { get; init; }
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    /// <summary>OAuth2 token endpoint (client credentials). Null = send ClientSecret as a static Bearer token.</summary>
    public string? TokenUrl { get; init; }
    public string? Scope { get; init; }
    /// <summary>Send client_id/client_secret in the form body (true) or as HTTP Basic (false, the RFC default most gateways accept).</summary>
    public bool ClientCredentialsInBody { get; init; } = true;
    /// <summary>Extra headers some gateways require (x-api-key, x-itau-apikey, correlation ids...).</summary>
    public Dictionary<string, string> ExtraHeaders { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public CorporateApiSettings Validate()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Endpoint)) missing.Add($"URL da API (--llm-endpoint ou {CorporateApiAssistant.EnvEndpoint})");
        if (string.IsNullOrWhiteSpace(ClientId)) missing.Add($"client id (--llm-client-id ou {CorporateApiAssistant.EnvClientId})");
        if (string.IsNullOrWhiteSpace(ClientSecret)) missing.Add($"client secret (--llm-client-secret ou {CorporateApiAssistant.EnvClientSecret})");
        if (missing.Count > 0) throw new InvalidOperationException("Provedor de LLM 'api' sem: " + string.Join("; ", missing) + ". A URL da API e o endpoint de token são fixos em CorporateApiAssistant (DefaultEndpoint/DefaultTokenUrl); o modelo é opcional (--llm-model).");
        return this;
    }
}
