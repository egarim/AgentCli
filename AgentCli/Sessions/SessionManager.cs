using System.Collections.Concurrent;

namespace AgentCli;

// ─── Compaction strategy ──────────────────────────────────────────────────────

/// <summary>
/// Controls when and how the conversation history is compacted.
/// </summary>
public class CompactionOptions
{
    /// <summary>
    /// Maximum number of messages (user+assistant pairs) before compaction triggers.
    /// 0 = disabled.
    /// </summary>
    public int MaxMessages { get; init; } = 100;

    /// <summary>
    /// Approximate character count at which compaction triggers (rough token proxy).
    /// 0 = disabled.
    /// </summary>
    public int MaxChars { get; init; } = 80_000;

    /// <summary>
    /// Prompt sent to the model when compacting.
    /// The full conversation history is appended after this.
    /// </summary>
    public string CompactionPrompt { get; init; } =
        "Summarize the conversation so far in a compact paragraph. " +
        "Preserve all key facts, decisions, and context. " +
        "Write it in third person as 'The user...' / 'The assistant...'. " +
        "Be concise but complete.";

    /// <summary>Disabled — no compaction ever.</summary>
    public static CompactionOptions Disabled => new() { MaxMessages = 0, MaxChars = 0 };
}

// ─── SessionManager ───────────────────────────────────────────────────────────

/// <summary>
/// Manages per-session conversation context on top of an IConversationStore.
///
/// Responsibilities:
///   - Load / create sessions by key (channel + userId)
///   - Maintain a per-session AgentLoop with the right message history
///   - Persist new messages after each turn
///   - Compact when history exceeds configured limits
///   - Enforce token policy (check before turn, record after)
///
/// Each session gets its own AgentLoop instance (with its own history list).
/// SessionManager acts as the factory and cache for those loops.
/// </summary>
public class SessionManager : IDisposable
{
    private readonly IConversationStore  _store;
    private readonly Func<AgentLoop>     _agentFactory;
    private readonly CompactionOptions   _compaction;
    private readonly ITokenLedger?       _ledger;
    private readonly ITokenPolicy?       _policy;

    // Active loops keyed by sessionKey
    private readonly ConcurrentDictionary<string, SessionState> _active = new(StringComparer.OrdinalIgnoreCase);

    private sealed class SessionState
    {
        public ConversationSession Session { get; }
        public AgentLoop           Loop    { get; }
        public DateTimeOffset      LastUsed { get; set; } = DateTimeOffset.UtcNow;

        public SessionState(ConversationSession session, AgentLoop loop)
        {
            Session = session;
            Loop    = loop;
        }
    }

    public SessionManager(
        IConversationStore  store,
        Func<AgentLoop>     agentFactory,
        CompactionOptions?  compaction = null,
        ITokenLedger?       ledger     = null,
        ITokenPolicy?       policy     = null)
    {
        _store        = store;
        _agentFactory = agentFactory;
        _compaction   = compaction ?? new CompactionOptions();
        _ledger       = ledger;
        _policy       = policy;
    }

    // ─── Main API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Send a user message in the given session and return the agent's response.
    /// Loads or resumes the session automatically.
    ///
    /// Returns null if the token policy denied the turn — caller should deliver
    /// the denial message from the TokenPolicyResult to the user.
    /// </summary>
    public async Task<SessionTurnResult> RunAsync(
        string            sessionKey,
        string            userMessage,
        string?           userId  = null,
        string?           channel = null,
        CancellationToken ct      = default)
    {
        // Derive userId/channel from sessionKey if not supplied
        userId  ??= ExtractUserId(sessionKey);
        channel ??= ExtractChannel(sessionKey);

        // ── Policy check ──────────────────────────────────────────────────────
        if (_policy != null && _ledger != null && userId != null && channel != null)
        {
            var check = await _policy.CheckAsync(userId, channel, _ledger, ct);
            if (!check.IsAllowed)
                return new SessionTurnResult(null, TokenUsage.Zero, check);

            // Warn: surface to caller but continue
            if (check.Outcome == TokenPolicyOutcome.Warn)
            {
                var state2 = await GetOrLoadAsync(sessionKey, ct);
                state2.LastUsed = DateTimeOffset.UtcNow;
                var userMsg2 = new ChatMessage("user", userMessage);
                state2.Session.Messages.Add(userMsg2);
                await _store.AppendMessageAsync(sessionKey, userMsg2, ct);
                SyncLoopHistory(state2);
                var result2 = await state2.Loop.RunAsync(userMessage, ct);
                await SyncNewMessagesAsync(state2, ct);
                await MaybeCompactAsync(state2, ct);
                if (_ledger != null && userId != null && channel != null)
                    await _ledger.RecordAsync(userId, channel, result2.Usage,
                        state2.Loop.Model, state2.Loop.Provider.Id, ct);
                return new SessionTurnResult(result2.Text, result2.Usage, check);
            }
        }

        // ── Normal turn ───────────────────────────────────────────────────────
        var state = await GetOrLoadAsync(sessionKey, ct);
        state.LastUsed = DateTimeOffset.UtcNow;

        var userMsg = new ChatMessage("user", userMessage);
        state.Session.Messages.Add(userMsg);
        await _store.AppendMessageAsync(sessionKey, userMsg, ct);

        SyncLoopHistory(state);
        var agentResult = await state.Loop.RunAsync(userMessage, ct);

        await SyncNewMessagesAsync(state, ct);
        await MaybeCompactAsync(state, ct);

        // ── Record usage ──────────────────────────────────────────────────────
        if (_ledger != null && userId != null && channel != null)
            await _ledger.RecordAsync(userId, channel, agentResult.Usage,
                state.Loop.Model, state.Loop.Provider.Id, ct);

        return new SessionTurnResult(agentResult.Text, agentResult.Usage, TokenPolicyResult.Allow());
    }

