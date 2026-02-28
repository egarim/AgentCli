using System.Text.Json.Nodes;

namespace AgentCli;

/// <summary>
/// GitHub Copilot Device Auth Flow — same as OpenClaw does it.
/// client_id: Iv1.b507a08c87ecfe98 (VS Code)
/// </summary>
public class GitHubDeviceAuth
{
    private const string ClientId = "Iv1.b507a08c87ecfe98";
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    private readonly HttpClient _http;

    public GitHubDeviceAuth(HttpClient http) => _http = http;

    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl);
        req.Headers.Add("Accept", "application/json");
        req.Content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("client_id", ClientId),
            new KeyValuePair<string, string>("scope", "read:user"),
        });

        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;
        return new DeviceCodeResponse(
            DeviceCode:      json["device_code"]!.GetValue<string>(),
            UserCode:        json["user_code"]!.GetValue<string>(),
            VerificationUri: json["verification_uri"]!.GetValue<string>(),
            ExpiresIn:       json["expires_in"]!.GetValue<int>(),
            Interval:        json["interval"]!.GetValue<int>()
        );
    }

    public async Task<string> PollForAccessTokenAsync(
        DeviceCodeResponse device,
        CancellationToken ct = default)
    {
        var expiresAt  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + device.ExpiresIn * 1000L;
        var intervalMs = Math.Max(1000, device.Interval * 1000);

        var body = new Dictionary<string, string>
        {
            ["client_id"]   = ClientId,
            ["device_code"] = device.DeviceCode,
            ["grant_type"]  = "urn:ietf:params:oauth:grant-type:device_code",
        };

        while (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < expiresAt && !ct.IsCancellationRequested)
        {
            await Task.Delay(intervalMs, ct);

            var req = new HttpRequestMessage(HttpMethod.Post, AccessTokenUrl);
            req.Headers.Add("Accept", "application/json");
            req.Content = new FormUrlEncodedContent(body);

            var res = await _http.SendAsync(req, ct);
            res.EnsureSuccessStatusCode();

            var json = JsonNode.Parse(await res.Content.ReadAsStringAsync())!;

            if (json["access_token"]?.GetValue<string>() is { Length: > 0 } token)
                return token; // ghu_...

            var error = json["error"]?.GetValue<string>() ?? "unknown";
            switch (error)
            {
                case "authorization_pending":
                    Console.Write(".");
                    break;
                case "slow_down":
                    intervalMs += 2000;
                    Console.Write("~");
                    break;
                case "expired_token": throw new Exception("Device code expired — run again");
                case "access_denied": throw new Exception("Login cancelled by user");
                default:              throw new Exception($"GitHub device flow error: {error}");
            }
        }

        throw new Exception("Device code expired — run again");
    }
}

public record DeviceCodeResponse(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresIn,
    int Interval
);
