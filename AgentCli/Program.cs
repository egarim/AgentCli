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
var memory = MemorySystem.CreateFile();

if (!await memory.Provider.ExistsAsync(MemorySystem.KeySoul))
{
    await memory.WriteSoulAsync("""
        # SOUL.md
        You are a helpful, direct, and practical AI assistant running locally.
        You remember things the user tells you — write important facts to memory.
        Be concise. Lead with the answer. Don't pad responses.
        You have tools — use them when helpful, not speculatively.
        """);
    Console.WriteLine("Created SOUL.md — edit it to change the agent's personality.");
}

if (!await memory.Provider.ExistsAsync(MemorySystem.KeyWorkflow))
{
    await memory.WriteWorkflowAutoAsync("""
        # WORKFLOW_AUTO.md
        # Files listed here are automatically read on every startup.
        # Ensures protocols are restored after context resets / compaction.
        # Supports date placeholder: memory/YYYY-MM-DD.md (resolves to today + yesterday)

        - MEMORY.md
        - memory/YYYY-MM-DD.md
        """);
    Console.WriteLine("Created WORKFLOW_AUTO.md — edit it to control startup reads.");
}

// ─── Services ─────────────────────────────────────────────────────────────────
var http         = new HttpClient();
var deviceAuth   = new GitHubDeviceAuth(http);
var tokenService = new CopilotTokenService(http);

// ─── GitHub Copilot login ─────────────────────────────────────────────────────
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
    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        { FileName = device.VerificationUri, UseShellExecute = true }); }
    catch { }
    Console.Write("Waiting for authorization");
    githubToken = await deviceAuth.PollForAccessTokenAsync(device);
    Console.WriteLine(" ✓");
    await SaveGitHubTokenAsync(githubToken);
}

// ─── Provider registry ────────────────────────────────────────────────────────
var registry = ProviderRegistry.FromEnvironment(http, tokenService, githubToken);

// Honour --provider <id> flag
var providerArg = args.SkipWhile(a => a != "--provider").Skip(1).FirstOrDefault();
if (providerArg != null)
{
    try { registry.SetActive(providerArg); }
    catch (Exception ex) { Console.WriteLine($"Warning: {ex.Message}"); }
}

// ─── System prompt ────────────────────────────────────────────────────────────
var memoryContext = await memory.BuildContextAsync();
var systemPrompt  = string.IsNullOrWhiteSpace(memoryContext)
    ? "You are a helpful AI assistant."
    : $"""
      {memoryContext}

      ---
      You have access to tools. Use memory_write to remember important things the user tells you.
      Use memory_search to recall past conversations. Be direct and concise.
      """;

// ─── Agent (rebuilt on /switch) ───────────────────────────────────────────────
AgentLoop BuildAgent() => new AgentLoop(registry.Active, systemPrompt,
    args.SkipWhile(a => a != "--model").Skip(1).FirstOrDefault());

var agent = BuildAgent();

