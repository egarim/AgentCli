using System.Text.Json.Serialization;

namespace AgentCli;

// ─── Memory types ─────────────────────────────────────────────────────────────

/// <summary>A single memory search hit.</summary>
public record MemorySearchResult(string Path, int Line, float Score, string Snippet);

/// <summary>A file loaded from WORKFLOW_AUTO.md on startup.</summary>
public record StartupFile(string RelativePath, string Content);

// ─── Chat types (shared across all providers) ─────────────────────────────────

public record ChatMessage(
    [property: JsonPropertyName("role")]         string         Role,
    [property: JsonPropertyName("content")]      string?        Content,
    [property: JsonPropertyName("tool_calls")]   List<ToolCall>? ToolCalls   = null,
    [property: JsonPropertyName("tool_call_id")] string?         ToolCallId  = null
);

public record ToolCall(
    [property: JsonPropertyName("id")]       string           Id,
    [property: JsonPropertyName("type")]     string           Type,
    [property: JsonPropertyName("function")] ToolCallFunction Function
);

public record ToolCallFunction(
    [property: JsonPropertyName("name")]      string Name,
    [property: JsonPropertyName("arguments")] string Arguments
);

public record ToolDefinition(
    [property: JsonPropertyName("type")]     string          Type,
    [property: JsonPropertyName("function")] ToolFunctionDef Function
);

public record ToolFunctionDef(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("parameters")]  object Parameters
);
