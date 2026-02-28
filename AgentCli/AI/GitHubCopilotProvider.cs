using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentCli;

/// <summary>
/// GitHub Copilot provider — device auth + short-lived token exchange.
/// Wraps CopilotTokenService to implement IAiProvider.
/// </summary>
public class GitHubCopilotProvider : IAiProvider
{
    private readonly HttpClient          _http;
    private readonly CopilotTokenService _tokenService;
    private readonly string              _githubToken;

    public GitHubCopilotProvider(HttpClient http, CopilotTokenService tokenService, string githubToken)
    {
        _http         = http;
        _tokenService = tokenService;
        _githubToken  = githubToken;
    }

    public string Id           => "github-copilot";
    public string DisplayName  => "GitHub Copilot";
    public string DefaultModel => "claude-sonnet-4.5";

    public async IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage>     messages,
        string                model,
        List<ToolDefinition>? tools = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (apiToken, baseUrl) = await _tokenService.GetTokenAsync(_githubToken);

        var body = new Dictionary<string, object>
        {
            ["model"]      = model,
            ["messages"]   = messages,
            ["stream"]     = true,
            ["max_tokens"] = 4096,
        };
        if (tools?.Count > 0)
            body["tools"] = tools;

        var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        req.Headers.Add("Accept", "text/event-stream");
        req.Headers.Add("User-Agent", "AgentCli/1.0");
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
            throw new Exception($"[github-copilot] HTTP {(int)res.StatusCode}: {err}");
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
