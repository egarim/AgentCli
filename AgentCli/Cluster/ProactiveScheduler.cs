using System.Text.Json;
using System.Text.Json.Serialization;
using Npgsql;

namespace AgentCli;

// ─── Proactive event model ────────────────────────────────────────────────────

public sealed record ProactiveEvent(
    Guid            Id,
    string          UserId,
    string          Channel,
    string          EventType,       // "reminder" | "summary" | "alert" | "direct"
    JsonElement?    Payload,
    DateTimeOffset  ScheduledFor
);

// ─── Handler interface ────────────────────────────────────────────────────────

/// <summary>
/// Handles a claimed proactive event.
/// Implement one handler per event_type.
/// </summary>
public interface IProactiveHandler
{
    string EventType { get; }
    Task HandleAsync(ProactiveEvent evt, CancellationToken ct);
}

// ─── Built-in handlers ────────────────────────────────────────────────────────

/// <summary>
/// DirectMessage — sends payload["text"] directly to the user without an AI turn.
/// Payload: { "text": "Reminder: your 3pm meeting" }
/// </summary>
public sealed class DirectMessageHandler : IProactiveHandler
{
    private readonly IChannelConnector _connector;

    public string EventType => "direct";

    public DirectMessageHandler(IChannelConnector connector)
        => _connector = connector;

    public async Task HandleAsync(ProactiveEvent evt, CancellationToken ct)
    {
        var text = evt.Payload?.TryGetProperty("text", out var t) == true
            ? t.GetString() ?? "(no text)"
            : "(no text)";

        var chatId = evt.Payload?.TryGetProperty("chatId", out var c) == true
            ? c.GetString()
            : null;

        await _connector.SendAsync(
            new ChannelOutboundMessage(
                To:   new ChannelAddress(evt.Channel, evt.UserId, chatId),
                Text: text),
            ct);
    }
}

/// <summary>
/// AgentTurn — runs a full AI turn for the user with an injected prompt.
/// Useful for: morning summaries, reminders with context, scheduled Q&A.
/// Payload: { "prompt": "Generate the user's daily summary" }
/// </summary>
public sealed class AgentTurnHandler : IProactiveHandler
{
    private readonly SessionManager   _sessions;
    private readonly IChannelConnector _connector;

    public string EventType => "agent-turn";

    public AgentTurnHandler(SessionManager sessions, IChannelConnector connector)
    {
        _sessions  = sessions;
        _connector = connector;
    }

    public async Task HandleAsync(ProactiveEvent evt, CancellationToken ct)
    {
        var prompt = evt.Payload?.TryGetProperty("prompt", out var p) == true
            ? p.GetString() ?? "Say hello to the user."
            : "Say hello to the user.";

        var chatId = evt.Payload?.TryGetProperty("chatId", out var c) == true
            ? c.GetString()
            : null;

        var sessionKey = chatId != null
            ? SessionKey.Direct(evt.Channel, chatId)
            : SessionKey.Direct(evt.Channel, evt.UserId);

        // Run full AI turn — SessionManager handles history, compaction, etc.
        var reply = await _sessions.RunAsync(sessionKey, prompt, ct);

        if (!string.IsNullOrWhiteSpace(reply))
        {
            await _connector.SendAsync(
                new ChannelOutboundMessage(
                    To:   new ChannelAddress(evt.Channel, evt.UserId, chatId),
                    Text: reply),
                ct);
        }
    }
}

/// <summary>
/// WebhookAlert — fires an HTTP POST to an external endpoint.
/// Payload: { "url": "https://...", "body": { ... } }
/// </summary>
public sealed class WebhookAlertHandler : IProactiveHandler
{
    private readonly HttpClient _http;

    public string EventType => "webhook";

    public WebhookAlertHandler(HttpClient? http = null)
        => _http = http ?? new HttpClient();

    public async Task HandleAsync(ProactiveEvent evt, CancellationToken ct)
    {
        if (evt.Payload == null) return;
        if (!evt.Payload.Value.TryGetProperty("url", out var urlEl)) return;
        var url = urlEl.GetString();
        if (string.IsNullOrWhiteSpace(url)) return;

        var body = evt.Payload.Value.TryGetProperty("body", out var bodyEl)
            ? bodyEl.GetRawText()
            : "{}";

        var content  = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(url, content, ct);
        response.EnsureSuccessStatusCode();
    }
}

// ─── Scheduler ────────────────────────────────────────────────────────────────

/// <summary>
/// Polls agentcli.proactive_events every PollInterval and claims pending rows
/// using FOR UPDATE SKIP LOCKED — Postgres guarantees only one agent claims each row.
///
/// No Redis, no distributed locks, no ownership table.
/// Multiple agents on multiple servers can all run this — they naturally fan out.
///
/// Usage:
///   var scheduler = new ProactiveScheduler(connectionString, handlers, logger);
///   scheduler.Start(cts.Token);
///   // later:
///   await scheduler.ScheduleAsync("telegram", userId, "reminder", payload, fireAt);
/// </summary>
public sealed class ProactiveScheduler : IAsyncDisposable
{
    private readonly NpgsqlDataSource              _dataSource;
    private readonly Dictionary<string, IProactiveHandler> _handlers;
    private readonly ILogger<ProactiveScheduler>?  _log;
    private readonly TimeSpan                      _pollInterval;

    private CancellationTokenSource? _cts;
    private Task?                    _pollTask;

