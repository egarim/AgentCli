using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AgentCli;

// ─── Gate modes ───────────────────────────────────────────────────────────────

public enum GateMode
{
    [JsonPropertyName("interactive")] Interactive,
    [JsonPropertyName("allowlist")]   Allowlist,
    [JsonPropertyName("allow-all")]   AllowAll,
}

// ─── Interface ────────────────────────────────────────────────────────────────

/// <summary>
/// Permission gate — every tool call passes through before execution.
/// </summary>
public interface IToolGate
{
    Task<PermissionResult> RequestAsync(string toolName, JsonElement args, CancellationToken ct = default);
}

// ─── Persistent config ────────────────────────────────────────────────────────

/// <summary>
/// Loads/saves ~/.agentcli/permissions.json
///
/// {
///   "mode":    "interactive",
///   "allowed": ["get_time", "memory_write"],
///   "denied":  ["shell_exec"]
/// }
/// </summary>
public class PermissionsConfig
{
    private readonly string _path;

    public GateMode      Mode    { get; set; } = GateMode.Interactive;
    public HashSet<string> Allowed { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Denied  { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PermissionsConfig(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "permissions.json");
    }

    public string ConfigPath => _path;

    public async Task LoadAsync()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var doc = JsonDocument.Parse(await File.ReadAllTextAsync(_path)).RootElement;

            if (doc.TryGetProperty("mode", out var m))
                Mode = m.GetString() switch
                {
                    "allowlist"   => GateMode.Allowlist,
                    "allow-all"   => GateMode.AllowAll,
                    _             => GateMode.Interactive,
                };

            if (doc.TryGetProperty("allowed", out var a))
                foreach (var item in a.EnumerateArray())
                    if (item.GetString() is { } s) Allowed.Add(s);

            if (doc.TryGetProperty("denied", out var d))
                foreach (var item in d.EnumerateArray())
                    if (item.GetString() is { } s) Denied.Add(s);
        }
        catch { }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var obj = new JsonObject
        {
            ["mode"]    = Mode switch { GateMode.Allowlist => "allowlist", GateMode.AllowAll => "allow-all", _ => "interactive" },
            ["allowed"] = new JsonArray(Allowed.OrderBy(x => x).Select(x => JsonValue.Create(x)!).ToArray()),
            ["denied"]  = new JsonArray(Denied.OrderBy(x => x).Select(x => JsonValue.Create(x)!).ToArray()),
        };
        await File.WriteAllTextAsync(_path,
            obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

// ─── Implementations ──────────────────────────────────────────────────────────

/// <summary>Always allows everything. Use for dev/testing.</summary>
public class AllowAllGate : IToolGate
{
    public Task<PermissionResult> RequestAsync(string toolName, JsonElement args, CancellationToken ct = default)
        => Task.FromResult(PermissionResult.Allow());
}

/// <summary>
/// Only allows tools in the explicit allowlist.
/// Explicit deny list is checked first regardless of mode.
/// </summary>
public class AllowlistGate : IToolGate
{
    private readonly PermissionsConfig _cfg;

    public AllowlistGate(PermissionsConfig cfg) => _cfg = cfg;

    public Task<PermissionResult> RequestAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        if (_cfg.Denied.Contains(toolName))
            return Task.FromResult(PermissionResult.Deny($"'{toolName}' is explicitly denied"));

        return Task.FromResult(_cfg.Allowed.Contains(toolName)
            ? PermissionResult.Allow()
            : PermissionResult.Deny($"'{toolName}' is not in the allowlist"));
    }
}

/// <summary>
/// Prompts the user in the terminal before each unknown tool call.
/// "always" persists the answer to permissions.json.
/// Explicit deny list is checked first.
/// </summary>
public class InteractiveGate : IToolGate
{
    private readonly PermissionsConfig _cfg;

    public InteractiveGate(PermissionsConfig cfg) => _cfg = cfg;

    public async Task<PermissionResult> RequestAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        // Explicit deny wins always
        if (_cfg.Denied.Contains(toolName))
            return PermissionResult.Deny($"'{toolName}' is explicitly denied");

        // Already allowed
        if (_cfg.Allowed.Contains(toolName))
            return PermissionResult.Allow();

        // Ask
        var preview = args.ValueKind == JsonValueKind.Object
            ? args.ToString().Length > 80 ? args.ToString()[..80] + "…" : args.ToString()
            : "";

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"  [permission] Agent wants to run ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(toolName);
        if (!string.IsNullOrEmpty(preview))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"({preview})");
        }
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("  Allow? ");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write("[y]es / [n]o / [a]lways / [d]eny-always : ");
        Console.ResetColor();

        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        Console.WriteLine();

        switch (answer)
        {
            case "y" or "yes":
                return PermissionResult.Allow();

            case "a" or "always":
                _cfg.Allowed.Add(toolName);
                await _cfg.SaveAsync();
                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"  ✓ '{toolName}' added to allowlist (saved)");
                Console.ResetColor();
                return PermissionResult.Allow();

            case "d" or "deny-always":
                _cfg.Denied.Add(toolName);
                await _cfg.SaveAsync();
                Console.ForegroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($"  ✗ '{toolName}' added to deny list (saved)");
                Console.ResetColor();
                return PermissionResult.Deny($"'{toolName}' denied and added to deny list");

            default: // n / no / anything else
                return PermissionResult.Deny($"user denied '{toolName}'");
        }
    }
}

/// <summary>
/// Composite gate — checks gates in order, first non-Allow wins.
/// Use to chain: e.g. AllowlistGate first, then InteractiveGate for unknowns.
/// </summary>
public class CompositeGate : IToolGate
{
    private readonly IReadOnlyList<IToolGate> _gates;

    public CompositeGate(params IToolGate[] gates) => _gates = gates;

    public async Task<PermissionResult> RequestAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        foreach (var gate in _gates)
        {
            var result = await gate.RequestAsync(toolName, args, ct);
            if (!result.IsAllowed) return result;
        }
        return PermissionResult.Allow();
    }
}

// ─── Factory ──────────────────────────────────────────────────────────────────

public static class ToolGateFactory
{
    /// <summary>
    /// Build the correct gate from config.
    /// Interactive mode is the safe default — asks before running unknown tools,
    /// persists "always" answers, explicit deny list always enforced.
    /// </summary>
    public static IToolGate FromConfig(PermissionsConfig cfg) =>
        cfg.Mode switch
        {
            GateMode.AllowAll   => new AllowAllGate(),
            GateMode.Allowlist  => new AllowlistGate(cfg),
            _                   => new InteractiveGate(cfg),   // default: interactive
        };
}
