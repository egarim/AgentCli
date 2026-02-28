using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentCli;

/// <summary>
/// Exchanges a GitHub OAuth token (ghu_...) for a short-lived Copilot API token.
/// Caches on disk — same pattern as OpenClaw.
/// </summary>
public class CopilotTokenService
{
    private const string CopilotTokenUrl = "https://api.github.com/copilot_internal/v2/token";
    private const string DefaultBaseUrl  = "https://api.individual.githubcopilot.com";
    private const long   BufferMs        = 300_000; // 5 min buffer before expiry

    private readonly HttpClient _http;
    private readonly string     _cachePath;

    // In-memory cache
    private string? _cachedToken;
    private long    _cachedExpiresAt;

    public CopilotTokenService(HttpClient http, string? cachePath = null)
    {
        _http = http;
        _cachePath = cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "copilot-token.json");
    }

    public async Task<(string Token, string BaseUrl)> GetTokenAsync(string githubToken)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 1. In-memory cache
        if (_cachedToken != null && now < _cachedExpiresAt - BufferMs)
            return (_cachedToken, DeriveBaseUrl(_cachedToken));

        // 2. Disk cache
        if (File.Exists(_cachePath))
        {
            try
            {
                var disk = JsonNode.Parse(await File.ReadAllTextAsync(_cachePath))!;
                var dt   = disk["token"]?.GetValue<string>();
                var de   = disk["expires_at"]?.GetValue<long>() ?? 0;
                if (dt != null && now < de - BufferMs)
                {
                    _cachedToken     = dt;
                    _cachedExpiresAt = de;
                    return (dt, DeriveBaseUrl(dt));
                }
            }
            catch { /* ignore corrupt cache */ }
        }

        // 3. Fetch fresh token
        var req = new HttpRequestMessage(HttpMethod.Get, CopilotTokenUrl);
        req.Headers.Add("Authorization", $"Bearer {githubToken}");
        req.Headers.Add("Accept", "application/json");

        var res = await _http.SendAsync(req);
        if (!res.IsSuccessStatusCode)
        {
            var body = await res.Content.ReadAsStringAsync();
            throw new Exception($"Copilot token exchange failed: HTTP {(int)res.StatusCode} — {body}");
        }

        var json       = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        var token      = json["token"]!.GetValue<string>();
        var expiresAt  = json["expires_at"]!.GetValue<long>();
        var expiresAtMs = expiresAt > 1_000_000_000_000L ? expiresAt : expiresAt * 1000;

        _cachedToken     = token;
        _cachedExpiresAt = expiresAtMs;

        // Save to disk
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        await File.WriteAllTextAsync(_cachePath, new JsonObject
        {
            ["token"]      = token,
            ["expires_at"] = expiresAtMs,
            ["updated_at"] = now,
        }.ToJsonString());

        return (token, DeriveBaseUrl(token));
    }

    private static string DeriveBaseUrl(string token)
    {
        var m = Regex.Match(token, @"(?:^|;)\s*proxy-ep=([^;\s]+)", RegexOptions.IgnoreCase);
        if (!m.Success) return DefaultBaseUrl;
        var host = m.Groups[1].Value
            .Replace("https://", "").Replace("http://", "")
            .Replace("proxy.", "api.", StringComparison.OrdinalIgnoreCase);
        return $"https://{host}";
    }
}
