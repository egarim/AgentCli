using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCli;

/// <summary>
/// Signal channel connector — via signal-cli REST API.
///
/// Requires signal-cli running in REST mode:
///   signal-cli -a +15551234567 daemon --http localhost:8080
///   See: https://github.com/AsamK/signal-cli
///
/// Send:    POST {baseUrl}/v2/send
/// Receive: GET  {baseUrl}/v1/receive/{number} (long-poll) or webhook
///
/// Note: Signal uses plain text with optional StyleRanges — handled by SignalFormatter.
/// </summary>
public sealed class SignalConnector : IChannelConnector
{
    private readonly HttpClient _http;
    private readonly string     _accountNumber; // your registered Signal number, e.g. "+15551234567"
    private readonly ILogger?   _log;
    private Func<ChannelInboundMessage, CancellationToken, Task>? _handler;
    private Task?               _pollTask;
    private CancellationTokenSource? _pollCts;

    public string ChannelName => "signal";

    public SignalConnector(string baseUrl, string accountNumber, ILogger? logger = null)
    {
        _accountNumber = accountNumber;
        _log           = logger;
        _http          = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = TimeSpan.FromSeconds(60),
        };
    }

    // ─── IChannelConnector ────────────────────────────────────────────────────

    public Task StartAsync(
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        _handler  = onMessage;
        _pollCts  = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pollTask = Task.Run(() => PollLoopAsync(_pollCts.Token), _pollCts.Token);
        _log?.LogInformation("Signal connector started (long-polling {Account}).", _accountNumber);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _handler = null;
        if (_pollCts != null) { await _pollCts.CancelAsync(); _pollCts.Dispose(); }
        if (_pollTask != null) try { await _pollTask; } catch { /* expected */ }
        _log?.LogInformation("Signal connector stopped.");
    }

    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        var payload = new
        {
            message    = message.Text,
            number     = _accountNumber,
            recipients = new[] { message.To.UserId },
        };

        var json    = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("/v2/send", content, ct);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync(ct);
            _log?.LogError("Signal send failed {Status}: {Err}", response.StatusCode, err);
            response.EnsureSuccessStatusCode();
        }
    }

    // ─── Long-poll loop ───────────────────────────────────────────────────────

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url  = $"/v1/receive/{Uri.EscapeDataString(_accountNumber)}";
                var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode) { await Task.Delay(5_000, ct); continue; }

                var body    = await resp.Content.ReadAsStringAsync(ct);
                var updates = JsonSerializer.Deserialize<List<SignalEnvelope>>(body);
                if (updates == null) { await Task.Delay(1_000, ct); continue; }

                foreach (var env in updates)
                    if (_handler != null)
                        await DispatchEnvelopeAsync(env, ct);

                if (updates.Count == 0) await Task.Delay(1_000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Signal poll error — retrying in 5s.");
                await Task.Delay(5_000, ct);
            }
        }
    }

    private async Task DispatchEnvelopeAsync(SignalEnvelope env, CancellationToken ct)
    {
        var dm = env.Envelope?.DataMessage;
        if (dm == null || string.IsNullOrWhiteSpace(dm.Message)) return;

        var source = env.Envelope?.Source ?? env.Envelope?.SourceNumber ?? "";
        var msg    = new ChannelInboundMessage(
            MessageId: $"signal:{env.Envelope?.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
            From:      new ChannelAddress("signal", source),
            Timestamp: env.Envelope?.Timestamp is { } ts
                ? DateTimeOffset.FromUnixTimeMilliseconds(ts)
                : DateTimeOffset.UtcNow,
            Text: dm.Message);

        try { await _handler!(msg, ct); }
        catch (Exception ex) { _log?.LogError(ex, "Error handling Signal message from {Src}", source); }
    }

    // ─── Signal-cli REST models ───────────────────────────────────────────────

    private sealed class SignalEnvelope
    {
        [JsonPropertyName("envelope")] public EnvelopeData? Envelope { get; set; }
    }
    private sealed class EnvelopeData
    {
        [JsonPropertyName("source")]       public string? Source       { get; set; }
        [JsonPropertyName("sourceNumber")] public string? SourceNumber { get; set; }
        [JsonPropertyName("timestamp")]    public long?   Timestamp    { get; set; }
        [JsonPropertyName("dataMessage")] public DataMessageData? DataMessage { get; set; }
    }
    private sealed class DataMessageData
    {
        [JsonPropertyName("message")] public string? Message { get; set; }
    }
}