// ─── Tool registration helper ─────────────────────────────────────────────────
void RegisterTools(AgentLoop a)
{
    a.RegisterTool("get_time", "Returns the current local date and time",
        new { type = "object", properties = new { } },
        _ => Task.FromResult(DateTime.Now.ToString("F")));

    a.RegisterTool("web_fetch", "Fetches plain text content from a URL",
        new { type = "object",
              properties = new { url = new { type = "string" } },
              required   = new[] { "url" } },
        async args2 =>
        {
            var url = args2.GetProperty("url").GetString()!;
            using var wh = new HttpClient();
            wh.DefaultRequestHeaders.Add("User-Agent", "AgentCli/1.0");
            var html = await wh.GetStringAsync(url);
            return html.Length > 2000 ? html[..2000] + "\n...(truncated)" : html;
        });

    a.RegisterTool("memory_write",
        "Saves an important fact to long-term memory (MEMORY.md)",
        new { type = "object",
              properties = new
              {
                  section = new { type = "string" },
                  content = new { type = "string" }
              },
              required = new[] { "section", "content" } },
        async args2 =>
        {
            await memory.AppendMemoryAsync(
                args2.GetProperty("section").GetString()!,
                args2.GetProperty("content").GetString()!);
            return $"Saved to memory: [{args2.GetProperty("section").GetString()}]";
        });

    a.RegisterTool("memory_search", "Search memory for relevant past context",
        new { type = "object",
              properties = new { query = new { type = "string" } },
              required   = new[] { "query" } },
        async args2 =>
        {
            var results = await memory.SearchAsync(args2.GetProperty("query").GetString()!, 5);
            if (results.Count == 0) return "No memory found.";
            var sb2 = new StringBuilder();
            foreach (var r in results)
            {
                sb2.AppendLine($"[{Path.GetFileName(r.Path)}:{r.Line}] score={r.Score:F2}");
                sb2.AppendLine(r.Snippet);
                sb2.AppendLine();
            }
            return sb2.ToString().Trim();
        });

    a.RegisterTool("memory_read_all", "Read the full contents of MEMORY.md",
        new { type = "object", properties = new { } },
        async _ => await memory.ReadMemoryAsync() ?? "(empty)");

    a.RegisterTool("daily_note", "Append a note to today's daily log",
        new { type = "object",
              properties = new { content = new { type = "string" } },
              required   = new[] { "content" } },
        async args2 =>
        {
            await memory.AppendDailyAsync(args2.GetProperty("content").GetString()!);
            return $"Appended to daily note ({DateTime.Today:yyyy-MM-dd})";
        });

    a.RegisterTool("workflow_auto_read", "Read WORKFLOW_AUTO.md",
        new { type = "object", properties = new { } },
        async _ => await memory.ReadWorkflowAutoAsync() ?? "(not found)");

    a.RegisterTool("workflow_auto_write", "Overwrite WORKFLOW_AUTO.md",
        new { type = "object",
              properties = new { content = new { type = "string" } },
              required   = new[] { "content" } },
        async args2 =>
        {
            await memory.WriteWorkflowAutoAsync(args2.GetProperty("content").GetString()!);
            return "WORKFLOW_AUTO.md updated.";
        });
}

RegisterTools(agent);

// ─── Banner ───────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("AgentCli ready. Type your message (Ctrl+C to exit, 'exit' to quit)");
Console.WriteLine($"Workspace : {memory.WorkspaceDir ?? "(in-memory)"} [{memory.ProviderName}]");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"Provider  : {registry.Active.DisplayName} ({registry.Active.Id}) — model: {args.SkipWhile(a => a != "--model").Skip(1).FirstOrDefault() ?? registry.Active.DefaultModel}");

var startupLoaded = await memory.RunStartupReadsAsync();
if (startupLoaded.Count > 0)
{
    Console.ForegroundColor = ConsoleColor.DarkGray;
    Console.WriteLine($"Startup   : {string.Join(", ", startupLoaded.Select(f => f.RelativePath))}");
}

Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("Commands  : /memory  /soul  /daily  /workflow  /providers  /switch <id>");
Console.ResetColor();
Console.WriteLine();

// ─── REPL ─────────────────────────────────────────────────────────────────────
while (true)
{
    Console.ForegroundColor = ConsoleColor.White;
    Console.Write("You: ");
    Console.ResetColor();

    var input = Console.ReadLine()?.Trim();
    if (string.IsNullOrEmpty(input)) continue;
    if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

    // ── Built-in commands ──────────────────────────────────────────────────────

    if (input == "/memory")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(await memory.ReadMemoryAsync() ?? "(empty)");
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input == "/soul")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(await memory.ReadSoulAsync() ?? "(empty)");
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input == "/daily")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(await memory.ReadDailyAsync() ?? "(no daily note today)");
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input == "/workflow")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(await memory.ReadWorkflowAutoAsync() ?? "(not found)");
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input == "/providers")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine("Available providers:");
        foreach (var p in registry.All)
        {
            var active = p.Id == registry.ActiveId ? " ◀ active" : "";
            Console.WriteLine($"  {p.Id,-20} {p.DisplayName} (default model: {p.DefaultModel}){active}");
        }
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input.StartsWith("/switch "))
    {
        var newId = input["/switch ".Length..].Trim();
        try
        {
            registry.SetActive(newId);
            agent = BuildAgent();
            RegisterTools(agent);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Switched to: {registry.Active.DisplayName} ({registry.Active.Id})");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
        }
        Console.ResetColor(); Console.WriteLine(); continue;
    }

    // ── Agent turn ────────────────────────────────────────────────────────────
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"Agent [{registry.Active.Id}]: ");
    Console.ResetColor();

    try { await agent.RunAsync(input); }
    catch (Exception ex)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
    }

    Console.WriteLine();
}
