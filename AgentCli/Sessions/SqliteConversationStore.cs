using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace AgentCli;

/// <summary>
/// SQLite-backed conversation store.
///
/// All sessions live in a single database file:
///   {storeDir}/conversations.db
///
/// Schema:
///   sessions  — one row per session (metadata only)
///   messages  — one row per message, FK → sessions.session_key
///
/// This is more query-friendly than JSONL (filter by channel, user, date etc.)
/// and scales better when many users share the same process.
/// </summary>
public class SqliteConversationStore : IConversationStore, IDisposable
{
    private readonly SqliteConnection _db;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Name => "sqlite";

    public SqliteConversationStore(string? dbPath = null)
    {
        var path = dbPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "conversations.db");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        _db = new SqliteConnection($"Data Source={path}");
        _db.Open();
        EnsureSchema();
    }

    // ─── IConversationStore ───────────────────────────────────────────────────

    public async Task<ConversationSession?> LoadAsync(string sessionKey, CancellationToken ct = default)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT channel, user_id, chat_type, created_at, updated_at, compaction_count FROM sessions WHERE session_key = @key";
        cmd.Parameters.AddWithValue("@key", sessionKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        var session = new ConversationSession
        {
            SessionKey      = sessionKey,
            Channel         = reader.IsDBNull(0) ? null : reader.GetString(0),
            UserId          = reader.IsDBNull(1) ? null : reader.GetString(1),
            ChatType        = reader.GetString(2),
            CreatedAt       = DateTimeOffset.Parse(reader.GetString(3)),
            UpdatedAt       = DateTimeOffset.Parse(reader.GetString(4)),
            CompactionCount = reader.GetInt32(5),
        };

        reader.Close();

        session.Messages.AddRange(await LoadMessagesAsync(sessionKey, ct));
        return session;
    }

    public async Task SaveAsync(ConversationSession session, CancellationToken ct = default)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;

        using var tx = _db.BeginTransaction();
        try
        {
            UpsertSession(session);
            DeleteMessages(session.SessionKey);
            foreach (var (msg, i) in session.Messages.Select((m, i) => (m, i)))
                InsertMessage(session.SessionKey, msg, i);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
        await Task.CompletedTask;
    }

    public async Task AppendMessageAsync(string sessionKey, ChatMessage message, CancellationToken ct = default)
    {
        // Ensure session row exists
        if (!await ExistsAsync(sessionKey, ct))
        {
            var newSession = new ConversationSession { SessionKey = sessionKey };
            UpsertSession(newSession);
        }

        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(position), -1) + 1 FROM messages WHERE session_key = @key";
        cmd.Parameters.AddWithValue("@key", sessionKey);
        var pos = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));

        InsertMessage(sessionKey, message, pos);
        TouchSession(sessionKey);
        await Task.CompletedTask;
    }

    public async Task DeleteAsync(string sessionKey, CancellationToken ct = default)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE session_key = @key; DELETE FROM sessions WHERE session_key = @key";
        cmd.Parameters.AddWithValue("@key", sessionKey);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct = default)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            SELECT s.session_key, s.channel, s.user_id, s.chat_type,
                   s.updated_at, s.compaction_count,
                   COUNT(m.id) AS message_count
            FROM sessions s
            LEFT JOIN messages m ON m.session_key = s.session_key
            GROUP BY s.session_key
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
                UpdatedAt:       DateTimeOffset.Parse(reader.GetString(4)),
                CompactionCount: reader.GetInt32(5),
                MessageCount:    reader.GetInt32(6)
            ));
        }
        return results;
    }

    public async Task<bool> ExistsAsync(string sessionKey, CancellationToken ct = default)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sessions WHERE session_key = @key LIMIT 1";
        cmd.Parameters.AddWithValue("@key", sessionKey);
        return await cmd.ExecuteScalarAsync(ct) != null;
    }

    // ─── Schema ───────────────────────────────────────────────────────────────

    private void EnsureSchema()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                session_key      TEXT PRIMARY KEY,
                channel          TEXT,
                user_id          TEXT,
                chat_type        TEXT NOT NULL DEFAULT 'direct',
                created_at       TEXT NOT NULL,
                updated_at       TEXT NOT NULL,
                compaction_count INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS messages (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                session_key TEXT NOT NULL REFERENCES sessions(session_key) ON DELETE CASCADE,
                position    INTEGER NOT NULL,
                role        TEXT NOT NULL,
                content     TEXT,
                tool_calls  TEXT,
                tool_call_id TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_messages_session ON messages(session_key, position);
            CREATE INDEX IF NOT EXISTS idx_sessions_channel ON sessions(channel);
            CREATE INDEX IF NOT EXISTS idx_sessions_updated ON sessions(updated_at DESC);
            """;
        cmd.ExecuteNonQuery();
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task<List<ChatMessage>> LoadMessagesAsync(string sessionKey, CancellationToken ct)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT role, content, tool_calls, tool_call_id FROM messages WHERE session_key = @key ORDER BY position ASC";
        cmd.Parameters.AddWithValue("@key", sessionKey);

        var msgs = new List<ChatMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var role       = reader.GetString(0);
            var content    = reader.IsDBNull(1) ? null : reader.GetString(1);
            var toolCallId = reader.IsDBNull(3) ? null : reader.GetString(3);
            List<ToolCall>? toolCalls = null;
            if (!reader.IsDBNull(2))
                toolCalls = JsonSerializer.Deserialize<List<ToolCall>>(reader.GetString(2), JsonOpts);
            msgs.Add(new ChatMessage(role, content, toolCalls, toolCallId));
        }
        return msgs;
    }

    private void UpsertSession(ConversationSession s)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sessions (session_key, channel, user_id, chat_type, created_at, updated_at, compaction_count)
            VALUES (@key, @channel, @userId, @chatType, @createdAt, @updatedAt, @compactionCount)
            ON CONFLICT(session_key) DO UPDATE SET
                channel          = excluded.channel,
                user_id          = excluded.user_id,
                chat_type        = excluded.chat_type,
                updated_at       = excluded.updated_at,
                compaction_count = excluded.compaction_count
            """;
        cmd.Parameters.AddWithValue("@key",             s.SessionKey);
        cmd.Parameters.AddWithValue("@channel",         (object?)s.Channel ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@userId",          (object?)s.UserId  ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@chatType",        s.ChatType);
        cmd.Parameters.AddWithValue("@createdAt",       s.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@updatedAt",       s.UpdatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@compactionCount", s.CompactionCount);
        cmd.ExecuteNonQuery();
    }

    private void DeleteMessages(string sessionKey)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "DELETE FROM messages WHERE session_key = @key";
        cmd.Parameters.AddWithValue("@key", sessionKey);
        cmd.ExecuteNonQuery();
    }

    private void InsertMessage(string sessionKey, ChatMessage msg, int position)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages (session_key, position, role, content, tool_calls, tool_call_id)
            VALUES (@key, @pos, @role, @content, @toolCalls, @toolCallId)
            """;
        cmd.Parameters.AddWithValue("@key",        sessionKey);
        cmd.Parameters.AddWithValue("@pos",        position);
        cmd.Parameters.AddWithValue("@role",       msg.Role);
        cmd.Parameters.AddWithValue("@content",    (object?)msg.Content    ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@toolCallId", (object?)msg.ToolCallId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@toolCalls",
            msg.ToolCalls != null
                ? (object)JsonSerializer.Serialize(msg.ToolCalls, JsonOpts)
                : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void TouchSession(string sessionKey)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE sessions SET updated_at = @now WHERE session_key = @key";
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@key", sessionKey);
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}
