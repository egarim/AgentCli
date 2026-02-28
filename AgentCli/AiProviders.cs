using System.Net.Http.Headers;

namespace AgentCli;

// ─── OpenAI ───────────────────────────────────────────────────────────────────

/// <summary>
/// OpenAI — https://api.openai.com/v1
/// Env: OPENAI_API_KEY
/// </summary>
public class OpenAiProvider : OpenAiCompatibleProvider
{
    private readonly string _apiKey;

    public OpenAiProvider(HttpClient http, string apiKey) : base(http) => _apiKey = apiKey;

    public override string Id           => "openai";
    public override string DisplayName  => "OpenAI";
    public override string DefaultModel => "gpt-4o";
    protected override string BaseUrl   => "https://api.openai.com/v1";

    protected override void AddAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
}

// ─── Azure OpenAI ─────────────────────────────────────────────────────────────

/// <summary>
/// Azure OpenAI — your own endpoint + deployment.
/// Env: AZURE_OPENAI_API_KEY, AZURE_OPENAI_ENDPOINT, AZURE_OPENAI_DEPLOYMENT
/// URL pattern: https://{resource}.openai.azure.com/openai/deployments/{deployment}/chat/completions?api-version=2024-02-01
/// </summary>
public class AzureOpenAiProvider : OpenAiCompatibleProvider
{
    private readonly string _apiKey;
    private readonly string _endpoint;    // e.g. https://myresource.openai.azure.com
    private readonly string _deployment;  // e.g. gpt-4o
    private readonly string _apiVersion;

    public AzureOpenAiProvider(
        HttpClient http,
        string apiKey,
        string endpoint,
        string deployment,
        string apiVersion = "2024-02-01") : base(http)
    {
        _apiKey     = apiKey;
        _endpoint   = endpoint.TrimEnd('/');
        _deployment = deployment;
        _apiVersion = apiVersion;
    }

    public override string Id           => "azure-openai";
    public override string DisplayName  => "Azure OpenAI";
    public override string DefaultModel => _deployment;

    // Azure ignores the "model" field in the body — model is baked into the URL
    protected override string BaseUrl         => _endpoint;
    protected override string CompletionsPath =>
        $"/openai/deployments/{_deployment}/chat/completions?api-version={_apiVersion}";

    protected override void AddAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Add("api-key", _apiKey);

    // Azure: strip "model" from body (it uses the deployment URL instead)
    protected override Dictionary<string, object> BuildRequestBody(
        List<ChatMessage> messages, string model, List<ToolDefinition>? tools)
    {
        var body = base.BuildRequestBody(messages, model, tools);
        body.Remove("model");
        return body;
    }
}

// ─── Anthropic ────────────────────────────────────────────────────────────────

