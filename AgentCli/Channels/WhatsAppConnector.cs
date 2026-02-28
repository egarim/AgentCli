using System.Text.Json;
using System.Text;

namespace AgentCli;

/// <summary>
/// WhatsApp channel connector — outbound via HTTP sidecar.
///
/// Send path: POST {sidecarBaseUrl}/send with { toUserId, chatId, text }
/// Receive path: inbound messages are pushed to your app via a webhook;
///   call OnWebhookInboundAsync() from your HTTP controller.
///
/// Fully compatible with Sivar.Os.Gateway.Infrastructure.Adapters.WhatsAppWebAdapter —
/// uses the same /send endpoint and payload shape.
///
/// The sidecar is typically a Node.js Baileys bridge (wa-sidecar) or similar.
/// </summary>
public sealed class WhatsAppConnector : IChannelConnector
{
    private readonly HttpClient _http;
    private readonly ILogger?   _log;
    private Func<ChannelInboundMessage, CancellationToken, Task>? _handler;

    public string ChannelName => "whatsapp";

    public WhatsAppConnector(HttpClient httpClient, ILogger? logger = null)
    {
        _http = httpClient;
        _log  = logger;
    }

    public WhatsAppConnector(string sidecarBaseUrl, ILogger? logger = null)
        : this(new HttpClient { BaseAddress = new Uri(sidecarBaseUrl), Timeout = TimeSpan.FromSeconds(30) }, logger)
    { }

    // ─── IChannelConnector ────────────────────────────────────────────────────

    public Task StartAsync(
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        _handler = onMessage;
        _log?.LogInformation("WhatsApp connector ready (webhook mode — waiting for inbound).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        _handler = null;
        return Task.CompletedTask;
    }

    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        var payload = new
        {
            toUserId = message.To.UserId,
            chatId   = message.To.ChatId ?? message.To.UserId,
            text     = message.Text,
        };

        var json    = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            var response = await _http.PostAsync("/send", content, ct);
            response.EnsureSuccessStatusCode();
            _log?.LogInformation("WhatsApp message sent to {UserId}", message.To.UserId);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to send WhatsApp message to {UserId}", message.To.UserId);
            throw;
        }
    }

    // ─── Webhook inbound pump ─────────────────────────────────────────────────

    /// <summary>
    /// Call this from your ASP.NET webhook controller when a WhatsApp inbound arrives.
    /// Compatible with Sivar.Os.Gateway.Controllers.WhatsAppInboundController DTO shape.
    /// </summary>
    public async Task OnWebhookInboundAsync(
        WhatsAppInboundDto dto,
        CancellationToken  ct)
    {
        if (_handler is null)
        {
            _log?.LogWarning("WhatsApp inbound received but no handler registered (call StartAsync first).");
            return;
        }

        var msg = new ChannelInboundMessage(
            MessageId: dto.MessageId,
            From:      new ChannelAddress("whatsapp", dto.FromUserId, dto.ChatId),
            Timestamp: DateTimeOffset.TryParse(dto.Timestamp, out var ts) ? ts : DateTimeOffset.UtcNow,
            Text:      dto.Text);

        try
        {
            await _handler(msg, ct);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error processing WhatsApp message from {UserId}", dto.FromUserId);
        }
    }
}

/// <summary>
/// WhatsApp inbound DTO.
/// Matches Sivar.Os.Gateway.Controllers.WhatsAppInboundDto exactly for drop-in compatibility.
/// </summary>
public sealed record WhatsAppInboundDto(
    string MessageId,
    string FromUserId,
    string ChatId,
    string Text,
    string Timestamp
);
