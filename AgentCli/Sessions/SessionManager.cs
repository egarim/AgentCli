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
///
/// Each session gets its own AgentLoop instance (with its own history list).
/// SessionManager acts as the factory and cache for those loops.
///
/// Usage:
///   var manager = new SessionManager(store, agentFactory, options);
///   var response = await manager.RunAsync("telegram:direct:5932684607", userMessage);
/// </summary>
public class SessionManager : IDisposable
{
    private readonly IConversationStore  _store;
    private readonly Func<AgentLoop>     _agentFactory;
    private readonly CompactionOptions   _compaction;

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
        CompactionOptions?  compaction = null)
    {
        _store        = store;
        _agentFactory = agentFactory;
        _compaction   = compaction ?? new CompactionOptions();
    }

    // ─── Main API ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Send a user message in the given session and return the agent's response.
    /// Loads or resumes the session automatically.
    /// </summary>
    public async Task<string> RunAsync(
        string            sessionKey,
        string            userMessage,
        CancellationToken ct = default)
    {
        var state = await GetOrLoadAsync(sessionKey, ct);
        state.LastUsed = DateTimeOffset.UtcNow;

        // Persist user message first (crash-safe)
        var userMsg = new ChatMessage("user", userMessage);
        state.Session.Messages.Add(userMsg);
        await _store.AppendMessageAsync(sessionKey, userMsg, ct);

        // Run the loop (AgentLoop also appends to its internal _history)
        // We sync AgentLoop history from our session before running
        SyncLoopHistory(state);
        var response = await state.Loop.RunAsync(userMessage, ct);

        // Persist assistant (+ any tool) messages added during the turn
        await SyncNewMessagesAsync(state, ct);

        // Compact if needed
        await MaybeCompactAsync(state, ct);

        return response;
    }

    /// <summary>
    /// Load a session's metadata + history without running a turn.
    /// Useful for inspecting or preloading a session.
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

        // Replay stored history into the fresh loop
        // AgentLoop always starts with the system prompt already in _history[0]
        // We inject the stored user/assistant/tool messages after it
        foreach (var msg in session.Messages)
            loop.InjectMessage(msg);

        var state = new SessionState(session, loop);
        _active.TryAdd(sessionKey, state);
        return state;
    }

    // ─── History sync ─────────────────────────────────────────────────────────

    /// <summary>
    /// Sync our session's message list → AgentLoop before a turn.
    /// The loop may have drifted if we injected compaction summaries externally.
    /// </summary>
    private static void SyncLoopHistory(SessionState state)
    {
        // AgentLoop owns its history — we only need to ensure the user message
        // we're about to send isn't already there (we added it to session.Messages above
        // but haven't called loop.RunAsync yet, so nothing to sync here).
        // Full sync only happens on load (GetOrLoadAsync).
    }

    /// <summary>
    /// After a turn completes, sync new messages the loop appended back to our session
    /// and persist them.
    /// </summary>
    private async Task SyncNewMessagesAsync(SessionState state, CancellationToken ct)
    {
        // AgentLoop._history has the full history including the system prompt.
        // Our session.Messages has user+assistant+tool only.
        // The delta = messages added during this turn (assistant + any tool results).
        var loopMsgs     = state.Loop.ExportHistory();         // all messages including system
        var sessionCount = state.Session.Messages.Count;      // already includes the user msg we added
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

        // Build compaction prompt: system + all messages → ask for summary
        var compactionMessages = new List<ChatMessage>(history)
        {
            new("user", _compaction.CompactionPrompt)
        };

        // Run a one-shot completion (no tools, no loop)
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
            catch { /* skip malformed chunks */ }
        }

        var summary = sb.ToString().Trim();
        if (string.IsNullOrEmpty(summary)) return;

        // Replace history with summary message
        session.Messages.Clear();
        session.Messages.Add(new ChatMessage("assistant",
            $"[Conversation summary — compaction #{session.CompactionCount + 1}]\n\n{summary}"));
        session.CompactionCount++;
        session.UpdatedAt = DateTimeOffset.UtcNow;

        // Rebuild the loop's history from scratch
        state.Loop.ResetHistory(session.Messages);

        // Persist the compacted session
        await _store.SaveAsync(session, ct);
    }

    public void Dispose()
    {
        if (_store is IDisposable d) d.Dispose();
    }
}
