using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Migrator.Core.Llm;

namespace Migrator.Tests;

/// <summary>Corporate LLM API: OAuth2 client credentials (token cached), OpenAI-compatible body by default, several response shapes recognized.</summary>
public sealed class CorporateApiAssistantTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await respond(request);
        }
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Fetches_a_token_once_and_calls_the_chat_endpoint_with_it()
    {
        var tokens = 0;
        var handler = new StubHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth/token"))
            {
                tokens++;
                var form = await request.Content!.ReadAsStringAsync();
                Assert.Contains("grant_type=client_credentials", form);
                Assert.Contains("client_id=meu-id", form);
                Assert.Contains("client_secret=meu-segredo", form);
                return Json("""{"access_token":"tok-123","expires_in":3600}""");
            }
            Assert.Equal("Bearer tok-123", request.Headers.Authorization!.ToString());
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("gpt-x", body["model"]!.GetValue<string>());
            Assert.Equal("sys", body["messages"]![0]!["content"]!.GetValue<string>());
            return Json("""{"choices":[{"message":{"role":"assistant","content":"resposta"}}]}""");
        });
        using var assistant = new CorporateApiAssistant(new CorporateApiSettings { Endpoint = "https://llm.test/v1/chat/completions", TokenUrl = "https://llm.test/oauth/token", ClientId = "meu-id", ClientSecret = "meu-segredo", Model = "gpt-x" }, handler);

        Assert.Equal("resposta", await assistant.CompleteAsync("sys", "user"));
        Assert.Equal("resposta", await assistant.CompleteAsync("sys", "user 2"));
        Assert.Equal(1, tokens);                                                 // token reused until it expires
        Assert.Equal("api/gpt-x", assistant.Name);
    }

    [Fact]
    public async Task Without_token_url_the_secret_is_a_static_bearer_token()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("Bearer chave-fixa", request.Headers.Authorization!.ToString());
            return Task.FromResult(Json("""{"content":[{"type":"text","text":"ok"}]}"""));
        });
        using var assistant = new CorporateApiAssistant(new CorporateApiSettings { Endpoint = "https://llm.test/chat", TokenUrl = null, ClientId = "id", ClientSecret = "chave-fixa" }, handler);
        Assert.Equal("ok", await assistant.CompleteAsync("s", "u"));
    }

    [Fact]
    public void Recognizes_common_response_shapes_and_reports_unknown_ones()
    {
        Assert.Equal("a", CorporateApiAssistant.ExtractText("""{"choices":[{"message":{"content":"a"}}]}"""));
        Assert.Equal("b", CorporateApiAssistant.ExtractText("""{"content":[{"text":"b"}]}"""));
        Assert.Equal("c", CorporateApiAssistant.ExtractText("""{"output":{"message":{"content":[{"text":"c"}]}}}"""));
        Assert.Equal("d", CorporateApiAssistant.ExtractText("""{"response":"d"}"""));
        Assert.Equal("texto puro", CorporateApiAssistant.ExtractText("texto puro"));
        var ex = Assert.Throws<InvalidOperationException>(() => CorporateApiAssistant.ExtractText("""{"status":"ok"}"""));
        Assert.Contains("ExtractText", ex.Message);
    }

    [Fact]
    public void Settings_come_from_options_or_environment_and_only_credentials_are_mandatory()
    {
        Environment.SetEnvironmentVariable(CorporateApiAssistant.EnvClientId, "env-id");
        Environment.SetEnvironmentVariable(CorporateApiAssistant.EnvClientSecret, "env-secret");
        try
        {
            var settings = CorporateApiAssistant.SettingsFrom(endpoint: null, model: null, clientId: null, clientSecret: null, tokenUrl: null, scope: null).Validate();
            Assert.Equal(CorporateApiAssistant.DefaultEndpoint, settings.Endpoint);     // the gateway URL is fixed in code
            Assert.Equal(CorporateApiAssistant.DefaultTokenUrl, settings.TokenUrl);
            Assert.Equal(CorporateApiAssistant.DefaultModel, settings.Model);
            Assert.Equal(("env-id", "env-secret"), (settings.ClientId, settings.ClientSecret));
            Assert.Equal("explicit-id", CorporateApiAssistant.SettingsFrom(null, "m", "explicit-id", null, null, null).ClientId);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CorporateApiAssistant.EnvClientId, null);
            Environment.SetEnvironmentVariable(CorporateApiAssistant.EnvClientSecret, null);
        }
        var ex = Assert.Throws<InvalidOperationException>(() => CorporateApiAssistant.SettingsFrom(null, null, null, null, null, null).Validate());
        Assert.Contains("client id", ex.Message);
        Assert.Contains("client secret", ex.Message);

        Assert.IsType<CorporateApiAssistant>(LlmAssistantFactory.Create(new LlmOptions { Provider = LlmOptions.Api, ClientId = "a", ClientSecret = "b", CacheDir = null }));
        Assert.Contains(LlmOptions.Api, LlmAssistantFactory.Providers);
    }
}