/// <summary>
/// Anthropic — https://api.anthropic.com
/// Uses Messages API (not OpenAI-compatible), but we convert to SSE-compatible output.
/// Env: ANTHROPIC_API_KEY
/// </summary>
public class AnthropicProvider : IAiProvider
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;

    public AnthropicProvider(HttpClient http, string apiKey)
    {
        _http   = http;
        _apiKey = apiKey;
    }

    public string Id           => "anthropic";
    public string DisplayName  => "Anthropic";
    public string DefaultModel => "claude-sonnet-4-5";

    public async System.Collections.Generic.IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage>     messages,
        string                model,
        List<ToolDefinition>? tools = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        // Separate system message from conversation
        var systemMsg = messages.FirstOrDefault(m => m.Role == "system")?.Content ?? "";
        var convMsgs  = messages
            .Where(m => m.Role != "system")
            .Select(m => new System.Text.Json.Nodes.JsonObject
            {
                ["role"]    = m.Role == "tool" ? "user" : m.Role,
                ["content"] = m.Role == "tool"
                    ? (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonArray(
                        new System.Text.Json.Nodes.JsonObject
                        {
                            ["type"]       = "tool_result",
                            ["tool_use_id"] = m.ToolCallId,
                            ["content"]    = m.Content
                        })
                    : m.Content,
            })
            .ToList();

        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["model"]      = model,
            ["max_tokens"] = 4096,
            ["stream"]     = true,
            ["system"]     = systemMsg,
            ["messages"]   = new System.Text.Json.Nodes.JsonArray(convMsgs.Cast<System.Text.Json.Nodes.JsonNode?>().ToArray()),
        };

        if (tools?.Count > 0)
        {
            body["tools"] = new System.Text.Json.Nodes.JsonArray(
                tools.Select(t => (System.Text.Json.Nodes.JsonNode?)new System.Text.Json.Nodes.JsonObject
                {
                    ["name"]         = t.Function.Name,
                    ["description"]  = t.Function.Description,
                    ["input_schema"] = System.Text.Json.JsonSerializer.SerializeToNode(t.Function.Parameters),
                }).ToArray());
        }

        var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post,
            "https://api.anthropic.com/v1/messages");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("Accept", "text/event-stream");
        req.Headers.Add("User-Agent", "AgentCli/1.0");
        req.Content = new System.Net.Http.StringContent(
            body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new Exception($"[anthropic] HTTP {(int)res.StatusCode}: {err}");
        }

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        // Translate Anthropic SSE → OpenAI SSE format so AgentLoop works unchanged
        string? currentToolId   = null;
        string? currentToolName = null;
        int     toolIndex       = 0;

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line.Substring("data: ".Length);
            if (data == "[DONE]") break;

            System.Text.Json.Nodes.JsonNode? doc;
            try { doc = System.Text.Json.Nodes.JsonNode.Parse(data); }
            catch { continue; }

            var eventType = doc?["type"]?.GetValue<string>();

            switch (eventType)
            {
                case "content_block_start":
                    var blockType = doc?["content_block"]?["type"]?.GetValue<string>();
                    if (blockType == "tool_use")
                    {
                        currentToolId   = doc?["content_block"]?["id"]?.GetValue<string>();
                        currentToolName = doc?["content_block"]?["name"]?.GetValue<string>();
                        toolIndex       = doc?["index"]?.GetValue<int>() ?? 0;
                        // Emit tool_calls start
                        yield return BuildOpenAiChunk(toolCall: new
                        {
                            index    = toolIndex,
                            id       = currentToolId,
                            type     = "function",
                            function = new { name = currentToolName, arguments = "" }
                        });
                    }
                    break;

                case "content_block_delta":
                    var deltaType = doc?["delta"]?["type"]?.GetValue<string>();
                    if (deltaType == "text_delta")
                    {
                        var text = doc?["delta"]?["text"]?.GetValue<string>() ?? "";
                        yield return BuildOpenAiChunk(text: text);
                    }
                    else if (deltaType == "input_json_delta")
                    {
                        var partial = doc?["delta"]?["partial_json"]?.GetValue<string>() ?? "";
                        yield return BuildOpenAiChunk(toolCall: new
                        {
                            index    = toolIndex,
                            id       = (string?)null,
                            type     = (string?)null,
                            function = new { name = (string?)null, arguments = partial }
                        });
                    }
                    break;

                case "message_stop":
                    yield break;
            }
        }
    }

    private static string BuildOpenAiChunk(string? text = null, object? toolCall = null)
    {
        var delta = new System.Text.Json.Nodes.JsonObject();
        if (text    != null) delta["content"]    = text;
        if (toolCall != null) delta["tool_calls"] = new System.Text.Json.Nodes.JsonArray(
            (System.Text.Json.Nodes.JsonNode)System.Text.Json.JsonSerializer.SerializeToNode(toolCall)!);

        return new System.Text.Json.Nodes.JsonObject
        {
            ["choices"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["delta"] = delta })
        }.ToJsonString();
    }
}

// ─── Groq ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Groq — https://api.groq.com/openai/v1 (OpenAI-compatible)
/// Env: GROQ_API_KEY
/// </summary>
public class GroqProvider : OpenAiCompatibleProvider
{
    private readonly string _apiKey;

