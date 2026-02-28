using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCli;

/// <summary>
/// Base for any provider that speaks the OpenAI chat completions API.
/// Subclasses only need to provide: Id, DisplayName, DefaultModel, BaseUrl,
/// and override AddAuthHeaders() + BuildRequestBody() if needed.
///
/// Covers: OpenAI, Azure OpenAI, Groq, Mistral, Ollama, OpenRouter, xAI, Together, etc.
/// </summary>
public abstract class OpenAiCompatibleProvider : IAiProvider
{
    protected readonly HttpClient _http;

    protected OpenAiCompatibleProvider(HttpClient http) => _http = http;

    public abstract string Id          { get; }
    public abstract string DisplayName { get; }
    public abstract string DefaultModel { get; }

    /// <summary>Full base URL — must NOT end with slash.</summary>
    protected abstract string BaseUrl { get; }

    /// <summary>Override to add Authorization / api-key / custom headers.</summary>
    protected abstract void AddAuthHeaders(HttpRequestMessage req);

    /// <summary>Endpoint path appended to BaseUrl. Override for Azure deployments.</summary>
    protected virtual string CompletionsPath => "/chat/completions";

    public async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage>     messages,
        string                model,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = BuildRequestBody(messages, model, tools);

        var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + CompletionsPath);
        req.Headers.Add("Accept", "text/event-stream");
        req.Headers.Add("User-Agent", "AgentCli/1.0");
        AddAuthHeaders(req);
        req.Content = new StringContent(
            JsonSerializer.Serialize(body, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            }),
            Encoding.UTF8, "application/json");

        using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new Exception($"[{Id}] HTTP {(int)res.StatusCode}: {err}");
        }

        using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: ")) continue;
            var data = line.Substring("data: ".Length);
            if (data == "[DONE]") break;
            yield return data;
        }
    }

    protected virtual Dictionary<string, object> BuildRequestBody(
        List<ChatMessage>     messages,
        string                model,
        List<ToolDefinition>? tools)
    {
        var body = new Dictionary<string, object>
        {
            ["model"]      = model,
            ["messages"]   = messages,
            ["stream"]     = true,
            ["max_tokens"] = 4096,
        };
        if (tools?.Count > 0)
            body["tools"] = tools;
        return body;
    }
}
