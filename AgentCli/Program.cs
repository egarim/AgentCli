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

// ─── Provider config (providers.json) ────────────────────────────────────────
var providerCfg = new ProviderConfig();
await providerCfg.LoadAsync();

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

// ─── CLI flag parsing ─────────────────────────────────────────────────────────
string? cliProvider = args.SkipWhile(a => a != "--provider").Skip(1).FirstOrDefault();
string? cliModel    = args.SkipWhile(a => a != "--model").Skip(1).FirstOrDefault();

// ─── Provider registry ────────────────────────────────────────────────────────
var registry = ProviderRegistry.Build(
    http,
    copilotTokenService: tokenService,
    githubToken:         githubToken,
    cfg:                 providerCfg,
    overrideProvider:    cliProvider
);

// ─── System prompt ────────────────────────────────────────────────────────────
var memoryContext = await memory.BuildContextAsync();
var systemPrompt  = string.IsNullOrWhiteSpace(memoryContext)
    ? "You are a helpful AI assistant."
    : $"""
      {memoryContext}

      ---
      You have tools. Use memory_write to remember important things the user tells you.
      Use memory_search to recall past conversations. Be direct and concise.
      """;

// ─── Agent factory ────────────────────────────────────────────────────────────
// Model priority: CLI flag > providers.json > provider default
string ResolveModel() =>
    cliModel
    ?? providerCfg.Model(registry.ActiveId)
    ?? registry.Active.DefaultModel;

AgentLoop BuildAgent() => new AgentLoop(registry.Active, systemPrompt, ResolveModel());

var agent = BuildAgent();

// ─── Tool registration ────────────────────────────────────────────────────────
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
              properties = new { section = new { type = "string" }, content = new { type = "string" } },
              required   = new[] { "section", "content" } },
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
                sb2.AppendLine(r.Snippet); sb2.AppendLine();
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
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"Workspace : {memory.WorkspaceDir ?? "(in-memory)"} [{memory.ProviderName}]");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"Provider  : {registry.Active.DisplayName} ({registry.Active.Id}) — model: {ResolveModel()}");
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"Config    : {providerCfg.ConfigPath}");

var startupLoaded = await memory.RunStartupReadsAsync();
if (startupLoaded.Count > 0)
    Console.WriteLine($"Startup   : {string.Join(", ", startupLoaded.Select(f => f.RelativePath))}");

Console.WriteLine("Commands  : /providers  /switch <id>  /config set <provider> <key> <value>");
Console.WriteLine("            /config get <provider> <key>  /config default <provider>  /config show");
Console.WriteLine("            /memory  /soul  /daily  /workflow");
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

    // ── Memory / soul / workflow commands ──────────────────────────────────────
    if (input == "/memory")
    {
        Print(await memory.ReadMemoryAsync() ?? "(empty)"); continue;
    }
    if (input == "/soul")
    {
        Print(await memory.ReadSoulAsync() ?? "(empty)"); continue;
    }
    if (input == "/daily")
    {
        Print(await memory.ReadDailyAsync() ?? "(no daily note today)"); continue;
    }
    if (input == "/workflow")
    {
        Print(await memory.ReadWorkflowAutoAsync() ?? "(not found)"); continue;
    }

    // ── Provider commands ──────────────────────────────────────────────────────
    if (input == "/providers")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"{"ID",-22} {"Display name",-22} {"Default model",-35} Config/Env");
        Console.WriteLine(new string('─', 100));
        foreach (var p in registry.All)
        {
            var active     = p.Id == registry.ActiveId ? " ◀" : "";
            var cfgModel   = providerCfg.Model(p.Id);
            var modelLabel = cfgModel != null ? $"{p.DefaultModel} (cfg: {cfgModel})" : p.DefaultModel;
            var hasKey     = providerCfg.ApiKey(p.Id) != null ? "cfg" :
                             Environment.GetEnvironmentVariable(p.Id.ToUpper().Replace("-","_") + "_API_KEY") != null ? "env" : "—";
            Console.WriteLine($"  {p.Id,-20} {p.DisplayName,-22} {modelLabel,-35} key={hasKey}{active}");
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
            Console.WriteLine($"Switched to: {registry.Active.DisplayName} — model: {ResolveModel()}");
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
        }
        Console.ResetColor(); Console.WriteLine(); continue;
    }

    // ── /config commands ───────────────────────────────────────────────────────
    if (input == "/config show")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"Config file: {providerCfg.ConfigPath}");
        Console.WriteLine($"Default provider: {providerCfg.DefaultProvider ?? "(not set)"}");
        foreach (var kvp in providerCfg.AllProviders())
        {
            Console.WriteLine($"  [{kvp.Key}]");
            foreach (var prop in kvp.Value)
            {
                // Mask keys
                var val = prop.Key == "apiKey"
                    ? MaskKey(prop.Value?.ToString())
                    : prop.Value?.ToString();
                Console.WriteLine($"    {prop.Key} = {val}");
            }
        }
        Console.ResetColor(); Console.WriteLine(); continue;
    }

    if (input.StartsWith("/config set "))
    {
        // /config set <provider> <key> <value>
        var parts = input["/config set ".Length..].Split(' ', 3);
        if (parts.Length < 3) { PrintErr("Usage: /config set <provider> <key> <value>"); continue; }
        providerCfg.Set(parts[0], parts[1], parts[2]);
        await providerCfg.SaveAsync();
        // Rebuild registry so new key takes effect immediately
        registry = ProviderRegistry.Build(http, tokenService, githubToken, providerCfg, cliProvider);
        agent = BuildAgent(); RegisterTools(agent);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Set [{parts[0]}].{parts[1]} — saved. Registry rebuilt.");
        Console.ResetColor(); Console.WriteLine(); continue;
    }

    if (input.StartsWith("/config get "))
    {
        var parts = input["/config get ".Length..].Split(' ', 2);
        if (parts.Length < 2) { PrintErr("Usage: /config get <provider> <key>"); continue; }
        var val = providerCfg.Get(parts[0], parts[1]);
        Print(val != null ? (parts[1] == "apiKey" ? MaskKey(val) : val) : "(not set)");
        continue;
    }

    if (input.StartsWith("/config unset "))
    {
        var parts = input["/config unset ".Length..].Split(' ', 2);
        if (parts.Length < 2) { PrintErr("Usage: /config unset <provider> <key>"); continue; }
        providerCfg.Set(parts[0], parts[1], null);
        await providerCfg.SaveAsync();
        registry = ProviderRegistry.Build(http, tokenService, githubToken, providerCfg, cliProvider);
        agent = BuildAgent(); RegisterTools(agent);
        Print($"Removed [{parts[0]}].{parts[1]} — saved.");
        continue;
    }

    if (input.StartsWith("/config default "))
    {
        var newDefault = input["/config default ".Length..].Trim();
        providerCfg.DefaultProvider = newDefault;
        await providerCfg.SaveAsync();
        Print($"Default provider set to '{newDefault}' in {providerCfg.ConfigPath}");
        continue;
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

// ─── Helpers ──────────────────────────────────────────────────────────────────
void Print(string text)
{
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine(text);
    Console.ResetColor();
    Console.WriteLine();
}

void PrintErr(string text)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine(text);
    Console.ResetColor();
    Console.WriteLine();
}

string? MaskKey(string? key) => key == null ? null :
    key.Length <= 8 ? "****" : key[..4] + new string('*', Math.Min(key.Length - 8, 20)) + key[^4..];
