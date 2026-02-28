using System.Text.Json.Serialization;

namespace AgentCli;

// ─── Session key helpers ──────────────────────────────────────────────────────

/// <summary>
/// Canonical session key builder.
/// Format mirrors OpenClaw's buildAgentPeerSessionKey():
///   direct DM:  "{channel}:direct:{userId}"        e.g. "telegram:direct:5932684607"
///   group:       "{channel}:group:{groupId}"        e.g. "telegram:group:-1001234567890"
///   main/merged: "main"                             (all DMs into one session)
/// </summary>
public static class SessionKey
{
    public static string Direct(string channel, string userId)
        => $"{Norm(channel)}:direct:{Norm(userId)}";

    public static string Group(string channel, string groupId)
        => $"{Norm(channel)}:group:{Norm(groupId)}";

    public static string Channel(string channel, string channelId)
        => $"{Norm(channel)}:channel:{Norm(channelId)}";

    /// <summary>Merge all DMs into one global session (OpenClaw dmScope=main).</summary>
    public const string Main = "main";

    private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();
}

// ─── Session model ────────────────────────────────────────────────────────────

/// <summary>
/// A conversation session — the full message history plus metadata.
/// Keyed by a canonical session key (see SessionKey static class).
/// </summary>
public class ConversationSession
{
    [JsonPropertyName("sessionKey")]
    public string SessionKey { get; init; } = "";

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }

    [JsonPropertyName("userId")]
    public string? UserId { get; set; }

    [JsonPropertyName("chatType")]
    public string ChatType { get; set; } = "direct"; // direct | group | channel

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("compactionCount")]
    public int CompactionCount { get; set; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; init; } = new();
}

/// <summary>Lightweight summary for listing sessions without loading full history.</summary>
public record SessionSummary(
    string          SessionKey,
    string?         Channel,
    string?         UserId,
    string          ChatType,
    int             MessageCount,
    int             CompactionCount,
    DateTimeOffset  UpdatedAt
);

// ─── Interface ────────────────────────────────────────────────────────────────

/// <summary>
/// Pluggable conversation persistence layer.
///
/// Implementations:
///   FileConversationStore    — one JSONL per session (OpenClaw format)
///   SqliteConversationStore  — single SQLite DB, all sessions in one table
///   InMemoryConversationStore — ephemeral (tests / unit work)
///
/// Consumers (SessionManager) sit above this layer and own compaction/windowing.
/// </summary>
public interface IConversationStore
{
    /// <summary>Human-readable name for diagnostics (e.g. "file", "sqlite", "memory").</summary>
    string Name { get; }

    /// <summary>
    /// Load a session by key. Returns null if no session exists for that key.
    /// </summary>
    Task<ConversationSession?> LoadAsync(string sessionKey, CancellationToken ct = default);

    /// <summary>
    /// Persist a session (create or overwrite).
    /// Only the messages + metadata are written — callers mutate session.Messages directly.
    /// </summary>
    Task SaveAsync(ConversationSession session, CancellationToken ct = default);

    /// <summary>Append a single message to an existing session (more efficient than full save for JSONL).</summary>
    Task AppendMessageAsync(string sessionKey, ChatMessage message, CancellationToken ct = default);

    /// <summary>Delete a session and all its data.</summary>
    Task DeleteAsync(string sessionKey, CancellationToken ct = default);

    /// <summary>List all known sessions (summaries only — no message loading).</summary>
    Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct = default);

    /// <summary>True if a session exists for the given key.</summary>
    Task<bool> ExistsAsync(string sessionKey, CancellationToken ct = default);
}
