namespace AgentCli;

// ─── Token accounting ─────────────────────────────────────────────────────────

/// <summary>
/// Token counts from one model invocation.
/// Populated from the OpenAI-standard usage chunk at the end of the SSE stream.
/// Null values mean the provider didn't report that field.
/// </summary>
public sealed record TokenUsage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens
)
{
    public static TokenUsage Zero => new(0, 0, 0);

    public static TokenUsage operator +(TokenUsage a, TokenUsage b) =>
        new(a.PromptTokens     + b.PromptTokens,
            a.CompletionTokens + b.CompletionTokens,
            a.TotalTokens      + b.TotalTokens);
}

/// <summary>Result of one AgentLoop turn — the text response plus token usage.</summary>
public sealed record AgentResult(
    string     Text,
    TokenUsage Usage
);

// ─── Ledger ───────────────────────────────────────────────────────────────────

/// <summary>
/// Records and queries token usage per user+channel.
/// "User" here is the external user ID (e.g. Telegram user_id).
/// "Channel" matches the session key channel prefix (e.g. "telegram", "whatsapp").
/// </summary>
public interface ITokenLedger
{
    /// <summary>
    /// Record usage for a completed turn.
    /// </summary>
    Task RecordAsync(
        string     userId,
        string     channel,
        TokenUsage usage,
        string?    model    = null,
        string?    provider = null,
        CancellationToken ct = default);

    /// <summary>Cumulative usage for today (UTC) for a specific user.</summary>
    Task<TokenUsage> GetTodayAsync(
        string userId, string channel,
        CancellationToken ct = default);

    /// <summary>Cumulative usage for a rolling window (e.g. last 30 days).</summary>
    Task<TokenUsage> GetWindowAsync(
        string userId, string channel,
        TimeSpan window,
        CancellationToken ct = default);

    /// <summary>All-time cumulative usage for a user.</summary>
    Task<TokenUsage> GetTotalAsync(
        string userId, string channel,
        CancellationToken ct = default);

    /// <summary>All-time cumulative usage across all users (admin overview).</summary>
    Task<IReadOnlyList<UserTokenSummary>> GetAllUsersAsync(
        CancellationToken ct = default);
}

/// <summary>Per-user summary row for admin overview.</summary>
public sealed record UserTokenSummary(
    string     UserId,
    string     Channel,
    TokenUsage Total,
    TokenUsage Today
);

// ─── Policy ───────────────────────────────────────────────────────────────────

/// <summary>
/// Outcome of a policy check before a turn.
/// </summary>
public enum TokenPolicyOutcome { Allow, Warn, Deny }

public sealed record TokenPolicyResult(
    TokenPolicyOutcome Outcome,
    string?            Message = null  // user-facing message if Warn or Deny
)
{
    public bool IsAllowed => Outcome != TokenPolicyOutcome.Deny;

    public static TokenPolicyResult Allow()              => new(TokenPolicyOutcome.Allow);
    public static TokenPolicyResult Warn(string msg)     => new(TokenPolicyOutcome.Warn, msg);
    public static TokenPolicyResult Deny(string msg)     => new(TokenPolicyOutcome.Deny, msg);
}

/// <summary>
/// Controls how many tokens each user can consume.
/// Called by SessionManager before each turn.
/// </summary>
public interface ITokenPolicy
{
    /// <summary>
    /// Check whether the user is allowed to start a new turn.
    /// Called BEFORE the turn runs — use current ledger state to decide.
    /// </summary>
    Task<TokenPolicyResult> CheckAsync(
        string userId, string channel,
        ITokenLedger ledger,
        CancellationToken ct = default);
}

/// <summary>Limits defined per-user or as a global default.</summary>
public sealed class TokenLimits
{
    /// <summary>Max total tokens per calendar day (UTC). 0 = unlimited.</summary>
    public int DailyTotalTokens { get; init; } = 0;

    /// <summary>Warning threshold — Warn when daily usage exceeds this. 0 = no warning.</summary>
    public int DailyWarnAt { get; init; } = 0;

    /// <summary>Max all-time total tokens. 0 = unlimited.</summary>
    public long LifetimeTotalTokens { get; init; } = 0;

    public static TokenLimits Unlimited => new();
}

/// <summary>
/// Simple configurable policy: global defaults with optional per-user overrides.
/// </summary>
public sealed class ConfigurableTokenPolicy : ITokenPolicy
{
    private readonly TokenLimits _defaults;
    private readonly Dictionary<string, TokenLimits> _perUser;

    public ConfigurableTokenPolicy(
        TokenLimits?                          defaults  = null,
        Dictionary<string, TokenLimits>?      perUser   = null)
    {
        _defaults = defaults ?? TokenLimits.Unlimited;
        _perUser  = perUser  ?? new Dictionary<string, TokenLimits>(StringComparer.OrdinalIgnoreCase);
    }

    public void SetUserLimit(string userId, TokenLimits limits)
        => _perUser[userId] = limits;

    public async Task<TokenPolicyResult> CheckAsync(
        string userId, string channel,
        ITokenLedger ledger,
        CancellationToken ct = default)
    {
        var limits = _perUser.TryGetValue(userId, out var u) ? u : _defaults;

        if (limits.DailyTotalTokens == 0 && limits.DailyWarnAt == 0 && limits.LifetimeTotalTokens == 0)
            return TokenPolicyResult.Allow();

        var today    = await ledger.GetTodayAsync(userId, channel, ct);
        var lifetime = limits.LifetimeTotalTokens > 0
            ? await ledger.GetTotalAsync(userId, channel, ct)
            : TokenUsage.Zero;

        // Hard daily limit
        if (limits.DailyTotalTokens > 0 && today.TotalTokens >= limits.DailyTotalTokens)
            return TokenPolicyResult.Deny(
                $"Daily token limit reached ({today.TotalTokens:N0} / {limits.DailyTotalTokens:N0}). Try again tomorrow.");

        // Lifetime limit
        if (limits.LifetimeTotalTokens > 0 && lifetime.TotalTokens >= limits.LifetimeTotalTokens)
            return TokenPolicyResult.Deny(
                $"Lifetime token limit reached ({lifetime.TotalTokens:N0} / {limits.LifetimeTotalTokens:N0}).");

        // Soft warning
        if (limits.DailyWarnAt > 0 && today.TotalTokens >= limits.DailyWarnAt)
            return TokenPolicyResult.Warn(
                $"⚠️ You've used {today.TotalTokens:N0} tokens today (warning at {limits.DailyWarnAt:N0}).");

        return TokenPolicyResult.Allow();
    }
}