    public ProactiveScheduler(
        string                          connectionString,
        IEnumerable<IProactiveHandler>  handlers,
        ILogger<ProactiveScheduler>?    logger       = null,
        TimeSpan?                       pollInterval = null)
    {
        _dataSource   = NpgsqlDataSource.Create(connectionString);
        _handlers     = handlers.ToDictionary(h => h.EventType, StringComparer.OrdinalIgnoreCase);
        _log          = logger;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(10);
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    public void Start(CancellationToken ct = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token), _cts.Token);
        _log?.LogInformation(
            "ProactiveScheduler started (poll every {Interval}s, {Count} handlers: {Types})",
            _pollInterval.TotalSeconds,
            _handlers.Count,
            string.Join(", ", _handlers.Keys));
    }

    public async Task StopAsync()
    {
        if (_cts != null) { await _cts.CancelAsync(); _cts.Dispose(); }
        if (_pollTask != null) try { await _pollTask; } catch { /* expected */ }
        _log?.LogInformation("ProactiveScheduler stopped.");
    }

    // ─── Schedule API ─────────────────────────────────────────────────────────

    /// <summary>Schedule a proactive event for a user.</summary>
    public async Task ScheduleAsync(
        string          channel,
        string          userId,
        string          eventType,
        object?         payload      = null,
        DateTimeOffset? scheduledFor = null,
        CancellationToken ct         = default)
    {
        var payloadJson = payload != null
            ? JsonSerializer.Serialize(payload)
            : null;

        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agentcli.proactive_events
                (user_id, channel, event_type, payload, scheduled_for, status)
            VALUES ($1, $2, $3, $4::jsonb, $5, 'pending')
            """;
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(eventType);
        cmd.Parameters.AddWithValue((object?)payloadJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue(scheduledFor ?? DateTimeOffset.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct);

        _log?.LogDebug("Scheduled {EventType} for {UserId}@{Channel} at {Time}",
            eventType, userId, channel, scheduledFor ?? DateTimeOffset.UtcNow);
    }

    /// <summary>Cancel all pending events for a user (e.g. user said /cancel).</summary>
    public async Task CancelUserEventsAsync(
        string channel, string userId, string? eventType = null, CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();

        if (eventType != null)
        {
            cmd.CommandText = """
                UPDATE agentcli.proactive_events
                SET status = 'cancelled'
                WHERE user_id = $1 AND channel = $2 AND event_type = $3 AND status = 'pending'
                """;
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(channel);
            cmd.Parameters.AddWithValue(eventType);
        }
        else
        {
            cmd.CommandText = """
                UPDATE agentcli.proactive_events
                SET status = 'cancelled'
                WHERE user_id = $1 AND channel = $2 AND status = 'pending'
                """;
            cmd.Parameters.AddWithValue(userId);
            cmd.Parameters.AddWithValue(channel);
        }
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ─── Poll loop ────────────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(ct);
                await Task.Delay(_pollInterval, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log?.LogError(ex, "ProactiveScheduler poll error — retrying after {Interval}s",
                    _pollInterval.TotalSeconds);
                await Task.Delay(_pollInterval, ct);
            }
        }
    }

    private async Task ProcessBatchAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx   = await conn.BeginTransactionAsync(ct);

        // Claim up to 10 pending events atomically.
        // FOR UPDATE SKIP LOCKED means multiple agents never claim the same row.
        List<ProactiveEvent> claimed;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, user_id, channel, event_type, payload, scheduled_for
                FROM agentcli.proactive_events
                WHERE status = 'pending'
                  AND scheduled_for <= NOW()
                ORDER BY scheduled_for ASC
                LIMIT 10
                FOR UPDATE SKIP LOCKED
                """;

            claimed = new List<ProactiveEvent>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var payload = reader.IsDBNull(4)
                    ? (JsonElement?)null
                    : JsonDocument.Parse(reader.GetString(4)).RootElement;

                claimed.Add(new ProactiveEvent(
                    Id:           reader.GetGuid(0),
                    UserId:       reader.GetString(1),
                    Channel:      reader.GetString(2),
                    EventType:    reader.GetString(3),
                    Payload:      payload,
                    ScheduledFor: reader.GetFieldValue<DateTimeOffset>(5)));
            }
        }

        if (claimed.Count == 0) { await tx.RollbackAsync(ct); return; }

        // Mark all claimed rows in one UPDATE before releasing the lock
        var ids = string.Join(",", claimed.Select(e => $"'{e.Id}'"));
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"""
                UPDATE agentcli.proactive_events
                SET status = 'claimed', claimed_at = NOW()
                WHERE id = ANY(ARRAY[{ids}]::uuid[])
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        // Process outside the transaction — handlers may take time
        foreach (var evt in claimed)
            await DispatchAsync(evt, ct);
    }

    private async Task DispatchAsync(ProactiveEvent evt, CancellationToken ct)
    {
        try
        {
            if (!_handlers.TryGetValue(evt.EventType, out var handler))
            {
                _log?.LogWarning("No handler for event_type '{Type}' (id={Id})", evt.EventType, evt.Id);
                await MarkAsync(evt.Id, "failed", ct);
                return;
            }

            _log?.LogInformation("Firing {EventType} for {UserId}@{Channel} (id={Id})",
                evt.EventType, evt.UserId, evt.Channel, evt.Id);

            await handler.HandleAsync(evt, ct);
            await MarkAsync(evt.Id, "fired", ct);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Handler failed for event {Id} ({Type})", evt.Id, evt.EventType);
            await MarkAsync(evt.Id, "failed", ct);
        }
    }

    private async Task MarkAsync(Guid id, string status, CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = status == "fired"
            ? "UPDATE agentcli.proactive_events SET status=$1, fired_at=NOW() WHERE id=$2"
            : "UPDATE agentcli.proactive_events SET status=$1 WHERE id=$2";
        cmd.Parameters.AddWithValue(status);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _dataSource.DisposeAsync();
    }
}
