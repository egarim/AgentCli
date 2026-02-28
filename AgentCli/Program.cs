using System.Text.Json.Nodes;
using System.Text;
using AgentCli;

// ─── Config ───────────────────────────────────────────────────────────────────
var configDir  = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentcli");
var configFile = Path.Combine(configDir, "config.json");
Directory.CreateDirectory(configDir);

async Task<string?> LoadGitHubTokenAsync()
{
    if (!File.Exists(configFile)) return null;
    try { return JsonNode.Parse(await File.ReadAllTextAsync(configFile))?["github_token"]?.GetValue<string>(); }
    catch { return null; }
}

async Task SaveGitHubTokenAsync(string token)
{
    await File.WriteAllTextAsync(configFile, new JsonObject { ["github_token"] = token }.ToJsonString());
    Console.WriteLine($"Token saved to {configFile}");
}

// ─── Memory + Soul ────────────────────────────────────────────────────────────
var memory = new MemorySystem();

// Seed SOUL.md if it doesn't exist yet
if (!File.Exists(memory.SoulPath))
{
    await memory.WriteSoulAsync("""
        # SOUL.md

        You are a helpful, direct, and practical AI assistant running locally.
        You remember things the user tells you — write important facts to memory.
        Be concise. Lead with the answer. Don't pad responses.
        You have tools — use them when helpful, not speculatively.
        """);
    Console.WriteLine($"Created {memory.SoulPath} — edit it to change the agent's personality.");
}

// ─── Services ─────────────────────────────────────────────────────────────────
var http         = new HttpClient();
var deviceAuth   = new GitHubDeviceAuth(http);
var tokenService = new CopilotTokenService(http);

// ─── Login ────────────────────────────────────────────────────────────────────
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
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = device.VerificationUri, UseShellExecute = true }); }
    catch { }
    Console.Write("Waiting for authorization");
    githubToken = await deviceAuth.PollForAccessTokenAsync(device);
    Console.WriteLine(" ✓");
    await SaveGitHubTokenAsync(githubToken);
}

// ─── Build system prompt with memory context ──────────────────────────────────
var memoryContext = await memory.BuildContextAsync();
var systemPrompt = string.IsNullOrWhiteSpace(memoryContext)
    ? "You are a helpful AI assistant."
    : $"""
      {memoryContext}

      ---

      You have access to tools. Use memory_write to remember important things
      the user tells you. Use memory_search to recall past conversations.
      Be direct and concise.
      """;

// ─── Build agent ──────────────────────────────────────────────────────────────
var chatClient = new CopilotChatClient(http, tokenService, githubToken) { Model = "claude-sonnet-4.5" };
var agent = new AgentLoop(chatClient, systemPrompt);

// ─── Tools ────────────────────────────────────────────────────────────────────

agent.RegisterTool(
    name: "get_time",
    description: "Returns the current local date and time",
    schema: new { type = "object", properties = new { } },
    handler: _ => Task.FromResult(DateTime.Now.ToString("F"))
);

agent.RegisterTool(
    name: "web_fetch",
    description: "Fetches plain text content from a URL",
    schema: new
    {
        type = "object",
        properties = new { url = new { type = "string", description = "URL to fetch" } },
        required = new[] { "url" }
    },
    handler: async args =>
    {
        var url = args.GetProperty("url").GetString()!;
        using var wh = new HttpClient();
        wh.DefaultRequestHeaders.Add("User-Agent", "AgentCli/1.0");
        var html = await wh.GetStringAsync(url);
        return html.Length > 2000 ? html[..2000] + "\n...(truncated)" : html;
    }
);

agent.RegisterTool(
    name: "memory_write",
    description: "Saves an important fact or note to long-term memory (MEMORY.md). Use this to remember things the user tells you — name, preferences, context, decisions.",
    schema: new
    {
        type = "object",
        properties = new
        {
            section = new { type = "string", description = "Short section heading, e.g. 'User Preferences' or 'Project Context'" },
            content = new { type = "string", description = "What to remember" }
        },
        required = new[] { "section", "content" }
    },
    handler: async args =>
    {
        var section = args.GetProperty("section").GetString()!;
        var content = args.GetProperty("content").GetString()!;
        await memory.AppendMemoryAsync(section, content);
        return $"Saved to memory: [{section}]";
    }
);

agent.RegisterTool(
    name: "memory_search",
    description: "Search your memory files for relevant past context",
    schema: new
    {
        type = "object",
        properties = new
        {
            query = new { type = "string", description = "Search query" }
        },
        required = new[] { "query" }
    },
    handler: async args =>
    {
        var query = args.GetProperty("query").GetString()!;
        var results = await memory.SearchAsync(query, maxResults: 5);
        if (results.Count == 0) return "No memory found for that query.";
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"[{Path.GetFileName(r.Path)}:{r.Line}] (score {r.Score:F2})");
            sb.AppendLine(r.Snippet);
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }
);

agent.RegisterTool(
    name: "memory_read_all",
    description: "Read the full contents of MEMORY.md",
    schema: new { type = "object", properties = new { } },
    handler: async _ =>
    {
        var content = await memory.ReadMemoryAsync();
        return content ?? "(MEMORY.md is empty)";
    }
);

agent.RegisterTool(
    name: "daily_note",
    description: "Append a note to today's daily log (memory/YYYY-MM-DD.md)",
    schema: new
    {
        type = "object",
        properties = new
        {
            content = new { type = "string", description = "Note to append to today's log" }
        },
        required = new[] { "content" }
    },
    handler: async args =>
    {
        var content = args.GetProperty("content").GetString()!;
        await memory.AppendDailyAsync(content);
        return $"Appended to daily note ({DateTime.Today:yyyy-MM-dd})";
    }
);

// ─── REPL ─────────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("AgentCli ready. Type your message (Ctrl+C to exit, 'exit' to quit)");
Console.WriteLine($"Workspace: {memory.SoulPath.Replace("SOUL.md", "")}");
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

    // Local commands
    if (input == "/memory")
    {
        var content = await memory.ReadMemoryAsync();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(content ?? "(empty)");
        Console.ResetColor();
        Console.WriteLine();
        continue;
    }
    if (input == "/soul")
    {
        var content = await memory.ReadSoulAsync();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(content ?? "(empty)");
        Console.ResetColor();
        Console.WriteLine();
        continue;
    }
    if (input == "/daily")
    {
        var content = await memory.ReadDailyAsync();
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(content ?? "(no daily note today)");
        Console.ResetColor();
        Console.WriteLine();
        continue;
    }

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
