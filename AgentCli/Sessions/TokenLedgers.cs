using System.Collections.Concurrent;
using Npgsql;

namespace AgentCli;

// ─── InMemoryTokenLedger ──────────────────────────────────────────────────────

/// <summary>
/// In-memory token ledger. No persistence — resets on restart.
/// Use for: single-process CLI, tests, or as a fast cache layer.
/// </summary>
public sealed class InMemoryTokenLedger : ITokenLedger
{
    private sealed record Entry(
        string     UserId,
        string     Channel,
        TokenUsage Usage,
        string?    Model,
        string?    Provider,
        DateTime   RecordedAt   // UTC
    );

    private readonly ConcurrentBag<Entry> _entries = new();

    public Task RecordAsync(
        string     userId,
        string     channel,
        TokenUsage usage,
        string?    model    = null,
        string?    provider = null,
        CancellationToken ct = default)
    {
        _entries.Add(new Entry(userId, channel, usage, model, provider, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public Task<TokenUsage> GetTodayAsync(
        string userId, string channel,
        CancellationToken ct = default)
    {
        var today   = DateTime.UtcNow.Date;
        var usage   = _entries
            .Where(e => e.UserId == userId && e.Channel == channel
                     && e.RecordedAt.Date == today)
            .Aggregate(TokenUsage.Zero, (acc, e) => acc + e.Usage);
        return Task.FromResult(usage);
    }

    public Task<TokenUsage> GetWindowAsync(
        string userId, string channel,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var since = DateTime.UtcNow - window;
        var usage = _entries
            .Where(e => e.UserId == userId && e.Channel == channel
                     && e.RecordedAt >= since)
            .Aggregate(TokenUsage.Zero, (acc, e) => acc + e.Usage);
        return Task.FromResult(usage);
    }

    public Task<TokenUsage> GetTotalAsync(
        string userId, string channel,
        CancellationToken ct = default)
    {
        var usage = _entries
            .Where(e => e.UserId == userId && e.Channel == channel)
            .Aggregate(TokenUsage.Zero, (acc, e) => acc + e.Usage);
        return Task.FromResult(usage);
    }

    public Task<IReadOnlyList<UserTokenSummary>> GetAllUsersAsync(
        CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.Date;
        var result = _entries
            .GroupBy(e => (e.UserId, e.Channel))
            .Select(g =>
            {
                var total = g.Aggregate(TokenUsage.Zero, (acc, e) => acc + e.Usage);
                var todayUsage = g
                    .Where(e => e.RecordedAt.Date == today)
                    .Aggregate(TokenUsage.Zero, (acc, e) => acc + e.Usage);
                return new UserTokenSummary(g.Key.UserId, g.Key.Channel, total, todayUsage);
            })
            .OrderByDescending(s => s.Total.TotalTokens)
            .ToList();
        return Task.FromResult<IReadOnlyList<UserTokenSummary>>(result);
    }

    /// <summary>Total number of recorded entries (useful for tests).</summary>
    public int Count => _entries.Count;
}

// ─── PostgresTokenLedger ──────────────────────────────────────────────────────

/// <summary>
/// Postgres-backed token ledger. Durable, queryable, shared across cluster.
///
/// Table: agentcli.token_usage
/// Schema added to EnsureSchemaAsync via PostgresConversationStore.
///
/// Each row = one turn. Aggregated at query time (no separate summary table needed
/// for the scale we're targeting — add materialized views later if needed).
/// </summary>
public sealed class PostgresTokenLedger : ITokenLedger, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresTokenLedger(string connectionString)
        => _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task RecordAsync(
        string     userId,
        string     channel,
        TokenUsage usage,
        string?    model    = null,
        string?    provider = null,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agentcli.token_usage
                (user_id, channel, prompt_tokens, completion_tokens, total_tokens,
                 model, provider, recorded_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, NOW())
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(usage.PromptTokens);
        cmd.Parameters.AddWithValue(usage.CompletionTokens);
        cmd.Parameters.AddWithValue(usage.TotalTokens);
        cmd.Parameters.AddWithValue((object?)model    ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)provider ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<TokenUsage> GetTodayAsync(
        string userId, string channel,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(prompt_tokens),0),
                   COALESCE(SUM(completion_tokens),0),
                   COALESCE(SUM(total_tokens),0)
            FROM agentcli.token_usage
            WHERE user_id = $1
              AND channel = $2
              AND recorded_at >= CURRENT_DATE
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        return await ReadUsageAsync(cmd, ct);
    }

    public async Task<TokenUsage> GetWindowAsync(
        string userId, string channel,
        TimeSpan window,
        CancellationToken ct = default)
    {
        var since = DateTimeOffset.UtcNow - window;
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(prompt_tokens),0),
                   COALESCE(SUM(completion_tokens),0),
                   COALESCE(SUM(total_tokens),0)
            FROM agentcli.token_usage
            WHERE user_id = $1
              AND channel = $2
              AND recorded_at >= $3
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(since);
        return await ReadUsageAsync(cmd, ct);
    }

    public async Task<TokenUsage> GetTotalAsync(
        string userId, string channel,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(SUM(prompt_tokens),0),
                   COALESCE(SUM(completion_tokens),0),
                   COALESCE(SUM(total_tokens),0)
            FROM agentcli.token_usage
            WHERE user_id = $1 AND channel = $2
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        return await ReadUsageAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<UserTokenSummary>> GetAllUsersAsync(
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                user_id,
                channel,
                SUM(prompt_tokens)      AS total_prompt,
                SUM(completion_tokens)  AS total_completion,
                SUM(total_tokens)       AS total_total,
                SUM(CASE WHEN recorded_at >= CURRENT_DATE
                         THEN prompt_tokens     ELSE 0 END) AS today_prompt,
                SUM(CASE WHEN recorded_at >= CURRENT_DATE
                         THEN completion_tokens ELSE 0 END) AS today_completion,
                SUM(CASE WHEN recorded_at >= CURRENT_DATE
                         THEN total_tokens      ELSE 0 END) AS today_total
            FROM agentcli.token_usage
            GROUP BY user_id, channel
            ORDER BY total_total DESC
            """;

        var results = new List<UserTokenSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new UserTokenSummary(
                UserId:  reader.GetString(0),
                Channel: reader.GetString(1),
                Total: new TokenUsage(
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt32(4)),
                Today: new TokenUsage(
                    reader.GetInt32(5),
                    reader.GetInt32(6),
                    reader.GetInt32(7))));
        }
        return results;
    }

    private static async Task<TokenUsage> ReadUsageAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return TokenUsage.Zero;
        return new TokenUsage(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