    /// <summary>
    /// Load a session's metadata + history without running a turn.
    /// </summary>
    public async Task<ConversationSession> GetSessionAsync(string sessionKey, CancellationToken ct = default)
        => (await GetOrLoadAsync(sessionKey, ct)).Session;

    /// <summary>Delete a session from the store and evict from the active cache.</summary>
    public async Task DeleteSessionAsync(string sessionKey, CancellationToken ct = default)
    {
        _active.TryRemove(sessionKey, out _);
        await _store.DeleteAsync(sessionKey, ct);
    }

    /// <summary>List all known sessions (from the backing store).</summary>
    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct = default)
        => _store.ListAsync(ct);

    /// <summary>Evict idle sessions from the in-memory cache after the given idle time.</summary>
    public void EvictIdle(TimeSpan idleTimeout)
    {
        var cutoff = DateTimeOffset.UtcNow - idleTimeout;
        foreach (var (key, state) in _active)
            if (state.LastUsed < cutoff)
                _active.TryRemove(key, out _);
    }

    // ─── Load / cache ─────────────────────────────────────────────────────────

    private async Task<SessionState> GetOrLoadAsync(string sessionKey, CancellationToken ct)
    {
        if (_active.TryGetValue(sessionKey, out var existing))
            return existing;

        var session = await _store.LoadAsync(sessionKey, ct)
                      ?? new ConversationSession { SessionKey = sessionKey };

        var loop = _agentFactory();

        foreach (var msg in session.Messages)
            loop.InjectMessage(msg);

        var state = new SessionState(session, loop);
        _active.TryAdd(sessionKey, state);
        return state;
    }

    // ─── History sync ─────────────────────────────────────────────────────────

    private static void SyncLoopHistory(SessionState state)
    {
        // No-op: full sync only happens on load (GetOrLoadAsync).
    }

    private async Task SyncNewMessagesAsync(SessionState state, CancellationToken ct)
    {
        var loopMsgs         = state.Loop.ExportHistory();
        var sessionCount     = state.Session.Messages.Count;
        var loopUserAsstMsgs = loopMsgs.Where(m => m.Role != "system").ToList();

        for (int i = sessionCount; i < loopUserAsstMsgs.Count; i++)
        {
            var msg = loopUserAsstMsgs[i];
            state.Session.Messages.Add(msg);
            await _store.AppendMessageAsync(state.Session.SessionKey, msg, ct);
        }

        state.Session.UpdatedAt = DateTimeOffset.UtcNow;
    }

    // ─── Compaction ───────────────────────────────────────────────────────────

    private async Task MaybeCompactAsync(SessionState state, CancellationToken ct)
    {
        if (_compaction.MaxMessages == 0 && _compaction.MaxChars == 0) return;

        var msgs    = state.Session.Messages;
        var charLen = msgs.Sum(m => m.Content?.Length ?? 0);

        bool shouldCompact =
            (_compaction.MaxMessages > 0 && msgs.Count > _compaction.MaxMessages) ||
            (_compaction.MaxChars    > 0 && charLen    > _compaction.MaxChars);

        if (!shouldCompact) return;

        await CompactAsync(state, ct);
    }

    private async Task CompactAsync(SessionState state, CancellationToken ct)
    {
        var session = state.Session;
        var history = state.Loop.ExportHistory();

        var compactionMessages = new List<ChatMessage>(history)
        {
            new("user", _compaction.CompactionPrompt)
        };

        var sb = new System.Text.StringBuilder();
        await foreach (var chunk in state.Loop.Provider.StreamAsync(
            compactionMessages, state.Loop.Model, tools: null, ct))
        {
            try
            {
                var doc   = System.Text.Json.JsonDocument.Parse(chunk);
                var text  = doc.RootElement
                               .GetProperty("choices")[0]
                               .GetProperty("delta")
                               .GetProperty("content")
                               .GetString();
                if (text != null) sb.Append(text);
            }
            catch { }
        }

        var summary = sb.ToString().Trim();
        if (string.IsNullOrEmpty(summary)) return;

        session.Messages.Clear();
        session.Messages.Add(new ChatMessage("assistant",
            $"[Conversation summary — compaction #{session.CompactionCount + 1}]\n\n{summary}"));
        session.CompactionCount++;
        session.UpdatedAt = DateTimeOffset.UtcNow;

        state.Loop.ResetHistory(session.Messages);
        await _store.SaveAsync(session, ct);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Extract userId from session key (format: "channel:type:userId" or "channel:userId").
    /// Returns null if key doesn't follow that pattern.
    /// </summary>
    private static string? ExtractUserId(string sessionKey)
    {
        var parts = sessionKey.Split(':');
        return parts.Length >= 3 ? parts[2] : null;
    }

    /// <summary>Extract channel from session key (first segment before ':').</summary>
    private static string? ExtractChannel(string sessionKey)
    {
        var idx = sessionKey.IndexOf(':');
        return idx > 0 ? sessionKey[..idx] : null;
    }

    public void Dispose()
    {
        if (_store is IDisposable d) d.Dispose();
    }
}

// ─── Turn result ──────────────────────────────────────────────────────────────

/// <summary>
/// Result of one SessionManager.RunAsync call.
/// Text is null if the turn was denied by policy.
/// </summary>
public sealed record SessionTurnResult(
    string?            Text,
    TokenUsage         Usage,
    TokenPolicyResult  PolicyResult
)
{
    public bool WasDenied => !PolicyResult.IsAllowed;
    public bool HasWarning => PolicyResult.Outcome == TokenPolicyOutcome.Warn;
}


