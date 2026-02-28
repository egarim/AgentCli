using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCli;

/// <summary>
/// Simple agentic loop: send message → stream response → execute tools → repeat.
/// </summary>
public class AgentLoop
{
    private readonly CopilotChatClient _client;
    private readonly List<ChatMessage> _history = new();

    private readonly Dictionary<string, (ToolDefinition Def, Func<JsonElement, Task<string>> Handler)>
        _tools = new();

    public AgentLoop(CopilotChatClient client, string systemPrompt)
    {
        _client = client;
        _history.Add(new ChatMessage("system", systemPrompt));
    }

    public void RegisterTool(
        string name,
        string description,
        object schema,
        Func<JsonElement, Task<string>> handler)
    {
        _tools[name] = (
            new ToolDefinition("function", new ToolFunctionDef(name, description, schema)),
            handler
        );
    }

    /// <summary>
    /// Run one user turn. Streams assistant text to console, handles tool calls,
    /// loops until the model returns a plain text response.
    /// </summary>
    public async Task<string> RunAsync(string userMessage, CancellationToken ct = default)
    {
        _history.Add(new ChatMessage("user", userMessage));

        while (true)
        {
            var tools = _tools.Values.Select(t => t.Def).ToList();
            var sb    = new StringBuilder();
            List<ToolCall>? pendingCalls = null;

            await foreach (var chunk in _client.StreamAsync(_history, tools, ct))
            {
                JsonNode? doc;
                try { doc = JsonNode.Parse(chunk); }
                catch { continue; }

                var delta = doc?["choices"]?[0]?["delta"];
                if (delta == null) continue;

                // Stream text to console
                if (delta["content"]?.GetValue<string>() is { } text)
                {
                    Console.Write(text);
                    sb.Append(text);
                }

                // Accumulate streaming tool call chunks
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
                                Name = fn?["name"]?.GetValue<string>()
                                       ?? existing.Function.Name,
                                Arguments = existing.Function.Arguments
                                            + (fn?["arguments"]?.GetValue<string>() ?? ""),
                            }
                        };
                    }
                }
            }

            // Add assistant message to history
            _history.Add(new ChatMessage("assistant", sb.ToString(), pendingCalls));

            // No tool calls → done
            if (pendingCalls == null || pendingCalls.Count == 0)
            {
                Console.WriteLine();
                return sb.ToString();
            }

            // Execute tools and feed results back
            Console.WriteLine();
            foreach (var call in pendingCalls)
            {
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.WriteLine($"[tool] {call.Function.Name}({call.Function.Arguments})");
                Console.ResetColor();

                string result;
                if (_tools.TryGetValue(call.Function.Name, out var entry))
                {
                    try
                    {
                        var args = JsonDocument.Parse(
                            string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments
                        ).RootElement;
                        result = await entry.Handler(args);
                    }
                    catch (Exception ex)
                    {
                        result = $"Error: {ex.Message}";
                    }
                }
                else
                {
                    result = $"Error: unknown tool '{call.Function.Name}'";
                }

                Console.ForegroundColor = ConsoleColor.DarkGreen;
                Console.WriteLine($"[tool result] {result[..Math.Min(result.Length, 120)]}");
                Console.ResetColor();

                _history.Add(new ChatMessage("tool", result, ToolCallId: call.Id));
            }
            // Loop — model sees tool results and continues
        }
    }
}
