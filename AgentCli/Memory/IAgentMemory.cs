namespace AgentCli;

// ─── Soul ─────────────────────────────────────────────────────────────────────

/// <summary>
/// The agent's identity — name, personality, system prompt.
/// Read-only at runtime. Written only by admins via direct DB access or admin API.
/// Single soul: "sivar-ai".
/// Loaded once on startup, cached, injected into every turn's system prompt.
/// </summary>
public sealed record SoulConfig(
    string AgentType,   // "sivar-ai"
    string Name,        // "Sivar AI"
    string Prompt,      // the full SOUL.md content
    int    Version,     // incremented on every admin update
    DateTimeOffset UpdatedAt
);

public interface ISoulProvider
{
    /// <summary>Load the soul for a given agent type (e.g. "sivar-ai").</summary>
    Task<SoulConfig?> GetAsync(string agentType, CancellationToken ct = default);

    /// <summary>Current version number — use for cache invalidation.</summary>
    Task<int> GetVersionAsync(string agentType, CancellationToken ct = default);
}

// ─── Agent Memory ─────────────────────────────────────────────────────────────

/// <summary>
/// Shared world knowledge — facts, news, domain context.
/// Written only by admins. Read by ALL agents for ALL users.
/// No user isolation — this is global context for the agent.
/// </summary>
public sealed record AgentMemoryEntry(
    string          Key,       // e.g. "news:2026-02-28:bitcoin"
    string          Content,   // the actual text
    string[]        Tags,      // ["news", "finance", "crypto"]
    DateTimeOffset  CreatedAt,
    DateTimeOffset  UpdatedAt
);

public interface IAgentMemory
{
    /// <summary>Keyword/tag search — returns top N relevant entries.</summary>
    Task<IReadOnlyList<AgentMemoryEntry>> SearchAsync(
        string query, int limit = 5, CancellationToken ct = default);

    /// <summary>Direct key lookup.</summary>
    Task<AgentMemoryEntry?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>List all entries, optionally filtered by tag.</summary>
    Task<IReadOnlyList<AgentMemoryEntry>> ListAsync(
        string? tag = null, CancellationToken ct = default);
}

// ─── User Memory ──────────────────────────────────────────────────────────────

/// <summary>
/// Personal facts about a specific user — timezone, language, preferences.
/// Written by the agent during conversation (on the user's behalf).
/// Hard-isolated: userId + channel is ALWAYS in every read/write.
/// No user can ever access another user's memory.
/// </summary>
public sealed record UserMemoryEntry(
    string          UserId,
    string          Channel,
    string          Key,       // e.g. "timezone", "language", "prefers_bullets"
    string          Value,
    DateTimeOffset  UpdatedAt
);

public interface IUserMemory
{
    /// <summary>Write or overwrite a fact about a user.</summary>
    Task WriteAsync(
        string userId, string channel,
        string key, string value,
        CancellationToken ct = default);

    /// <summary>Read one fact. Returns null if not set.</summary>
    Task<string?> GetAsync(
        string userId, string channel,
        string key, CancellationToken ct = default);

    /// <summary>
    /// Read all known facts about a user.
    /// Used to inject user context into the system prompt.
    /// </summary>
    Task<IReadOnlyList<UserMemoryEntry>> GetAllAsync(
        string userId, string channel,
        CancellationToken ct = default);

    /// <summary>Delete one fact.</summary>
    Task DeleteAsync(
        string userId, string channel,
        string key, CancellationToken ct = default);

    /// <summary>
    /// Delete ALL facts about a user.
    /// GDPR right-to-forget — call when user requests data deletion.
    /// </summary>
    Task DeleteAllAsync(
        string userId, string channel,
        CancellationToken ct = default);
}
