using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace AgentCli;

/// <summary>
/// PostgreSQL-backed conversation store — the shared backend for stateless agent clusters.
///
/// All agents on all servers point at the same Postgres DB.
/// Any agent can handle any user at any time — no ownership table needed.
///
/// Schema lives in schema "agentcli" to stay isolated from other apps.
/// Tables auto-created on first connection (EnsureSchemaAsync).
///
/// Connection string (NpgsqlConnectionStringBuilder):
///   Host=86.48.30.121;Port=5432;Database=agentcli;Username=postgres;Password=...
///
/// Usage:
///   var store = await PostgresConversationStore.CreateAsync(connectionString);
///   var manager = new SessionManager(store, agentLoop, CompactionOptions.Default);
/// </summary>
public class PostgresConversationStore : IConversationStore, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Name => "postgres";

    // ─── Factory ──────────────────────────────────────────────────────────────

    private PostgresConversationStore(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    /// <summary>
    /// Create and initialize the store — runs schema migration on first call.
    /// Safe to call on every startup (idempotent).
    /// </summary>
    public static async Task<PostgresConversationStore> CreateAsync(
        string connectionString,
        CancellationToken ct = default)
    {
        var dataSource = NpgsqlDataSource.Create(connectionString);
        var store      = new PostgresConversationStore(dataSource);
        await store.EnsureSchemaAsync(ct);
        return store;
    }

    // ─── IConversationStore ───────────────────────────────────────────────────

    public async Task<ConversationSession?> LoadAsync(
        string sessionKey, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Load session metadata
        ConversationSession? session = null;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT channel, user_id, chat_type, created_at, updated_at, compaction_count
                FROM agentcli.sessions
                WHERE session_key = $1
                """;
            cmd.Parameters.AddWithValue(sessionKey);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;

            session = new ConversationSession
            {
                SessionKey      = sessionKey,
                Channel         = reader.IsDBNull(0) ? null : reader.GetString(0),
                UserId          = reader.IsDBNull(1) ? null : reader.GetString(1),
                ChatType        = reader.GetString(2),
                CreatedAt       = reader.GetFieldValue<DateTimeOffset>(3),
                UpdatedAt       = reader.GetFieldValue<DateTimeOffset>(4),
                CompactionCount = reader.GetInt32(5),
            };
        }

        // Load messages
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT role, content, tool_calls, tool_call_id
                FROM agentcli.messages
                WHERE session_key = $1
                ORDER BY position ASC
                """;
            cmd.Parameters.AddWithValue(sessionKey);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var role        = reader.GetString(0);
                var content     = reader.IsDBNull(1) ? null : reader.GetString(1);
                var toolCallId  = reader.IsDBNull(3) ? null : reader.GetString(3);
                List<ToolCall>? toolCalls = null;
                if (!reader.IsDBNull(2))
                    toolCalls = JsonSerializer.Deserialize<List<ToolCall>>(
                        reader.GetString(2), JsonOpts);
                session.Messages.Add(new ChatMessage(role, content, toolCalls, toolCallId));
            }
        }

        return session;
    }

    public async Task SaveAsync(ConversationSession session, CancellationToken ct = default)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);
        try
        {
            await UpsertSessionAsync(conn, session, ct);
            await DeleteMessagesAsync(conn, session.SessionKey, ct);
            for (var i = 0; i < session.Messages.Count; i++)
                await InsertMessageAsync(conn, session.SessionKey, session.Messages[i], i, ct);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task AppendMessageAsync(
        string sessionKey, ChatMessage message, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);

        // Ensure session row exists (upsert with defaults)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO agentcli.sessions
                    (session_key, chat_type, created_at, updated_at, compaction_count)
                VALUES ($1, 'direct', NOW(), NOW(), 0)
                ON CONFLICT (session_key) DO NOTHING
                """;
            cmd.Parameters.AddWithValue(sessionKey);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Get next position
        int position;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(MAX(position), -1) + 1
                FROM agentcli.messages
                WHERE session_key = $1
                """;
            cmd.Parameters.AddWithValue(sessionKey);
            position = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        await InsertMessageAsync(conn, sessionKey, message, position, ct);
        await TouchSessionAsync(conn, sessionKey, ct);
    }

    public async Task DeleteAsync(string sessionKey, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        // Messages deleted via CASCADE
        cmd.CommandText = "DELETE FROM agentcli.sessions WHERE session_key = $1";
        cmd.Parameters.AddWithValue(sessionKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_key, s.channel, s.user_id, s.chat_type,
                   s.updated_at, s.compaction_count,
                   COUNT(m.id)::int AS message_count
            FROM agentcli.sessions s
            LEFT JOIN agentcli.messages m ON m.session_key = s.session_key
            GROUP BY s.session_key, s.channel, s.user_id, s.chat_type,
                     s.updated_at, s.compaction_count
            ORDER BY s.updated_at DESC
            """;

        var results = new List<SessionSummary>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SessionSummary(
                SessionKey:      reader.GetString(0),
                Channel:         reader.IsDBNull(1) ? null : reader.GetString(1),
                UserId:          reader.IsDBNull(2) ? null : reader.GetString(2),
                ChatType:        reader.GetString(3),
                UpdatedAt:       reader.GetFieldValue<DateTimeOffset>(4),
                CompactionCount: reader.GetInt32(5),
                MessageCount:    reader.GetInt32(6)
            ));
        }
        return results;
    }

    public async Task<bool> ExistsAsync(string sessionKey, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM agentcli.sessions WHERE session_key = $1 LIMIT 1";
        cmd.Parameters.AddWithValue(sessionKey);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    // ─── Schema migration ─────────────────────────────────────────────────────

    /// <summary>
    /// Idempotent schema setup — safe to call on every startup.
    /// Creates schema + tables + indexes if they don't exist.
    /// </summary>
    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            -- Isolated schema — won't collide with other apps on same Postgres
            CREATE SCHEMA IF NOT EXISTS agentcli;

            -- Per-session metadata
            CREATE TABLE IF NOT EXISTS agentcli.sessions (
                session_key      TEXT        PRIMARY KEY,
                channel          TEXT,
                user_id          TEXT,
                chat_type        TEXT        NOT NULL DEFAULT 'direct',
                created_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                compaction_count INTEGER     NOT NULL DEFAULT 0
            );

            -- Message history — one row per message
            CREATE TABLE IF NOT EXISTS agentcli.messages (
                id           BIGSERIAL   PRIMARY KEY,
                session_key  TEXT        NOT NULL
                             REFERENCES agentcli.sessions(session_key)
                             ON DELETE CASCADE,
                position     INTEGER     NOT NULL,
                role         TEXT        NOT NULL,
                content      TEXT,
                tool_calls   JSONB,
                tool_call_id TEXT
            );

            -- Proactive event queue — agents poll this; FOR UPDATE SKIP LOCKED
            CREATE TABLE IF NOT EXISTS agentcli.proactive_events (
                id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id        TEXT        NOT NULL,
                channel        TEXT        NOT NULL,
                event_type     TEXT        NOT NULL,
                payload        JSONB,
                scheduled_for  TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                claimed_by     TEXT,
                claimed_at     TIMESTAMPTZ,
                fired_at       TIMESTAMPTZ,
                status         TEXT        NOT NULL DEFAULT 'pending',
                created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            -- Agent registry — gateways use this for health checks + routing
            CREATE TABLE IF NOT EXISTS agentcli.agents (
                agent_id       TEXT        PRIMARY KEY,
                agent_name     TEXT        NOT NULL,
                host           TEXT        NOT NULL,
                reply_gateway  TEXT,
                version        TEXT,
                started_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                last_heartbeat TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                status         TEXT        NOT NULL DEFAULT 'active'
            );

            -- Channel registry — 100 bots = 100 rows; gateways read this
            CREATE TABLE IF NOT EXISTS agentcli.channels (
                channel_id     TEXT        PRIMARY KEY,
                channel_type   TEXT        NOT NULL,
                bot_token      TEXT,
                tenant_id      TEXT,
                gateway_id     TEXT,
                display_name   TEXT,
                created_at     TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                active         BOOLEAN     NOT NULL DEFAULT TRUE
            );

            -- Soul — single row "sivar-ai", written by admins only
            CREATE TABLE IF NOT EXISTS agentcli.souls (
                agent_type    TEXT        PRIMARY KEY,  -- "sivar-ai"
                name          TEXT        NOT NULL,
                prompt        TEXT        NOT NULL,
                startup_reads TEXT[]      NOT NULL DEFAULT '{}',
                version       INTEGER     NOT NULL DEFAULT 1,
                updated_at    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_by    TEXT
            );

            -- Idempotent migration: add startup_reads to existing deployments
            ALTER TABLE agentcli.souls
                ADD COLUMN IF NOT EXISTS startup_reads TEXT[] NOT NULL DEFAULT '{}';

            -- Agent memory — shared world knowledge, written by admins
            CREATE TABLE IF NOT EXISTS agentcli.agent_memory (
                key          TEXT        PRIMARY KEY,
                content      TEXT        NOT NULL,
                tags         TEXT[]      NOT NULL DEFAULT '{}',
                created_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );

            -- User memory — hard-isolated per (user_id, channel)
            CREATE TABLE IF NOT EXISTS agentcli.user_memory (
                user_id      TEXT        NOT NULL,
                channel      TEXT        NOT NULL,
                key          TEXT        NOT NULL,
                value        TEXT        NOT NULL,
                updated_at   TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (user_id, channel, key)
            );

            -- Indexes
            CREATE INDEX IF NOT EXISTS idx_messages_session_pos
                ON agentcli.messages (session_key, position);

            CREATE INDEX IF NOT EXISTS idx_sessions_channel
                ON agentcli.sessions (channel);

            CREATE INDEX IF NOT EXISTS idx_sessions_updated
                ON agentcli.sessions (updated_at DESC);

            CREATE INDEX IF NOT EXISTS idx_sessions_user
                ON agentcli.sessions (user_id);

            CREATE INDEX IF NOT EXISTS idx_proactive_pending
                ON agentcli.proactive_events (scheduled_for)
                WHERE status = 'pending';

            CREATE INDEX IF NOT EXISTS idx_agents_status
                ON agentcli.agents (status);
            CREATE INDEX IF NOT EXISTS idx_agent_memory_tags
                ON agentcli.agent_memory USING GIN (tags);

            CREATE INDEX IF NOT EXISTS idx_user_memory_user
                ON agentcli.user_memory (user_id, channel);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private static async Task UpsertSessionAsync(
        NpgsqlConnection conn, ConversationSession s, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agentcli.sessions
                (session_key, channel, user_id, chat_type, created_at, updated_at, compaction_count)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT (session_key) DO UPDATE SET
                channel          = EXCLUDED.channel,
                user_id          = EXCLUDED.user_id,
                chat_type        = EXCLUDED.chat_type,
                updated_at       = EXCLUDED.updated_at,
                compaction_count = EXCLUDED.compaction_count
            """;
        cmd.Parameters.AddWithValue(s.SessionKey);
        cmd.Parameters.AddWithValue((object?)s.Channel  ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)s.UserId   ?? DBNull.Value);
        cmd.Parameters.AddWithValue(s.ChatType);
        cmd.Parameters.AddWithValue(s.CreatedAt);
        cmd.Parameters.AddWithValue(s.UpdatedAt);
        cmd.Parameters.AddWithValue(s.CompactionCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task DeleteMessagesAsync(
        NpgsqlConnection conn, string sessionKey, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM agentcli.messages WHERE session_key = $1";
        cmd.Parameters.AddWithValue(sessionKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task InsertMessageAsync(
        NpgsqlConnection conn, string sessionKey, ChatMessage msg, int position, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agentcli.messages
                (session_key, position, role, content, tool_calls, tool_call_id)
            VALUES ($1, $2, $3, $4, $5::jsonb, $6)
            """;
        cmd.Parameters.AddWithValue(sessionKey);
        cmd.Parameters.AddWithValue(position);
        cmd.Parameters.AddWithValue(msg.Role);
        cmd.Parameters.AddWithValue((object?)msg.Content    ?? DBNull.Value);
        cmd.Parameters.AddWithValue(
            msg.ToolCalls != null
                ? (object)JsonSerializer.Serialize(msg.ToolCalls, JsonOpts)
                : DBNull.Value);
        cmd.Parameters.AddWithValue((object?)msg.ToolCallId ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task TouchSessionAsync(
        NpgsqlConnection conn, string sessionKey, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agentcli.sessions SET updated_at = NOW() WHERE session_key = $1
            """;
        cmd.Parameters.AddWithValue(sessionKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync();
}
