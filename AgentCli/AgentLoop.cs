using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCli;

/// <summary>
/// Agentic loop: send message → stream response → gate + execute tools → repeat.
/// Provider-agnostic, gate-enforced, skill-backed.
/// </summary>
public class AgentLoop
{
    private readonly IAiProvider  _provider;
    private readonly string       _model;
    private readonly IToolGate    _gate;
    private readonly List<ChatMessage> _history = new();

    // name → (spec, handler)
    private readonly Dictionary<string, (ToolSpec Spec, Func<JsonElement, CancellationToken, Task<string>> Handler)>
        _tools = new(StringComparer.OrdinalIgnoreCase);

    public AgentLoop(IAiProvider provider, string systemPrompt, string? model = null, IToolGate? gate = null)
    {
        _provider = provider;
        _model    = model ?? provider.DefaultModel;
        _gate     = gate  ?? new AllowAllGate();
        _history.Add(new ChatMessage("system", systemPrompt));
    }

    // ─── Tool registration ────────────────────────────────────────────────────

    /// <summary>Register a tool with a cancellation-aware handler.</summary>
    public void RegisterTool(
        string name,
        string description,
        object schema,
        Func<JsonElement, CancellationToken, Task<string>> handler)
    {
        _tools[name] = (new ToolSpec(name, description, schema), handler);
    }

    /// <summary>Convenience overload — handler without CancellationToken.</summary>
    public void RegisterTool(
        string name,
        string description,
        object schema,
        Func<JsonElement, Task<string>> handler)
        => RegisterTool(name, description, schema, (a, _) => handler(a));

    /// <summary>Register all tools from a skill (respects gate at call time).</summary>
    public void RegisterSkill(ISkill skill)
    {
        foreach (var tool in skill.Manifest.Tools)
            RegisterTool(
                tool.Name,
                tool.Description,
                tool.Schema,
                (args, ct) => skill.InvokeAsync(tool.Name, args, ct));
    }

    /// <summary>Returns the ToolDefinition list for the model (schema serialised via JSON round-trip).</summary>
    public IReadOnlyList<ToolDefinition> ToolDefinitions =>
        _tools.Values
              .Select(t => new ToolDefinition("function",
                  new ToolFunctionDef(t.Spec.Name, t.Spec.Description, t.Spec.Schema)))
              .ToList();

    // ─── Main loop ────────────────────────────────────────────────────────────

    public async Task<string> RunAsync(string userMessage, CancellationToken ct = default)
    {
        _history.Add(new ChatMessage("user", userMessage));

        while (true)
        {
            var tools = ToolDefinitions.ToList();
            var sb    = new StringBuilder();
            List<ToolCall>? pendingCalls = null;

            await foreach (var chunk in _provider.StreamAsync(_history, _model, tools, ct))
            {
                JsonNode? doc;
                try { doc = JsonNode.Parse(chunk); }
                catch { continue; }

                var delta = doc?["choices"]?[0]?["delta"];
                if (delta == null) continue;

                if (delta["content"]?.GetValue<string>() is { } text)
                {
                    Console.Write(text);
                    sb.Append(text);
                }

                if (delta["tool_calls"] is JsonArray tcArray)
                {
                    pendingCalls ??= new();
                    foreach (var callChunk in tcArray)
                    {
                        var i = callChunk?["index"]?.GetValue<int>() ?? 0;
                        while (pendingCalls.Count <= i)
                            pendingCalls.Add(new ToolCall("", "function", new("", "")));

                        var fn       = callChunk?["function"];
                        var existing = pendingCalls[i];
                        pendingCalls[i] = existing with
                        {
                            Id = callChunk?["id"]?.GetValue<string>() ?? existing.Id,
                            Function = existing.Function with
                            {
                                Name = fn?["name"]?.GetValue<string>() ?? existing.Function.Name,
                                Arguments = existing.Function.Arguments
                                            + (fn?["arguments"]?.GetValue<string>() ?? ""),
                            }
                        };
                    }
                }
            }

            _history.Add(new ChatMessage("assistant", sb.ToString(), pendingCalls));

            if (pendingCalls == null || pendingCalls.Count == 0)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            Console.WriteLine();

            foreach (var call in pendingCalls)
            {
                var argsJson = string.IsNullOrWhiteSpace(call.Function.Arguments)
                    ? "{}" : call.Function.Arguments;

                JsonElement args;
                try   { args = JsonDocument.Parse(argsJson).RootElement; }
                catch { args = JsonDocument.Parse("{}").RootElement; }

                // ── Gate check ────────────────────────────────────────────────
                var permission = await _gate.RequestAsync(call.Function.Name, args, ct);

                if (!permission.IsAllowed)
                {
                    var reason = $"Permission denied: {call.Function.Name}" +
                                 (permission.Reason != null ? $" — {permission.Reason}" : "");
                    Console.ForegroundColor = ConsoleColor.DarkRed;
                    Console.WriteLine($"  [blocked] {reason}");
                    Console.ResetColor();
                    _history.Add(new ChatMessage("tool", reason, ToolCallId: call.Id));
                    continue;
                }

                // ── Execute ───────────────────────────────────────────────────
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"  [tool] {call.Function.Name}({Truncate(argsJson, 80)})");
                Console.ResetColor();

                string result;
                if (_tools.TryGetValue(call.Function.Name, out var entry))
                {
                    try   { result = await entry.Handler(args, ct); }
                    catch (Exception ex) { result = $"Error: {ex.Message}"; }
                }
                else
                {
                    result = $"Error: unknown tool '{call.Function.Name}'";
                }

                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"  [result] {Truncate(result, 120)}");
                Console.ResetColor();

                _history.Add(new ChatMessage("tool", result, ToolCallId: call.Id));
            }
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
