using Npgsql;

namespace AgentCli;

// ─── PostgresSoulProvider ─────────────────────────────────────────────────────

/// <summary>
/// Reads the agent soul from agentcli.souls.
/// Read-only at runtime — admin writes directly to DB or via migration.
///
/// Caches the soul in memory; re-checks version every CacheCheckInterval
/// so a soul update propagates to all agents within ~60s without restart.
/// </summary>
public sealed class PostgresSoulProvider : ISoulProvider, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeSpan         _cacheCheckInterval;

    // In-memory cache — avoids a DB round-trip on every turn
    private SoulConfig?    _cached;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public PostgresSoulProvider(
        string    connectionString,
        TimeSpan? cacheCheckInterval = null)
    {
        _dataSource         = NpgsqlDataSource.Create(connectionString);
        _cacheCheckInterval = cacheCheckInterval ?? TimeSpan.FromSeconds(60);
    }

    public async Task<SoulConfig?> GetAsync(
        string agentType, CancellationToken ct = default)
    {
        // Return cache if still fresh
        if (_cached?.AgentType == agentType
            && DateTimeOffset.UtcNow - _lastCheck < _cacheCheckInterval)
            return _cached;

        // Check if version changed
        var currentVersion = await GetVersionAsync(agentType, ct);
        if (_cached?.AgentType == agentType
            && _cached.Version == currentVersion)
        {
            _lastCheck = DateTimeOffset.UtcNow;
            return _cached;
        }

        // Load fresh
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_type, name, prompt, startup_reads, version, updated_at
            FROM agentcli.souls
            WHERE agent_type = $1
            """;
        cmd.Parameters.AddWithValue(agentType);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        _cached = new SoulConfig(
            AgentType:    reader.GetString(0),
            Name:         reader.GetString(1),
            Prompt:       reader.GetString(2),
            StartupReads: reader.IsDBNull(3)
                              ? []
                              : reader.GetFieldValue<string[]>(3),
            Version:      reader.GetInt32(4),
            UpdatedAt:    reader.GetFieldValue<DateTimeOffset>(5));

        _lastCheck = DateTimeOffset.UtcNow;
        return _cached;
    }

    public async Task<int> GetVersionAsync(
        string agentType, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT version FROM agentcli.souls WHERE agent_type = $1
            """;
        cmd.Parameters.AddWithValue(agentType);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result == null ? 0 : Convert.ToInt32(result);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}

// ─── PostgresAgentMemory ──────────────────────────────────────────────────────

/// <summary>
/// Shared world knowledge — facts written by admins, read by all agents.
/// Keyword search: matches query words against key + content + tags (case-insensitive).
/// For semantic/vector search, replace SearchAsync with pgvector later.
/// </summary>
public sealed class PostgresAgentMemory : IAgentMemory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresAgentMemory(string connectionString)
        => _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task<IReadOnlyList<AgentMemoryEntry>> SearchAsync(
        string query, int limit = 5, CancellationToken ct = default)
    {
        // Simple keyword search — each word in query must appear somewhere
        // Works well for explicit facts; upgrade to pgvector for semantic search later
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                         .Select(w => w.ToLowerInvariant())
                         .Distinct()
                         .ToArray();

        if (words.Length == 0) return [];

        // Build: WHERE lower(key||' '||content||' '||array_to_string(tags,' ')) LIKE %word%
        // for each word (AND between words)
        var conditions = words.Select((_, i) =>
            $"lower(key || ' ' || content || ' ' || array_to_string(tags, ' ')) LIKE '%' || lower(${i + 1}) || '%'");

        var where = string.Join(" AND ", conditions);

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT key, content, tags, created_at, updated_at
            FROM agentcli.agent_memory
            WHERE {where}
            ORDER BY updated_at DESC
            LIMIT {limit}
            """;

        foreach (var word in words)
            cmd.Parameters.AddWithValue(word);

        return await ReadEntriesAsync(cmd, ct);
    }

    public async Task<AgentMemoryEntry?> GetAsync(
        string key, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT key, content, tags, created_at, updated_at
            FROM agentcli.agent_memory
            WHERE key = $1
            """;
        cmd.Parameters.AddWithValue(key);

        var results = await ReadEntriesAsync(cmd, ct);
        return results.FirstOrDefault();
    }

    public async Task<IReadOnlyList<AgentMemoryEntry>> ListAsync(
        string? tag = null, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        if (tag != null)
        {
            cmd.CommandText = """
                SELECT key, content, tags, created_at, updated_at
                FROM agentcli.agent_memory
                WHERE $1 = ANY(tags)
                ORDER BY updated_at DESC
                """;
            cmd.Parameters.AddWithValue(tag);
        }
        else
        {
            cmd.CommandText = """
                SELECT key, content, tags, created_at, updated_at
                FROM agentcli.agent_memory
                ORDER BY updated_at DESC
                """;
        }

        return await ReadEntriesAsync(cmd, ct);
    }

    private static async Task<List<AgentMemoryEntry>> ReadEntriesAsync(
        NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<AgentMemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AgentMemoryEntry(
                Key:       reader.GetString(0),
                Content:   reader.GetString(1),
                Tags:      reader.GetFieldValue<string[]>(2),
                CreatedAt: reader.GetFieldValue<DateTimeOffset>(3),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return results;
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}

// ─── PostgresUserMemory ───────────────────────────────────────────────────────

/// <summary>
/// Per-user personal facts — hard-isolated by (user_id, channel).
/// Agent writes these during conversation on the user's behalf.
/// userId + channel is ALWAYS in the WHERE clause — no cross-user access possible.
/// </summary>
public sealed class PostgresUserMemory : IUserMemory, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresUserMemory(string connectionString)
        => _dataSource = NpgsqlDataSource.Create(connectionString);

    public async Task WriteAsync(
        string userId, string channel,
        string key, string value,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agentcli.user_memory (user_id, channel, key, value, updated_at)
            VALUES ($1, $2, $3, $4, NOW())
            ON CONFLICT (user_id, channel, key)
            DO UPDATE SET value = EXCLUDED.value, updated_at = NOW()
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(key);
        cmd.Parameters.AddWithValue(value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> GetAsync(
        string userId, string channel,
        string key, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT value FROM agentcli.user_memory
            WHERE user_id = $1 AND channel = $2 AND key = $3
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(key);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<IReadOnlyList<UserMemoryEntry>> GetAllAsync(
        string userId, string channel,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_id, channel, key, value, updated_at
            FROM agentcli.user_memory
            WHERE user_id = $1 AND channel = $2
            ORDER BY key ASC
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);

        var results = new List<UserMemoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new UserMemoryEntry(
                UserId:    reader.GetString(0),
                Channel:   reader.GetString(1),
                Key:       reader.GetString(2),
                Value:     reader.GetString(3),
                UpdatedAt: reader.GetFieldValue<DateTimeOffset>(4)));
        }
        return results;
    }

    public async Task DeleteAsync(
        string userId, string channel,
        string key, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM agentcli.user_memory
            WHERE user_id = $1 AND channel = $2 AND key = $3
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(key);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAllAsync(
        string userId, string channel,
        CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM agentcli.user_memory
            WHERE user_id = $1 AND channel = $2
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
