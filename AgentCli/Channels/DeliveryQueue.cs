using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCli;

// ─── Delivery queue ───────────────────────────────────────────────────────────

/// <summary>
/// File-backed delivery queue — persists failed outbound messages and retries them.
///
/// Mirrors OpenClaw's delivery-queue system:
///   ~/.agentcli/delivery-queue/         ← pending
///   ~/.agentcli/delivery-queue/failed/  ← exceeded max retries
///
/// The DeliveryWorker background task drives the retry loop.
/// </summary>
public sealed class FileDeliveryQueue : IAsyncDisposable
{
    private readonly string _queueDir;
    private readonly string _failedDir;
    private readonly ChannelConnectorRegistry _connectors;
    private readonly ILogger?  _log;

    // Retry policy
    private readonly int      _maxRetries = 5;
    private readonly TimeSpan _baseDelay  = TimeSpan.FromSeconds(2);

    private CancellationTokenSource? _cts;
    private Task?                    _workerTask;

    public FileDeliveryQueue(
        ChannelConnectorRegistry connectors,
        string?  queueDir = null,
        ILogger? logger   = null)
    {
        _connectors = connectors;
        _log        = logger;

        var dir    = queueDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "delivery-queue");
        _queueDir  = dir;
        _failedDir = Path.Combine(dir, "failed");

        Directory.CreateDirectory(_queueDir);
        Directory.CreateDirectory(_failedDir);
    }

    // ─── Enqueue ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueue a message for delivery. Attempts immediate send; if that fails
    /// persists to disk for retry by the background worker.
    /// </summary>
    public async Task EnqueueAsync(
        ChannelOutboundMessage message,
        CancellationToken      ct = default)
    {
        // Try immediate send first
        if (_connectors.TryGet(message.To.Channel, out var connector))
        {
            try
            {
                await connector!.SendAsync(message, ct);
                return; // success — no need to queue
            }
            catch (Exception ex)
            {
                _log?.LogWarning(ex, "Immediate send failed — queuing for retry.");
            }
        }

        // Persist to disk
        var entry = new QueueEntry
        {
            Id          = Guid.NewGuid(),
            EnqueuedAt  = DateTimeOffset.UtcNow,
            Channel     = message.To.Channel,
            UserId      = message.To.UserId,
            ChatId      = message.To.ChatId,
            Text        = message.Text,
            Metadata    = message.Metadata?.ToDictionary(k => k.Key, v => v.Value),
            RetryCount  = 0,
        };

        await WriteEntryAsync(entry);
        _log?.LogInformation("Message queued: {Id} → {Channel}:{UserId}", entry.Id, entry.Channel, entry.UserId);
    }

    // ─── Background worker ────────────────────────────────────────────────────

    public void StartWorker(CancellationToken ct = default)
    {
        _cts        = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token), _cts.Token);
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync(ct);
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Delivery worker error.");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        var files = Directory.GetFiles(_queueDir, "*.json");
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            await ProcessEntryFileAsync(file, ct);
        }
    }

    private async Task ProcessEntryFileAsync(string filePath, CancellationToken ct)
    {
        QueueEntry? entry;
        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            entry    = JsonSerializer.Deserialize<QueueEntry>(json);
            if (entry is null) { File.Delete(filePath); return; }
        }
        catch { return; } // corrupt file

        // Exponential backoff — don't retry too soon
        var backoff = _baseDelay * Math.Pow(2, entry.RetryCount);
        if (DateTimeOffset.UtcNow < entry.LastAttemptAt + backoff) return;

        if (!_connectors.TryGet(entry.Channel, out var connector))
        {
            _log?.LogWarning("No connector for channel {Channel} — skipping {Id}", entry.Channel, entry.Id);
            return;
        }

        try
        {
            var message = new ChannelOutboundMessage(
                To:   new ChannelAddress(entry.Channel, entry.UserId, entry.ChatId),
                Text: entry.Text,
                Metadata: entry.Metadata);

            await connector!.SendAsync(message, ct);

            // Success — remove from queue
            File.Delete(filePath);
            _log?.LogInformation("Queued message delivered: {Id}", entry.Id);
        }
        catch (Exception ex)
        {
            entry.RetryCount++;
            entry.LastAttemptAt = DateTimeOffset.UtcNow;
            entry.LastError     = ex.Message;

            _log?.LogWarning("Retry {Count}/{Max} failed for {Id}: {Error}",
                entry.RetryCount, _maxRetries, entry.Id, ex.Message);

            if (entry.RetryCount >= _maxRetries)
            {
                // Move to failed/
                var failedPath = Path.Combine(_failedDir, Path.GetFileName(filePath));
                File.Move(filePath, failedPath, overwrite: true);
                _log?.LogError("Message {Id} exceeded max retries — moved to failed/", entry.Id);
            }
            else
            {
                await WriteEntryAsync(entry, filePath);
            }
        }
    }

    private async Task WriteEntryAsync(QueueEntry entry, string? path = null)
    {
        path ??= Path.Combine(_queueDir, $"{entry.Id}.json");
        var tmp  = $"{path}.{Environment.ProcessId}.tmp";
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts != null) { await _cts.CancelAsync(); _cts.Dispose(); }
        if (_workerTask != null) try { await _workerTask; } catch { }
    }

    // ─── Queue entry model ────────────────────────────────────────────────────

    private sealed class QueueEntry
    {
        [JsonPropertyName("id")]             public Guid                          Id             { get; set; }
        [JsonPropertyName("enqueuedAt")]     public DateTimeOffset                EnqueuedAt     { get; set; }
        [JsonPropertyName("channel")]        public string                        Channel        { get; set; } = "";
        [JsonPropertyName("userId")]         public string                        UserId         { get; set; } = "";
        [JsonPropertyName("chatId")]         public string?                       ChatId         { get; set; }
        [JsonPropertyName("text")]           public string                        Text           { get; set; } = "";
        [JsonPropertyName("metadata")]       public Dictionary<string, string>?   Metadata       { get; set; }
        [JsonPropertyName("retryCount")]     public int                           RetryCount     { get; set; }
        [JsonPropertyName("lastAttemptAt")]  public DateTimeOffset                LastAttemptAt  { get; set; }
        [JsonPropertyName("lastError")]      public string?                       LastError      { get; set; }
    }
}
