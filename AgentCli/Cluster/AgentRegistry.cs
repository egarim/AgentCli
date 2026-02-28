using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace AgentCli;

/// <summary>
/// Cluster options — loaded from appsettings.json / environment.
/// All fields have safe defaults so single-server deployments work with zero config.
/// </summary>
public sealed class ClusterOptions
{
    /// <summary>
    /// Human-readable role name for this agent.
    /// Combined with a stable machineId hash to form agent_id.
    /// e.g. "sivar-main" → agent_id "sivar-main-a3f8c291"
    /// </summary>
    public string AgentName { get; set; } = "agent";

    /// <summary>
    /// Host:port where this agent's AgentApiServer listens.
    /// Gateways POST /api/messages here.
    /// e.g. "http://154.12.236.61:5050"
    /// </summary>
    public string Host { get; set; } = "http://localhost:5050";

    /// <summary>
    /// Gateway base URL for outbound channel sends.
    /// Agent POSTs replies here; gateway forwards to Telegram/WhatsApp/etc.
    /// e.g. "https://bot.sivar.lat"
    /// Leave null for single-server mode (agent calls channel directly).
    /// </summary>
    public string? ReplyGateway { get; set; }

    /// <summary>Postgres connection string for shared cluster state.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>How often to update last_heartbeat in DB.</summary>
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Agents with last_heartbeat older than this are considered dead.</summary>
    public TimeSpan DeadThreshold { get; set; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// Registers this agent in the cluster and maintains a heartbeat.
///
/// On startup: INSERT INTO agentcli.agents (upsert) with status='active'
/// Every HeartbeatInterval: UPDATE last_heartbeat
/// On graceful shutdown: UPDATE status='draining', then 'stopped'
///
/// Gateways query agentcli.agents WHERE status='active'
/// to get the list of live hosts for round-robin forwarding.
/// </summary>
public sealed class AgentRegistry : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ClusterOptions   _options;
    private readonly string           _agentId;
    private readonly string           _version;

    private CancellationTokenSource? _cts;
    private Task?                    _heartbeatTask;

    public string AgentId => _agentId;

    public AgentRegistry(ClusterOptions options, string? version = null)
    {
        _options    = options;
        _version    = version ?? GetType().Assembly.GetName().Version?.ToString() ?? "0.0.0";
        _agentId    = BuildAgentId(options.AgentName);
        _dataSource = NpgsqlDataSource.Create(options.ConnectionString
            ?? throw new InvalidOperationException("ClusterOptions.ConnectionString is required"));
    }

    // ─── Lifecycle ────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct = default)
    {
        // Register / re-register this agent
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agentcli.agents
                (agent_id, agent_name, host, reply_gateway, version, started_at, last_heartbeat, status)
            VALUES ($1, $2, $3, $4, $5, NOW(), NOW(), 'active')
            ON CONFLICT (agent_id) DO UPDATE SET
                agent_name     = EXCLUDED.agent_name,
                host           = EXCLUDED.host,
                reply_gateway  = EXCLUDED.reply_gateway,
                version        = EXCLUDED.version,
                started_at     = NOW(),
                last_heartbeat = NOW(),
                status         = 'active'
            """;
        cmd.Parameters.AddWithValue(_agentId);
        cmd.Parameters.AddWithValue(_options.AgentName);
        cmd.Parameters.AddWithValue(_options.Host);
        cmd.Parameters.AddWithValue((object?)_options.ReplyGateway ?? DBNull.Value);
        cmd.Parameters.AddWithValue(_version);
        await cmd.ExecuteNonQueryAsync(ct);

        // Start heartbeat loop
        _cts           = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token), _cts.Token);

        Console.WriteLine($"[cluster] registered as {_agentId} at {_options.Host}");
    }

    /// <summary>Graceful drain — sets status='draining' so gateway stops routing new messages.</summary>
    public async Task DrainAsync(CancellationToken ct = default)
    {
        await SetStatusAsync("draining", ct);
        Console.WriteLine($"[cluster] {_agentId} draining...");
    }

    /// <summary>Full stop — sets status='stopped' and cancels heartbeat.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_cts != null) { await _cts.CancelAsync(); _cts.Dispose(); }
        if (_heartbeatTask != null) try { await _heartbeatTask; } catch { /* expected */ }
        await SetStatusAsync("stopped", ct);
        Console.WriteLine($"[cluster] {_agentId} stopped.");
    }

    // ─── Cluster queries ──────────────────────────────────────────────────────

    /// <summary>List all currently active agents (for gateway round-robin).</summary>
    public async Task<IReadOnlyList<AgentInfo>> GetActiveAgentsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT agent_id, agent_name, host, reply_gateway, version, last_heartbeat
            FROM agentcli.agents
            WHERE status = 'active'
              AND last_heartbeat > NOW() - $1::interval
            ORDER BY last_heartbeat DESC
            """;
        // Pass threshold as seconds string so Npgsql handles it cleanly
        cmd.Parameters.AddWithValue($"{_options.DeadThreshold.TotalSeconds} seconds");

        var agents = new List<AgentInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            agents.Add(new AgentInfo(
                AgentId:       reader.GetString(0),
                AgentName:     reader.GetString(1),
                Host:          reader.GetString(2),
                ReplyGateway:  reader.IsDBNull(3) ? null : reader.GetString(3),
                Version:       reader.IsDBNull(4) ? null : reader.GetString(4),
                LastHeartbeat: reader.GetFieldValue<DateTimeOffset>(5)
            ));
        }
        return agents;
    }

