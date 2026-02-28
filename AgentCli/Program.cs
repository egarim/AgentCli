using System.Text.Json.Nodes;
using System.Text;
using AgentCli;

// ─── Config dirs ──────────────────────────────────────────────────────────────
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

// ─── Provider config ──────────────────────────────────────────────────────────
var providerCfg = new ProviderConfig();
await providerCfg.LoadAsync();

// ─── Permissions config ───────────────────────────────────────────────────────
var permCfg = new PermissionsConfig();
await permCfg.LoadAsync();
var gate = ToolGateFactory.FromConfig(permCfg);

// ─── Memory ───────────────────────────────────────────────────────────────────
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
    Console.WriteLine("Created SOUL.md");
}

if (!await memory.Provider.ExistsAsync(MemorySystem.KeyWorkflow))
{
    await memory.WriteWorkflowAutoAsync("""
        # WORKFLOW_AUTO.md
        - MEMORY.md
        - memory/YYYY-MM-DD.md
        """);
    Console.WriteLine("Created WORKFLOW_AUTO.md");
}

// ─── Skill providers (global) ─────────────────────────────────────────────────
var inProcess = new InProcessSkillProvider();
var fileSkills = new FileSkillProvider(); // ~/.agentcli/skills/
var skills = new CompositeSkillProvider(inProcess, fileSkills);

// Register built-in skills (ISkill classes)
inProcess.Register(new CoreSkill(memory));

// ─── Services ─────────────────────────────────────────────────────────────────
var http         = new HttpClient();
var deviceAuth   = new GitHubDeviceAuth(http);
var tokenService = new CopilotTokenService(http);

// ─── GitHub login ─────────────────────────────────────────────────────────────
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
        { FileName = device.VerificationUri, UseShellExecute = true }); } catch { }
    Console.Write("Waiting for authorization");
    githubToken = await deviceAuth.PollForAccessTokenAsync(device);
    Console.WriteLine(" ✓");
    await SaveGitHubTokenAsync(githubToken);
}

// ─── CLI flags ────────────────────────────────────────────────────────────────
string? cliProvider = args.SkipWhile(a => a != "--provider").Skip(1).FirstOrDefault();
string? cliModel    = args.SkipWhile(a => a != "--model").Skip(1).FirstOrDefault();

// ─── Provider registry ────────────────────────────────────────────────────────
var registry = ProviderRegistry.Build(
    http, copilotTokenService: tokenService,
    githubToken: githubToken, cfg: providerCfg, overrideProvider: cliProvider);

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

// ─── Agent factory ────────────────────────────────────────────────────────────
string ResolveModel() =>
    cliModel ?? providerCfg.Model(registry.ActiveId) ?? registry.Active.DefaultModel;

async Task<AgentLoop> BuildAgentAsync()
{
    var agent = new AgentLoop(registry.Active, systemPrompt, ResolveModel(), gate);

    // Load all skills and register their tools
    foreach (var skill in await skills.ListAsync())
        agent.RegisterSkill(skill);

    return agent;
}

var agent = await BuildAgentAsync();

// ─── Banner ───────────────────────────────────────────────────────────────────
Console.WriteLine();
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("AgentCli ready. Type your message or a command. 'exit' to quit.");
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine($"Workspace  : {memory.WorkspaceDir ?? "(in-memory)"} [{memory.ProviderName}]");
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"Provider   : {registry.Active.DisplayName} ({registry.Active.Id}) — {ResolveModel()}");
Console.ForegroundColor = ConsoleColor.DarkGray;
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine($"Gate       : {permCfg.Mode}  (allowed: {permCfg.Allowed.Count}, denied: {permCfg.Denied.Count})  [{permCfg.ConfigPath}]");
Console.ForegroundColor = ConsoleColor.DarkGray;

var loadedSkills = await skills.ListAsync();
Console.WriteLine($"Skills     : {loadedSkills.Count} loaded — {string.Join(", ", loadedSkills.Select(s => s.Manifest.Id))}");

