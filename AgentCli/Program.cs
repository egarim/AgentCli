using System.Text.Json.Nodes;
using System.Text.Json;
using AgentCli;

// ─── Config file ─────────────────────────────────────────────────────────────
var configDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentcli");
var configFile = Path.Combine(configDir, "config.json");
Directory.CreateDirectory(configDir);

async Task<string?> LoadGitHubTokenAsync()
{
    if (!File.Exists(configFile)) return null;
    try
    {
        var json = JsonNode.Parse(await File.ReadAllTextAsync(configFile));
        return json?["github_token"]?.GetValue<string>();
    }
    catch { return null; }
}

async Task SaveGitHubTokenAsync(string token)
{
    var obj = new JsonObject { ["github_token"] = token };
    await File.WriteAllTextAsync(configFile, obj.ToJsonString());
    Console.WriteLine($"Token saved to {configFile}");
}

// ─── Services ────────────────────────────────────────────────────────────────
var http         = new HttpClient();
var deviceAuth   = new GitHubDeviceAuth(http);
var tokenService = new CopilotTokenService(http);

// ─── Login ───────────────────────────────────────────────────────────────────
var githubToken = await LoadGitHubTokenAsync();

if (githubToken == null || args.Contains("--login"))
{
    Console.WriteLine("=== GitHub Copilot Login ===");

    var device = await deviceAuth.RequestDeviceCodeAsync();

    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"  1. Open:  {device.VerificationUri}");
    Console.WriteLine($"  2. Enter: {device.UserCode}");
    Console.ResetColor();
    Console.WriteLine();

    // Try to open browser
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = device.VerificationUri,
            UseShellExecute = true
        });
    }
    catch { /* headless — user opens manually */ }

    Console.Write("Waiting for authorization");
    githubToken = await deviceAuth.PollForAccessTokenAsync(device);
    Console.WriteLine(" ✓");

    await SaveGitHubTokenAsync(githubToken);
}

// ─── Build agent ─────────────────────────────────────────────────────────────
var chatClient = new CopilotChatClient(http, tokenService, githubToken)
{
    Model = "claude-sonnet-4.5"
};

var agent = new AgentLoop(chatClient, systemPrompt: """
    You are a helpful assistant running in a C# console app.
    Be concise and direct. You have access to tools — use them when helpful.
    """);

// ─── Register tools ───────────────────────────────────────────────────────────

// Tool: get current time
agent.RegisterTool(
    name: "get_time",
    description: "Returns the current local date and time",
    schema: new { type = "object", properties = new { } },
    handler: _ => Task.FromResult(DateTime.Now.ToString("F"))
);

// Tool: web fetch (simple)
agent.RegisterTool(
    name: "web_fetch",
    description: "Fetches plain text content from a URL",
    schema: new
    {
        type = "object",
        properties = new
        {
            url = new { type = "string", description = "The URL to fetch" }
        },
        required = new[] { "url" }
    },
    handler: async args =>
    {
        var url = args.GetProperty("url").GetString()!;
        using var wh = new HttpClient();
        wh.DefaultRequestHeaders.Add("User-Agent", "AgentCli/1.0");
        var html = await wh.GetStringAsync(url);
        // Return first 2000 chars
        return html.Length > 2000 ? html[..2000] + "...(truncated)" : html;
    }
);

// ─── REPL ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("AgentCli ready. Type your message (Ctrl+C to exit, 'exit' to quit)");
Console.ResetColor();
Console.WriteLine();

while (true)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write("You: ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write("Agent: ");
    Console.ResetColor();

    try
    {
        await agent.RunAsync(input);
    }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }

    Console.WriteLine();
}
