using System.Text.Json.Nodes;

namespace AgentCli;

/// <summary>
/// Application entry point. Owns startup, the REPL loop, and command dispatch.
/// </summary>
public class App
{
    // ─── State ────────────────────────────────────────────────────────────────

    private readonly string[]         _args;
    private readonly HttpClient       _http;
    private readonly MemorySystem     _memory;
    private readonly ISkillProvider   _skills;
    private readonly ProviderConfig   _providerCfg;
    private readonly PermissionsConfig _permCfg;
    private readonly string           _configFile;

    private ProviderRegistry  _registry;
    private IToolGate         _gate;
    private AgentLoop         _agent = null!;

    private readonly string? _cliProvider;
    private readonly string? _cliModel;

    // ─── Entry point ──────────────────────────────────────────────────────────

    public static async Task RunAsync(string[] args)
    {
        var app = new App(args);
        await app.InitAsync();
        await app.RunReplAsync();
    }

    // ─── Constructor ──────────────────────────────────────────────────────────

    private App(string[] args)
    {
        _args = args;
        _http = new HttpClient();

        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agentcli");
        Directory.CreateDirectory(configDir);
        _configFile = Path.Combine(configDir, "config.json");

        _providerCfg = new ProviderConfig();
        _permCfg     = new PermissionsConfig();
        _memory      = MemorySystem.CreateFile();
        _gate        = new AllowAllGate(); // replaced in InitAsync

        _cliProvider = Flag("--provider");
        _cliModel    = Flag("--model");

        var inProcess  = new InProcessSkillProvider();
        var fileSkills = new FileSkillProvider();
        _skills = new CompositeSkillProvider(inProcess, fileSkills);

        inProcess.Register(new CoreSkill(_memory));
        inProcess.Register(new ShellSkill());

        // Placeholder — replaced after GitHub login
        _registry = new ProviderRegistry("github-copilot");
    }

    // ─── Initialisation ───────────────────────────────────────────────────────

    private async Task InitAsync()
    {
        await _providerCfg.LoadAsync();
        await _permCfg.LoadAsync();
        _gate = ToolGateFactory.FromConfig(_permCfg);

        await SeedDefaultFilesAsync();

        var githubToken = await LoadGitHubTokenAsync();
        if (githubToken == null || _args.Contains("--login"))
            githubToken = await RunGitHubLoginAsync();

        _registry = BuildRegistry(githubToken);
        _agent    = await BuildAgentAsync();

        await PrintBannerAsync();
    }

    private async Task SeedDefaultFilesAsync()
    {
        if (!await _memory.Provider.ExistsAsync(MemorySystem.KeySoul))
        {
            await _memory.WriteSoulAsync("""
                # SOUL.md
                You are a helpful, direct, and practical AI assistant running locally.
                You remember things the user tells you — write important facts to memory.
                Be concise. Lead with the answer. Don't pad responses.
                You have tools — use them when helpful, not speculatively.
                """);
            Console.WriteLine("Created SOUL.md");
        }

        if (!await _memory.Provider.ExistsAsync(MemorySystem.KeyWorkflow))
        {
            await _memory.WriteWorkflowAutoAsync("""
                # WORKFLOW_AUTO.md
                - MEMORY.md
                - memory/YYYY-MM-DD.md
                """);
            Console.WriteLine("Created WORKFLOW_AUTO.md");
        }
    }

