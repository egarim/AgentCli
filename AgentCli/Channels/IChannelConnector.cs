using System.Text.Json.Serialization;

namespace AgentCli;

// ─── Mirrors Sivar.Os.Gateway.Core exactly (drop-in compatible) ──────────────

/// <summary>
/// Normalized inbound message from any channel.
/// Mirrors Sivar.Os.Gateway.Core.InboundMessage.
/// </summary>
public sealed record ChannelInboundMessage(
    string MessageId,
    ChannelAddress From,
    DateTimeOffset Timestamp,
    string Text,
    string? ReplyToMessageId = null,
    IReadOnlyDictionary<string, string>? Metadata = null
);

/// <summary>
/// Outbound message to be sent via a channel.
/// Mirrors Sivar.Os.Gateway.Core.OutboundMessage.
/// </summary>
public sealed record ChannelOutboundMessage(
    ChannelAddress To,
    string Text,
    IReadOnlyDictionary<string, string>? Metadata = null
);

/// <summary>
/// Channel + user addressing.
/// Mirrors Sivar.Os.Gateway.Core.ChannelAddress.
/// </summary>
public sealed record ChannelAddress(
    string Channel,       // "telegram", "whatsapp", "discord", "signal"
    string UserId,        // platform user/chat id
    string? ChatId = null // group/channel chat id (if different from UserId)
);

// ─── Channel connector interface ─────────────────────────────────────────────

/// <summary>
/// Pluggable channel connector.
/// Implements both send (IChannelAdapter compatible) and receive (StartAsync/StopAsync).
///
/// Compatible with Sivar.Os.Gateway.Core.IChannelAdapter — same ChannelName + SendAsync
/// contract, so implementations can be used in both AgentCli and the Gateway.
/// </summary>
public interface IChannelConnector
{
    /// <summary>Channel identifier ("telegram", "whatsapp", "discord", etc.)</summary>
    string ChannelName { get; }

    /// <summary>
    /// Start receiving inbound messages.
    /// Each inbound message is passed to the provided handler.
    /// </summary>
    Task StartAsync(
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        CancellationToken ct);

    /// <summary>Stop receiving messages and clean up resources.</summary>
    Task StopAsync(CancellationToken ct);

    /// <summary>Send a message to a user/chat.</summary>
    Task SendAsync(ChannelOutboundMessage message, CancellationToken ct);
}

// ─── Typing indicator ─────────────────────────────────────────────────────────

/// <summary>
/// Sends "typing..." status while the agent is processing.
/// Start before the agent turn; stop (or dispose) when done.
/// </summary>
public interface ITypingIndicator : IAsyncDisposable
{
    Task StartAsync(ChannelAddress target, CancellationToken ct);
    Task StopAsync(ChannelAddress target, CancellationToken ct);
}

