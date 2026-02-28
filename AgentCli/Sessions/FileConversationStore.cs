using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCli;

/// <summary>
/// File-based conversation store.
///
/// Layout (mirrors OpenClaw exactly):
///   {storeDir}/
///     {sessionKey-safe}/
///       session.jsonl    — one JSON record per line (events)
///       meta.json        — session metadata (no messages)
///
/// JSONL event types:
///   {"type":"session","sessionKey":"...","channel":"...","userId":"...","createdAt":"..."}
///   {"type":"message","role":"user"|"assistant"|"tool","content":"...","toolCalls":[...],"toolCallId":"..."}
///
/// On load the file is replayed top-to-bottom into a ChatMessage list.
/// AppendMessageAsync() writes a single line — no full rewrite.
/// SaveAsync() rewrites the full file (used after compaction).
/// </summary>
public class FileConversationStore : IConversationStore
{
    private readonly string _storeDir;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented           = false,
    };

    public string Name => "file";

    public FileConversationStore(string? storeDir = null)
    {
        _storeDir = storeDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentcli", "sessions");
        Directory.CreateDirectory(_storeDir);
    }

    // ─── IConversationStore ───────────────────────────────────────────────────

    public async Task<ConversationSession?> LoadAsync(string sessionKey, CancellationToken ct = default)
    {
        var path = SessionFilePath(sessionKey);
        if (!File.Exists(path)) return null;

        var session = new ConversationSession
        {
            SessionKey = sessionKey,
        };

        bool headerRead = false;
        await foreach (var line in ReadLinesAsync(path, ct))
        {
            var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.GetProperty("type").GetString();

            if (type == "session" && !headerRead)
            {
                headerRead = true;
                if (doc.RootElement.TryGetProperty("channel",        out var ch))  session.Channel  = ch.GetString();
                if (doc.RootElement.TryGetProperty("userId",         out var uid)) session.UserId   = uid.GetString();
                if (doc.RootElement.TryGetProperty("chatType",       out var ct2)) session.ChatType = ct2.GetString() ?? "direct";
                if (doc.RootElement.TryGetProperty("createdAt",      out var ca))  session.CreatedAt = DateTimeOffset.Parse(ca.GetString()!);
                if (doc.RootElement.TryGetProperty("updatedAt",      out var ua))  session.UpdatedAt = DateTimeOffset.Parse(ua.GetString()!);
                if (doc.RootElement.TryGetProperty("compactionCount",out var cc))  session.CompactionCount = cc.GetInt32();
                continue;
            }

            if (type == "message")
            {
                var msg = DeserializeMessage(doc.RootElement);
                if (msg != null) session.Messages.Add(msg);
            }
        }

        return session;
    }

    public async Task SaveAsync(ConversationSession session, CancellationToken ct = default)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var path = SessionFilePath(session.SessionKey);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var writer = new StreamWriter(path, append: false);
        await writer.WriteLineAsync(SerializeHeader(session));
        foreach (var msg in session.Messages)
            await writer.WriteLineAsync(SerializeMessage(msg));
    }

    public async Task AppendMessageAsync(string sessionKey, ChatMessage message, CancellationToken ct = default)
    {
        var path = SessionFilePath(sessionKey);

        // If file doesn't exist yet, create header first
        if (!File.Exists(path))
        {
            var newSession = new ConversationSession { SessionKey = sessionKey };
            await SaveAsync(newSession, ct);
        }

        await using var writer = new StreamWriter(path, append: true);
        await writer.WriteLineAsync(SerializeMessage(message));
    }

    public Task DeleteAsync(string sessionKey, CancellationToken ct = default)
    {
        var path = SessionFilePath(sessionKey);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken ct = default)
    {
        var results = new List<SessionSummary>();
        foreach (var file in Directory.GetFiles(_storeDir, "*.jsonl", SearchOption.AllDirectories))
        {
            var summary = await ReadSummaryAsync(file, ct);
            if (summary != null) results.Add(summary);
        }
        return results.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public Task<bool> ExistsAsync(string sessionKey, CancellationToken ct = default)
        => Task.FromResult(File.Exists(SessionFilePath(sessionKey)));

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private string SessionFilePath(string sessionKey)
    {
        var safe = SanitizeKey(sessionKey);
        return Path.Combine(_storeDir, safe + ".jsonl");
    }

    private static string SanitizeKey(string key)
    {
        // Replace characters unsafe in filenames with underscores
        // "telegram:direct:5932684607" → "telegram_direct_5932684607"
        return string.Join("_",
            key.Split(Path.GetInvalidFileNameChars()
                .Concat(new[] { ':', '/', '\\' })
                .Distinct()
                .ToArray(),
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static string SerializeHeader(ConversationSession s)
        => JsonSerializer.Serialize(new
        {
            type            = "session",
            sessionKey      = s.SessionKey,
            channel         = s.Channel,
            userId          = s.UserId,
            chatType        = s.ChatType,
            createdAt       = s.CreatedAt.ToString("O"),
            updatedAt       = s.UpdatedAt.ToString("O"),
            compactionCount = s.CompactionCount,
        }, JsonOpts);

    private static string SerializeMessage(ChatMessage m)
        => JsonSerializer.Serialize(new
        {
            type       = "message",
            role       = m.Role,
            content    = m.Content,
            toolCalls  = m.ToolCalls,
            toolCallId = m.ToolCallId,
        }, JsonOpts);

    private static ChatMessage? DeserializeMessage(JsonElement el)
    {
        var role    = el.TryGetProperty("role",    out var r) ? r.GetString() : null;
        if (role == null) return null;

        var content    = el.TryGetProperty("content",    out var c)  ? c.GetString()  : null;
        var toolCallId = el.TryGetProperty("toolCallId", out var tci) ? tci.GetString() : null;

        List<ToolCall>? toolCalls = null;
        if (el.TryGetProperty("toolCalls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            toolCalls = JsonSerializer.Deserialize<List<ToolCall>>(tc.GetRawText(), JsonOpts);

        return new ChatMessage(role, content, toolCalls, toolCallId);
    }

    private static async Task<SessionSummary?> ReadSummaryAsync(string path, CancellationToken ct)
    {
        string? sessionKey = null, channel = null, userId = null, chatType = "direct";
        DateTimeOffset updatedAt = DateTimeOffset.MinValue;
        int messageCount = 0, compactionCount = 0;

        await foreach (var line in ReadLinesAsync(path, ct))
        {
            try
            {
                var doc  = JsonDocument.Parse(line);
                var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;

                if (type == "session")
                {
                    sessionKey      = doc.RootElement.TryGetProperty("sessionKey",      out var sk) ? sk.GetString() : Path.GetFileNameWithoutExtension(path);
                    channel         = doc.RootElement.TryGetProperty("channel",         out var ch) ? ch.GetString() : null;
                    userId          = doc.RootElement.TryGetProperty("userId",          out var ui) ? ui.GetString() : null;
                    chatType        = doc.RootElement.TryGetProperty("chatType",        out var ct2) ? ct2.GetString() ?? "direct" : "direct";
                    compactionCount = doc.RootElement.TryGetProperty("compactionCount", out var cc)  ? cc.GetInt32() : 0;
                    if (doc.RootElement.TryGetProperty("updatedAt", out var ua))
                        DateTimeOffset.TryParse(ua.GetString(), out updatedAt);
                }
                else if (type == "message")
                {
                    messageCount++;
                }
            }
            catch { /* skip malformed lines */ }
        }

        if (sessionKey == null) return null;
        return new SessionSummary(sessionKey, channel, userId, chatType, messageCount, compactionCount, updatedAt);
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(ct) is { } line)
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
    }
}