    private async Task<string> RunGitHubLoginAsync()
    {
        var deviceAuth   = new GitHubDeviceAuth(_http);
        var tokenService = new CopilotTokenService(_http);

        Console.WriteLine("=== GitHub Copilot Login ===");
        var device = await deviceAuth.RequestDeviceCodeAsync();
        Console.WriteLine();
        Out(ConsoleColor.Cyan, $"  1. Open:  {device.VerificationUri}");
        Out(ConsoleColor.Cyan, $"  2. Enter: {device.UserCode}");
        Console.WriteLine();

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                { FileName = device.VerificationUri, UseShellExecute = true });
        }
        catch { }

        Console.Write("Waiting for authorization");
        var token = await deviceAuth.PollForAccessTokenAsync(device);
        Console.WriteLine(" ✓");
        await SaveGitHubTokenAsync(token);
        return token;
    }

    private ProviderRegistry BuildRegistry(string? githubToken)
    {
        var tokenService = new CopilotTokenService(_http);
        return ProviderRegistry.Build(
            _http,
            copilotTokenService: tokenService,
            githubToken:         githubToken,
            cfg:                 _providerCfg,
            overrideProvider:    _cliProvider);
    }

    private async Task<AgentLoop> BuildAgentAsync()
    {
        var context = await _memory.BuildContextAsync();
        var prompt  = string.IsNullOrWhiteSpace(context)
            ? "You are a helpful AI assistant."
            : $"""
              {context}

              ---
              You have access to tools. Use memory_write to remember important things the user tells you.
              Use memory_search to recall past conversations. Be direct and concise.
              """;

        var agent = new AgentLoop(_registry.Active, prompt, ResolveModel(), _gate);

        foreach (var skill in await _skills.ListAsync())
            agent.RegisterSkill(skill);

        return agent;
    }

    private async Task PrintBannerAsync()
    {
        Console.WriteLine();
        Out(ConsoleColor.Green,    "AgentCli ready. Type your message or a command. 'exit' to quit.");
        Out(ConsoleColor.DarkGray, $"Workspace  : {_memory.WorkspaceDir ?? "(in-memory)"} [{_memory.ProviderName}]");
        Out(ConsoleColor.Cyan,     $"Provider   : {_registry.Active.DisplayName} ({_registry.Active.Id}) — {ResolveModel()}");
        Out(ConsoleColor.Yellow,   $"Gate       : {_permCfg.Mode}  (allowed: {_permCfg.Allowed.Count}, denied: {_permCfg.Denied.Count})");

        var loadedSkills = await _skills.ListAsync();
        Out(ConsoleColor.DarkGray, $"Skills     : {loadedSkills.Count} — {string.Join(", ", loadedSkills.Select(s => s.Manifest.Id))}");

        var startupFiles = await _memory.RunStartupReadsAsync();
        if (startupFiles.Count > 0)
            Out(ConsoleColor.DarkGray, $"Startup    : {string.Join(", ", startupFiles.Select(f => f.RelativePath))}");

        Console.WriteLine();
        Out(ConsoleColor.DarkGray, "  Providers : /providers  /switch <id>  /config set|get|unset|default|show");
        Out(ConsoleColor.DarkGray, "  Skills    : /skills  /skills reload");
        Out(ConsoleColor.DarkGray, "  Gate      : /permissions  /permissions mode <interactive|allowlist|allow-all>");
        Out(ConsoleColor.DarkGray, "              /permissions allow <tool>  /permissions deny <tool>  /permissions reset <tool>");
        Out(ConsoleColor.DarkGray, "  Memory    : /memory  /soul  /daily  /workflow");
        Console.ResetColor();
        Console.WriteLine();
    }

    // ─── REPL ─────────────────────────────────────────────────────────────────

    private async Task RunReplAsync()
    {
        while (true)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("You: ");
            Console.ResetColor();

            var input = Console.ReadLine()?.Trim();
            if (string.IsNullOrEmpty(input)) continue;
            if (input.Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

            await DispatchAsync(input);
        }
    }

    // ─── Command dispatch ─────────────────────────────────────────────────────

    private async Task DispatchAsync(string input)
    {
        if (await TryMemoryCommandAsync(input))    return;
        if (await TryProviderCommandAsync(input))  return;
        if (await TryConfigCommandAsync(input))    return;
        if (await TrySkillCommandAsync(input))     return;
        if (await TryPermissionCommandAsync(input)) return;
        await RunAgentTurnAsync(input);
    }

    // ─── Memory commands ──────────────────────────────────────────────────────

    private async Task<bool> TryMemoryCommandAsync(string input)
    {
        switch (input)
        {
            case "/memory":   Print(await _memory.ReadMemoryAsync()      ?? "(empty)"); return true;
            case "/soul":     Print(await _memory.ReadSoulAsync()         ?? "(empty)"); return true;
            case "/daily":    Print(await _memory.ReadDailyAsync()        ?? "(none)");  return true;
            case "/workflow": Print(await _memory.ReadWorkflowAutoAsync() ?? "(none)");  return true;
        }
        return false;
    }

    // ─── Provider commands ────────────────────────────────────────────────────

    private async Task<bool> TryProviderCommandAsync(string input)
    {
        if (input == "/providers")
        {
            PrintProviders();
            return true;
        }

        if (input.StartsWith("/switch "))
        {
            await SwitchProviderAsync(input["/switch ".Length..].Trim());
            return true;
        }

        return false;
    }

    private void PrintProviders()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  {"ID",-22} {"Display",-22} {"Model",-30} Key");
        Console.WriteLine("  " + new string('─', 82));
        foreach (var p in _registry.All)
        {
            var active   = p.Id == _registry.ActiveId ? " ◀" : "";
            var cfgModel = _providerCfg.Model(p.Id);
            var model    = cfgModel != null ? $"{p.DefaultModel} (cfg: {cfgModel})" : p.DefaultModel;
            var hasKey   = _providerCfg.ApiKey(p.Id) != null ? "cfg"
                         : EnvVar(p.Id.ToUpper().Replace("-", "_") + "_API_KEY") != null ? "env"
                         : "—";
            Console.WriteLine($"  {p.Id,-22} {p.DisplayName,-22} {model,-30} {hasKey}{active}");
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    private async Task SwitchProviderAsync(string id)
    {
        try
        {
            _registry.SetActive(id);
            _agent = await BuildAgentAsync();
            Print($"Switched to: {_registry.Active.DisplayName} — {ResolveModel()}");
        }
        catch (Exception ex) { PrintErr(ex.Message); }
    }

    // ─── Config commands ──────────────────────────────────────────────────────

    private async Task<bool> TryConfigCommandAsync(string input)
    {
        if (input == "/config show")           { PrintConfig(); return true; }
        if (input.StartsWith("/config set "))  { await ConfigSetAsync(input["/config set ".Length..]); return true; }
        if (input.StartsWith("/config get "))  { ConfigGet(input["/config get ".Length..]);  return true; }
        if (input.StartsWith("/config unset ")) { await ConfigUnsetAsync(input["/config unset ".Length..]); return true; }
        if (input.StartsWith("/config default ")) { await ConfigDefaultAsync(input["/config default ".Length..]); return true; }
        return false;
    }

    private void PrintConfig()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  File   : {_providerCfg.ConfigPath}");
        Console.WriteLine($"  Default: {_providerCfg.DefaultProvider ?? "(not set)"}");
        foreach (var (id, section) in _providerCfg.AllProviders())
        {
            Console.WriteLine($"  [{id}]");
            foreach (var prop in section)
            {
                var val = prop.Key == "apiKey" ? MaskKey(prop.Value?.ToString()) : prop.Value?.ToString();
                Console.WriteLine($"    {prop.Key} = {val}");
            }
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    private async Task ConfigSetAsync(string args)
    {
        var parts = args.Split(' ', 3);
        if (parts.Length < 3) { PrintErr("Usage: /config set <provider> <key> <value>"); return; }
        _providerCfg.Set(parts[0], parts[1], parts[2]);
        await _providerCfg.SaveAsync();
        _registry = BuildRegistry(await LoadGitHubTokenAsync());
        _agent    = await BuildAgentAsync();
        Print($"Set [{parts[0]}].{parts[1]} — saved.");
    }

    private void ConfigGet(string args)
    {
        var parts = args.Split(' ', 2);
        if (parts.Length < 2) { PrintErr("Usage: /config get <provider> <key>"); return; }
        var val = _providerCfg.Get(parts[0], parts[1]);
        Print(val != null ? (parts[1] == "apiKey" ? MaskKey(val) : val) : "(not set)");
    }

    private async Task ConfigUnsetAsync(string args)
    {
        var parts = args.Split(' ', 2);
        if (parts.Length < 2) { PrintErr("Usage: /config unset <provider> <key>"); return; }
        _providerCfg.Set(parts[0], parts[1], null);
        await _providerCfg.SaveAsync();
        _registry = BuildRegistry(await LoadGitHubTokenAsync());
        _agent    = await BuildAgentAsync();
        Print($"Removed [{parts[0]}].{parts[1]} — saved.");
    }

    private async Task ConfigDefaultAsync(string provider)
    {
        _providerCfg.DefaultProvider = provider.Trim();
        await _providerCfg.SaveAsync();
        Print($"Default → {_providerCfg.DefaultProvider}");
    }

    // ─── Skill commands ───────────────────────────────────────────────────────

    private async Task<bool> TrySkillCommandAsync(string input)
    {
        if (input == "/skills")        { await PrintSkillsAsync(); return true; }
        if (input == "/skills reload") { await ReloadSkillsAsync(); return true; }
        return false;
    }

    private async Task PrintSkillsAsync()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        var list = await _skills.ListAsync();
        if (list.Count == 0)
        {
            Console.WriteLine("  (no skills loaded)");
        }
        else
        {
            foreach (var s in list)
            {
                Console.WriteLine($"  [{s.Manifest.Id}] {s.Manifest.Description}");
                foreach (var t in s.Manifest.Tools)
                    Console.WriteLine($"    • {t.Name} — {t.Description}");
            }
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    private async Task ReloadSkillsAsync()
    {
        _agent = await BuildAgentAsync();
        var list = await _skills.ListAsync();
        Print($"Reloaded {list.Count} skill(s): {string.Join(", ", list.Select(s => s.Manifest.Id))}");
    }

    // ─── Permission commands ──────────────────────────────────────────────────

    private async Task<bool> TryPermissionCommandAsync(string input)
    {
        if (input == "/permissions")                   { PrintPermissions(); return true; }
        if (input.StartsWith("/permissions mode "))    { await PermissionsModeAsync(input["/permissions mode ".Length..]); return true; }
        if (input.StartsWith("/permissions allow "))   { await PermissionsAllowAsync(input["/permissions allow ".Length..]); return true; }
        if (input.StartsWith("/permissions deny "))    { await PermissionsDenyAsync(input["/permissions deny ".Length..]); return true; }
        if (input.StartsWith("/permissions reset "))   { await PermissionsResetAsync(input["/permissions reset ".Length..]); return true; }
        return false;
    }

    private void PrintPermissions()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"  File   : {_permCfg.ConfigPath}");
        Console.WriteLine($"  Mode   : {_permCfg.Mode}");
        Console.WriteLine($"  Allowed: {(_permCfg.Allowed.Count == 0 ? "(none)" : string.Join(", ", _permCfg.Allowed.OrderBy(x => x)))}");
        Console.WriteLine($"  Denied : {(_permCfg.Denied.Count  == 0 ? "(none)" : string.Join(", ", _permCfg.Denied.OrderBy(x => x)))}");
        Console.ResetColor();
        Console.WriteLine();
    }

    private async Task PermissionsModeAsync(string mode)
    {
        _permCfg.Mode = mode.Trim() switch
        {
            "allowlist" => GateMode.Allowlist,
            "allow-all" => GateMode.AllowAll,
            _           => GateMode.Interactive,
        };
        await _permCfg.SaveAsync();
        _gate  = ToolGateFactory.FromConfig(_permCfg);
        _agent = await BuildAgentAsync();
        Print($"Gate mode → {_permCfg.Mode} (saved)");
    }

    private async Task PermissionsAllowAsync(string tool)
    {
        _permCfg.Allowed.Add(tool.Trim());
        _permCfg.Denied.Remove(tool.Trim());
        await _permCfg.SaveAsync();
        Print($"'{tool}' added to allowlist (saved)");
    }

    private async Task PermissionsDenyAsync(string tool)
    {
        _permCfg.Denied.Add(tool.Trim());
        _permCfg.Allowed.Remove(tool.Trim());
        await _permCfg.SaveAsync();
        Print($"'{tool}' added to deny list (saved)");
    }

    private async Task PermissionsResetAsync(string tool)
    {
        _permCfg.Allowed.Remove(tool.Trim());
        _permCfg.Denied.Remove(tool.Trim());
        await _permCfg.SaveAsync();
        Print($"'{tool}' removed from both lists");
    }

    // ─── Agent turn ───────────────────────────────────────────────────────────

    private async Task RunAgentTurnAsync(string input)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"Agent [{_registry.Active.Id}]: ");
        Console.ResetColor();

        try   { await _agent.RunAsync(input); }
        catch (Exception ex) { PrintErr(ex.Message); }

        Console.WriteLine();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private string ResolveModel() =>
        _cliModel ?? _providerCfg.Model(_registry.ActiveId) ?? _registry.Active.DefaultModel;

    private string? Flag(string name) =>
        _args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

    private async Task<string?> LoadGitHubTokenAsync()
    {
        if (!File.Exists(_configFile)) return null;
        try { return JsonNode.Parse(await File.ReadAllTextAsync(_configFile))?["github_token"]?.GetValue<string>(); }
        catch { return null; }
    }

    private async Task SaveGitHubTokenAsync(string token)
    {
        await File.WriteAllTextAsync(_configFile,
            new JsonObject { ["github_token"] = token }.ToJsonString());
        Console.WriteLine($"Token saved to {_configFile}");
    }

    private static void Print(string text)
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(text);
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintErr(string text)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(text);
        Console.ResetColor();
        Console.WriteLine();
    }

    private static void Out(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }

    private static string? MaskKey(string? key) =>
        key == null ? null
        : key.Length <= 8 ? "****"
        : key[..4] + new string('*', Math.Min(key.Length - 8, 20)) + key[^4..];

    private static string? EnvVar(string key) =>
        Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } v ? v : null;
}