var startupLoaded = await memory.RunStartupReadsAsync();
if (startupLoaded.Count > 0)
    Console.WriteLine($"Startup    : {string.Join(", ", startupLoaded.Select(f => f.RelativePath))}");

Console.WriteLine();
Console.WriteLine("  Providers : /providers  /switch <id>  /config set|get|unset|default|show");
Console.WriteLine("  Skills    : /skills  /skills reload");
Console.WriteLine("  Gate      : /permissions  /permissions mode <interactive|allowlist|allow-all>");
Console.WriteLine("              /permissions allow <tool>  /permissions deny <tool>  /permissions reset <tool>");
Console.WriteLine("  Memory    : /memory  /soul  /daily  /workflow");
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

    // ── Memory commands ───────────────────────────────────────────────────────
    if (input == "/memory")  { Print(await memory.ReadMemoryAsync()      ?? "(empty)"); continue; }
    if (input == "/soul")    { Print(await memory.ReadSoulAsync()         ?? "(empty)"); continue; }
    if (input == "/daily")   { Print(await memory.ReadDailyAsync()        ?? "(none)");  continue; }
    if (input == "/workflow"){ Print(await memory.ReadWorkflowAutoAsync() ?? "(none)");  continue; }

    // ── Provider commands ─────────────────────────────────────────────────────
    if (input == "/providers")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  {"ID",-22} {"Display",-22} {"Model",-30} Key");
        Console.WriteLine("  " + new string('─', 82));
        foreach (var p in registry.All)
        {
            var active   = p.Id == registry.ActiveId ? " ◀" : "";
            var cfgModel = providerCfg.Model(p.Id);
            var model    = cfgModel != null ? $"{p.DefaultModel}(cfg:{cfgModel})" : p.DefaultModel;
            var hasKey   = providerCfg.ApiKey(p.Id) != null ? "cfg" :
                           Env(p.Id.ToUpper().Replace("-", "_") + "_API_KEY") != null ? "env" : "—";
            Console.WriteLine($"  {p.Id,-22} {p.DisplayName,-22} {model,-30} {hasKey}{active}");
        }
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input.StartsWith("/switch "))
    {
        var newId = input["/switch ".Length..].Trim();
        try { registry.SetActive(newId); agent = await BuildAgentAsync();
              Print($"Switched to: {registry.Active.DisplayName} — {ResolveModel()}"); }
        catch (Exception ex) { PrintErr(ex.Message); }
        continue;
    }
    if (input == "/config show")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  File   : {providerCfg.ConfigPath}");
        Console.WriteLine($"  Default: {providerCfg.DefaultProvider ?? "(not set)"}");
        foreach (var kvp in providerCfg.AllProviders())
        {
            Console.WriteLine($"  [{kvp.Key}]");
            foreach (var prop in kvp.Value)
                Console.WriteLine($"    {prop.Key} = {(prop.Key == "apiKey" ? MaskKey(prop.Value?.ToString()) : prop.Value?.ToString())}");
        }
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input.StartsWith("/config set "))
    {
        var parts = input["/config set ".Length..].Split(' ', 3);
        if (parts.Length < 3) { PrintErr("Usage: /config set <provider> <key> <value>"); continue; }
        providerCfg.Set(parts[0], parts[1], parts[2]);
        await providerCfg.SaveAsync();
        registry = ProviderRegistry.Build(http, tokenService, githubToken, providerCfg, cliProvider);
        agent = await BuildAgentAsync();
        Print($"Set [{parts[0]}].{parts[1]} — saved.");
        continue;
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
        agent = await BuildAgentAsync();
        Print($"Removed [{parts[0]}].{parts[1]} — saved.");
        continue;
    }
    if (input.StartsWith("/config default "))
    {
        providerCfg.DefaultProvider = input["/config default ".Length..].Trim();
        await providerCfg.SaveAsync();
        Print($"Default → {providerCfg.DefaultProvider}");
        continue;
    }

    // ── Skill commands ────────────────────────────────────────────────────────
    if (input == "/skills")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        var list = await skills.ListAsync();
        if (list.Count == 0) { Console.WriteLine("  (no skills loaded)"); }
        else foreach (var s in list)
        {
            Console.WriteLine($"  [{s.Manifest.Id}] {s.Manifest.Description}");
            foreach (var t in s.Manifest.Tools)
                Console.WriteLine($"    • {t.Name} — {t.Description}");
        }
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input == "/skills reload")
    {
        agent = await BuildAgentAsync();
        var list = await skills.ListAsync();
        Print($"Reloaded {list.Count} skill(s): {string.Join(", ", list.Select(s => s.Manifest.Id))}");
        continue;
    }

    // ── Permission/gate commands ───────────────────────────────────────────────
    if (input == "/permissions")
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  File   : {permCfg.ConfigPath}");
        Console.WriteLine($"  Mode   : {permCfg.Mode}");
        Console.WriteLine($"  Allowed: {(permCfg.Allowed.Count == 0 ? "(none)" : string.Join(", ", permCfg.Allowed.OrderBy(x => x)))}");
        Console.WriteLine($"  Denied : {(permCfg.Denied.Count  == 0 ? "(none)" : string.Join(", ", permCfg.Denied.OrderBy(x => x)))}");
        Console.ResetColor(); Console.WriteLine(); continue;
    }
    if (input.StartsWith("/permissions mode "))
    {
        var mode = input["/permissions mode ".Length..].Trim();
        permCfg.Mode = mode switch { "allowlist" => GateMode.Allowlist, "allow-all" => GateMode.AllowAll, _ => GateMode.Interactive };
        await permCfg.SaveAsync();
        gate = ToolGateFactory.FromConfig(permCfg);
        agent = await BuildAgentAsync();
        Print($"Gate mode → {permCfg.Mode} (saved, agent rebuilt)");
        continue;
    }
    if (input.StartsWith("/permissions allow "))
    {
        var tool = input["/permissions allow ".Length..].Trim();
        permCfg.Allowed.Add(tool); permCfg.Denied.Remove(tool);
        await permCfg.SaveAsync();
        Print($"'{tool}' added to allowlist (saved)");
        continue;
    }
    if (input.StartsWith("/permissions deny "))
    {
        var tool = input["/permissions deny ".Length..].Trim();
        permCfg.Denied.Add(tool); permCfg.Allowed.Remove(tool);
        await permCfg.SaveAsync();
        Print($"'{tool}' added to deny list (saved)");
        continue;
    }
    if (input.StartsWith("/permissions reset "))
    {
        var tool = input["/permissions reset ".Length..].Trim();
        permCfg.Allowed.Remove(tool); permCfg.Denied.Remove(tool);
        await permCfg.SaveAsync();
        Print($"'{tool}' removed from both lists (will prompt next time in interactive mode)");
        continue;
    }

    // ── Agent turn ────────────────────────────────────────────────────────────
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.Write($"Agent [{registry.Active.Id}]: ");
    Console.ResetColor();

    try { await agent.RunAsync(input); }
    catch (Exception ex) { PrintErr(ex.Message); }
    Console.WriteLine();
}

// ─── Helpers ──────────────────────────────────────────────────────────────────
void Print(string text)
{ Console.ForegroundColor = ConsoleColor.DarkCyan; Console.WriteLine(text); Console.ResetColor(); Console.WriteLine(); }

void PrintErr(string text)
{ Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(text); Console.ResetColor(); Console.WriteLine(); }

string? MaskKey(string? key) => key == null ? null :
    key.Length <= 8 ? "****" : key[..4] + new string('*', Math.Min(key.Length - 8, 20)) + key[^4..];

string? Env(string k) => Environment.GetEnvironmentVariable(k)?.Trim() is { Length: > 0 } v ? v : null;
