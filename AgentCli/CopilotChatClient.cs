using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentCli;

// ─── Message types ────────────────────────────────────────────────────────────

public record ChatMessage(
    [property: JsonPropertyName("role")]         string Role,
    [property: JsonPropertyName("content")]      string? Content,
    [property: JsonPropertyName("tool_calls")]   List<ToolCall>? ToolCalls    = null,
    [property: JsonPropertyName("tool_call_id")] string?         ToolCallId   = null
);

public record ToolCall(
    [property: JsonPropertyName("id")]       string           Id,
    [property: JsonPropertyName("type")]     string           Type,
    [property: JsonPropertyName("function")] ToolCallFunction Function
);

public record ToolCallFunction(
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("arguments")] string Arguments
);

public record ToolDefinition(
    [property: JsonPropertyName("type")]     string          Type,
    [property: JsonPropertyName("function")] ToolFunctionDef Function
);

public record ToolFunctionDef(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")]  object Parameters
);

// ─── Chat client ──────────────────────────────────────────────────────────────

/// <summary>
/// Streams from GitHub Copilot chat completions endpoint (OpenAI-compatible).
/// Yields raw SSE data lines — caller parses JSON.
/// </summary>
public class CopilotChatClient
{
    private readonly HttpClient          _http;
    private readonly CopilotTokenService _tokenService;
    private readonly string              _githubToken;
    public  string                       Model { get; set; } = "claude-sonnet-4.5";

    public CopilotChatClient(HttpClient http, CopilotTokenService tokenService, string githubToken)
    {
        _http         = http;
        _tokenService = tokenService;
        _githubToken  = githubToken;
    }

    public async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage>    messages,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (apiToken, baseUrl) = await _tokenService.GetTokenAsync(_githubToken);

        var body = new Dictionary<string, object>
        {
            ["model"]      = Model,
            ["messages"]   = messages,
            ["stream"]     = true,
            ["max_tokens"] = 4096,
        };
        if (tools?.Count > 0)
            body["tools"] = tools;

        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        req.Headers.Add("Accept", "text/event-stream");
        // Required by Copilot API — same headers OpenClaw sends
        req.Headers.Add("Copilot-Integration-Id",  "vscode-chat");
        req.Headers.Add("Editor-Version",           "vscode/1.96.0");
        req.Headers.Add("Editor-Plugin-Version",    "copilot-chat/0.23.0");
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
            throw new Exception($"Copilot API error: HTTP {(int)res.StatusCode} — {err}");
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
}