    /// <summary>Mark stale agents as dead (run periodically from any agent).</summary>
    public async Task ReapDeadAgentsAsync(CancellationToken ct = default)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agentcli.agents
            SET status = 'dead'
            WHERE status = 'active'
              AND last_heartbeat < NOW() - $1::interval
              AND agent_id != $2
            """;
        cmd.Parameters.AddWithValue($"{_options.DeadThreshold.TotalSeconds} seconds");
        cmd.Parameters.AddWithValue(_agentId);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0)
            Console.WriteLine($"[cluster] reaped {rows} dead agent(s)");
    }

    // ─── Agent ID generation ──────────────────────────────────────────────────

    /// <summary>
    /// Stable agent_id = "{name}-{machineId}"
    /// machineId = first 8 hex chars of SHA256(hostname + first MAC address)
    /// Same server = same machineId across restarts.
    /// </summary>
    public static string BuildAgentId(string agentName)
    {
        var hostname = Environment.MachineName.ToLowerInvariant();
        var mac      = GetFirstMacAddress();
        var raw      = $"{hostname}:{mac}";
        var hash     = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var suffix   = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        var name     = agentName.ToLowerInvariant().Trim();
        return $"{name}-{suffix}";
    }

    private static string GetFirstMacAddress()
    {
        try
        {
            return NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                         && n.OperationalStatus == OperationalStatus.Up)
                .Select(n => n.GetPhysicalAddress().ToString())
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) ?? "000000000000";
        }
        catch { return "000000000000"; }
    }

    // ─── Heartbeat loop ───────────────────────────────────────────────────────

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.HeartbeatInterval, ct);
                await BeatAsync(ct);
                // Opportunistically reap dead agents on every 5th beat
                // (any agent can do this — safe due to WHERE agent_id != my_id)
                if (Random.Shared.Next(5) == 0)
                    await ReapDeadAgentsAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[cluster] heartbeat error: {ex.Message}");
                // Don't crash — next iteration will retry
            }
        }
    }

    private async Task BeatAsync(CancellationToken ct)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE agentcli.agents SET last_heartbeat = NOW() WHERE agent_id = $1
            """;
        cmd.Parameters.AddWithValue(_agentId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task SetStatusAsync(string status, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var cmd  = conn.CreateCommand();
            cmd.CommandText = "UPDATE agentcli.agents SET status = $1 WHERE agent_id = $2";
            cmd.Parameters.AddWithValue(status);
            cmd.Parameters.AddWithValue(_agentId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { /* best-effort on shutdown */ }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _dataSource.DisposeAsync();
    }
}

/// <summary>Agent info from the registry — used by gateways for routing.</summary>
public sealed record AgentInfo(
    string          AgentId,
    string          AgentName,
    string          Host,
    string?         ReplyGateway,
    string?         Version,
    DateTimeOffset  LastHeartbeat
);