/// <summary>No-op typing indicator for channels that don't support it.</summary>
public sealed class NullTypingIndicator : ITypingIndicator
{
    public static readonly NullTypingIndicator Instance = new();
    public Task StartAsync(ChannelAddress target, CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(ChannelAddress target, CancellationToken ct)  => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// ─── User gate ────────────────────────────────────────────────────────────────

/// <summary>
/// Controls who can use the bot.
/// Checked before every inbound message is processed.
/// </summary>
public interface IUserGate
{
    /// <summary>
    /// Returns AccessDecision for the given user.
    /// Called before routing; can return Allow, Deny, or Pending (auth required).
    /// </summary>
    Task<AccessDecision> CheckAsync(ChannelAddress user, CancellationToken ct);

    /// <summary>Mark a user as explicitly allowed (e.g. after pairing/auth).</summary>
    Task AllowAsync(string channel, string userId, CancellationToken ct);

    /// <summary>Mark a user as explicitly denied.</summary>
    Task DenyAsync(string channel, string userId, CancellationToken ct);
}

public enum AccessDecision { Allow, Deny, PendingAuth }

/// <summary>Open gate — allows everyone.</summary>
public sealed class AllowAllUserGate : IUserGate
{
    public static readonly AllowAllUserGate Instance = new();
    public Task<AccessDecision> CheckAsync(ChannelAddress user, CancellationToken ct) =>
        Task.FromResult(AccessDecision.Allow);
    public Task AllowAsync(string channel, string userId, CancellationToken ct) => Task.CompletedTask;
    public Task DenyAsync(string channel, string userId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// File-backed allowlist gate.
/// Persists to ~/.agentcli/allowlist.json — survives restarts.
/// </summary>
public sealed class AllowlistUserGate : IUserGate
{
    private readonly string _filePath;
    private readonly HashSet<string> _allowed  = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _denied   = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim   _lock      = new(1, 1);

    public AllowlistUserGate(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "allowlist.json");
        LoadFromDisk();
    }

    public async Task<AccessDecision> CheckAsync(ChannelAddress user, CancellationToken ct)
    {
        var key = Key(user.Channel, user.UserId);
        if (_denied.Contains(key))  return AccessDecision.Deny;
        if (_allowed.Contains(key)) return AccessDecision.Allow;
        return AccessDecision.PendingAuth;
    }

    public async Task AllowAsync(string channel, string userId, CancellationToken ct)
    {
        var key = Key(channel, userId);
        await _lock.WaitAsync(ct);
        try { _allowed.Add(key); _denied.Remove(key); await SaveAsync(); }
        finally { _lock.Release(); }
    }

    public async Task DenyAsync(string channel, string userId, CancellationToken ct)
    {
        var key = Key(channel, userId);
        await _lock.WaitAsync(ct);
        try { _denied.Add(key); _allowed.Remove(key); await SaveAsync(); }
        finally { _lock.Release(); }
    }

    private static string Key(string channel, string userId) =>
        $"{channel.ToLowerInvariant()}:{userId}";

    private void LoadFromDisk()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var data = System.Text.Json.JsonSerializer.Deserialize<AllowlistData>(json);
            if (data is null) return;
            foreach (var k in data.Allowed ?? []) _allowed.Add(k);
            foreach (var k in data.Denied  ?? []) _denied.Add(k);
        }
        catch { /* corrupt file — start fresh */ }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        var data = new AllowlistData(
            Allowed: _allowed.ToList(),
            Denied:  _denied.ToList());
        var json = System.Text.Json.JsonSerializer.Serialize(data,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_filePath, json);
    }

    private sealed record AllowlistData(
        [property: JsonPropertyName("allowed")] List<string>? Allowed,
        [property: JsonPropertyName("denied")]  List<string>? Denied);
}

// ─── Concurrency guard ────────────────────────────────────────────────────────

/// <summary>
/// Per-session semaphore — ensures only one agent turn runs at a time per user.
/// Prevents history corruption from concurrent messages.
/// </summary>
public sealed class ConcurrencyGuard : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim>
        _semaphores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Acquire the slot for the given session key.
    /// Returns an IDisposable that releases the slot when disposed.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(string sessionKey, CancellationToken ct)
    {
        var sem = _semaphores.GetOrAdd(sessionKey, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        return new Release(sem);
    }

    public void Dispose()
    {
        foreach (var sem in _semaphores.Values) sem.Dispose();
        _semaphores.Clear();
    }

    private sealed class Release : IDisposable
    {
        private readonly SemaphoreSlim _sem;
        private bool _disposed;
        public Release(SemaphoreSlim sem) => _sem = sem;
        public void Dispose() { if (!_disposed) { _disposed = true; _sem.Release(); } }
    }
}

// ─── Connector registry ───────────────────────────────────────────────────────

/// <summary>
/// Registry of all active channel connectors.
/// Mirrors Sivar.Os.Gateway.Core.IChannelRegistry.
/// </summary>
public sealed class ChannelConnectorRegistry
{
    private readonly Dictionary<string, IChannelConnector> _connectors =
        new(StringComparer.OrdinalIgnoreCase);

    public void Register(IChannelConnector connector) =>
        _connectors[connector.ChannelName] = connector;

    public IChannelConnector Get(string channelName) =>
        _connectors.TryGetValue(channelName, out var c) ? c
            : throw new InvalidOperationException($"No connector registered for channel '{channelName}'");

    public bool TryGet(string channelName, out IChannelConnector? connector) =>
        _connectors.TryGetValue(channelName, out connector);

    public IReadOnlyCollection<IChannelConnector> All => _connectors.Values;
}
