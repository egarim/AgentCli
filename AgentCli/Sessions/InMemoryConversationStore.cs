using System.Collections.Concurrent;

namespace AgentCli;

/// <summary>
/// In-memory conversation store.
/// No persistence — data lives only for the lifetime of the process.
///
/// Use cases:
///   - Unit tests
///   - Ephemeral CLI sessions (no history needed)
///   - Integration tests against SessionManager
/// </summary>
public class InMemoryConversationStore : IConversationStore
{
    private readonly ConcurrentDictionary<string, ConversationSession> _sessions = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "memory";

    public Task<ConversationSession?> LoadAsync(string sessionKey, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionKey, out var session);
        return Task.FromResult(session);
    }

    public Task SaveAsync(ConversationSession session, CancellationToken ct = default)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        _sessions[session.SessionKey] = session;
        return Task.CompletedTask;
    }

    public Task AppendMessageAsync(string sessionKey, ChatMessage message, CancellationToken ct = default)
    {
        var session = _sessions.GetOrAdd(sessionKey, k => new ConversationSession
        {
            SessionKey = k,
            CreatedAt  = DateTimeOffset.UtcNow,
            UpdatedAt  = DateTimeOffset.UtcNow,
        });
        lock (session.Messages) session.Messages.Add(message);
        session.UpdatedAt = DateTimeOffset.UtcNow;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string sessionKey, CancellationToken ct = default)
    {
        _sessions.TryRemove(sessionKey, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<SessionSummary> result = _sessions.Values
            .Select(s => new SessionSummary(
                s.SessionKey, s.Channel, s.UserId, s.ChatType,
                s.Messages.Count, s.CompactionCount, s.UpdatedAt))
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<bool> ExistsAsync(string sessionKey, CancellationToken ct = default)
        => Task.FromResult(_sessions.ContainsKey(sessionKey));

    /// <summary>Clear all sessions (useful between tests).</summary>
    public void Clear() => _sessions.Clear();
}