    public GroqProvider(HttpClient http, string apiKey) : base(http) => _apiKey = apiKey;

    public override string Id           => "groq";
    public override string DisplayName  => "Groq";
    public override string DefaultModel => "llama-3.3-70b-versatile";
    protected override string BaseUrl   => "https://api.groq.com/openai/v1";

    protected override void AddAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
}

// ─── Mistral ──────────────────────────────────────────────────────────────────

/// <summary>
/// Mistral AI — https://api.mistral.ai/v1 (OpenAI-compatible)
/// Env: MISTRAL_API_KEY
/// </summary>
public class MistralProvider : OpenAiCompatibleProvider
{
    private readonly string _apiKey;

    public MistralProvider(HttpClient http, string apiKey) : base(http) => _apiKey = apiKey;

    public override string Id           => "mistral";
    public override string DisplayName  => "Mistral";
    public override string DefaultModel => "mistral-large-latest";
    protected override string BaseUrl   => "https://api.mistral.ai/v1";

    protected override void AddAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
}

// ─── xAI (Grok) ───────────────────────────────────────────────────────────────

/// <summary>
/// xAI Grok — https://api.x.ai/v1 (OpenAI-compatible)
/// Env: XAI_API_KEY
/// </summary>
public class XAiProvider : OpenAiCompatibleProvider
{
    private readonly string _apiKey;

    public XAiProvider(HttpClient http, string apiKey) : base(http) => _apiKey = apiKey;

    public override string Id           => "xai";
    public override string DisplayName  => "xAI (Grok)";
    public override string DefaultModel => "grok-3-fast";
    protected override string BaseUrl   => "https://api.x.ai/v1";

    protected override void AddAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
}

// ─── OpenRouter ───────────────────────────────────────────────────────────────

/// <summary>
/// OpenRouter — https://openrouter.ai/api/v1 (OpenAI-compatible, routes to any model)
/// Env: OPENROUTER_API_KEY
/// </summary>
public class OpenRouterProvider : OpenAiCompatibleProvider
{
    private readonly string _apiKey;

    public OpenRouterProvider(HttpClient http, string apiKey) : base(http) => _apiKey = apiKey;

    public override string Id           => "openrouter";
    public override string DisplayName  => "OpenRouter";
    public override string DefaultModel => "openrouter/auto";
    protected override string BaseUrl   => "https://openrouter.ai/api/v1";

    protected override void AddAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
}

// ─── Together AI ──────────────────────────────────────────────────────────────

/// <summary>
/// Together AI — https://api.together.xyz/v1 (OpenAI-compatible)
/// Env: TOGETHER_API_KEY
/// </summary>
public class TogetherProvider : OpenAiCompatibleProvider
{
    private readonly string _apiKey;

    public TogetherProvider(HttpClient http, string apiKey) : base(http) => _apiKey = apiKey;

    public override string Id           => "together";
    public override string DisplayName  => "Together AI";
    public override string DefaultModel => "meta-llama/Llama-3.3-70B-Instruct-Turbo";
    protected override string BaseUrl   => "https://api.together.xyz/v1";

    protected override void AddAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
}

// ─── Ollama ───────────────────────────────────────────────────────────────────

/// <summary>
/// Ollama — local inference, no auth needed.
/// Env: OLLAMA_BASE_URL (default: http://localhost:11434)
/// </summary>
public class OllamaProvider : OpenAiCompatibleProvider
{
    private readonly string _baseUrl;

    public OllamaProvider(HttpClient http, string? baseUrl = null) : base(http) =>
        _baseUrl = (baseUrl ?? "http://localhost:11434").TrimEnd('/');

    public override string Id           => "ollama";
    public override string DisplayName  => "Ollama (local)";
    public override string DefaultModel => "llama3.2";
    protected override string BaseUrl   => _baseUrl;

    protected override void AddAuthHeaders(HttpRequestMessage req) { /* no auth */ }
}
