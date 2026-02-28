using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCli;

/// <summary>
/// Discord channel connector — uses Discord HTTP API (no gateway WebSocket).
///
/// Send path:  POST /channels/{channelId}/messages
/// Receive:    Webhook or your own gateway WebSocket integration;
///             call OnWebhookInboundAsync() from your controller.
///
/// Discord webhooks post to your endpoint with a JSON Update object.
/// For full slash-command + DM support you need a Discord Application with
/// the Message Content intent enabled.
///
/// Note: For a full gateway (WebSocket) connection, integrate Discord.Net or DSharpPlus
/// and call OnWebhookInboundAsync with normalized messages.
/// </summary>
public sealed class DiscordConnector : IChannelConnector
{
    private readonly HttpClient _http;
    private readonly string     _botToken;
    private readonly ILogger?   _log;

    private Func<ChannelInboundMessage, CancellationToken, Task>? _handler;

    private const string ApiBase = "https://discord.com/api/v10";
    private const int    MaxChars = 2000;

    public string ChannelName => "discord";

    public DiscordConnector(string botToken, ILogger? logger = null)
    {
        _botToken = botToken;
        _log      = logger;
        _http     = new HttpClient { BaseAddress = new Uri(ApiBase) };
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bot", botToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentCli/1.0");
    }

    // ─── IChannelConnector ────────────────────────────────────────────────────

    public Task StartAsync(
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        _handler = onMessage;
        _log?.LogInformation("Discord connector ready (webhook/controller mode).");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _handler = null; return Task.CompletedTask; }

    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        // Use ChatId as the Discord channel ID (Discord channels are not user-addressed)
        var channelId = message.To.ChatId ?? message.To.UserId;

        // Chunk at Discord 2000-char limit
        var chunks = ChunkText(message.Text, MaxChars);

        foreach (var chunk in chunks)
        {
            var payload = new { content = chunk };
            var json    = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try
            {
                var response = await _http.PostAsync($"/channels/{channelId}/messages", content, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(ct);
                    _log?.LogError("Discord API error {Status}: {Error}", response.StatusCode, err);
                    response.EnsureSuccessStatusCode();
                }
                _log?.LogInformation("Discord message sent to channel {ChannelId}", channelId);

                if (chunks.Count > 1) await Task.Delay(300, ct);
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Failed to send Discord message to {ChannelId}", channelId);
                throw;
            }
        }
    }

    // ─── Webhook inbound pump ─────────────────────────────────────────────────

    /// <summary>
    /// Pump a normalized inbound message (from your Discord gateway or webhook handler).
    ///
    /// When using Discord gateway (WebSocket), normalize the MESSAGE_CREATE event:
    ///   - UserId   = author.id
    ///   - ChatId   = channel_id (for guilds)
    ///   - Text     = content
    ///   - Metadata = { "guildId", "username", "discriminator" }
    /// </summary>
    public async Task OnWebhookInboundAsync(
        DiscordInboundDto dto,
        CancellationToken ct)
    {
        if (_handler is null) return;

        var isDm    = string.IsNullOrEmpty(dto.GuildId);
        var msg     = new ChannelInboundMessage(
            MessageId:        dto.MessageId,
            From:             new ChannelAddress(
                                  Channel: "discord",
                                  UserId:  dto.AuthorId,
                                  ChatId:  isDm ? null : dto.ChannelId),
            Timestamp:        DateTimeOffset.TryParse(dto.Timestamp, out var ts)
                                  ? ts : DateTimeOffset.UtcNow,
            Text:             dto.Content,
            ReplyToMessageId: dto.ReplyToMessageId,
            Metadata: new Dictionary<string, string>
            {
                ["username"] = dto.Username ?? "",
                ["guildId"]  = dto.GuildId  ?? "",
                ["channelId"]= dto.ChannelId,
            });

        try { await _handler(msg, ct); }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error processing Discord message from {AuthorId}", dto.AuthorId);
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static IReadOnlyList<string> ChunkText(string text, int maxChars)
    {
        if (text.Length <= maxChars) return [text];
        var chunks    = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxChars) { chunks.Add(remaining.ToString()); break; }
            var window = remaining[..maxChars];
            var cut    = window.LastIndexOf('\n');
            if (cut < maxChars / 2) cut = maxChars;
            chunks.Add(remaining[..cut].ToString());
            remaining = remaining[cut..].TrimStart('\n');
        }
        return chunks;
    }
}

/// <summary>
/// Normalized Discord inbound DTO.
/// Fill from a Discord gateway MESSAGE_CREATE event or an interactions webhook.
/// </summary>
public sealed record DiscordInboundDto(
    string  MessageId,
    string  AuthorId,
    string  ChannelId,
    string  Content,
    string  Timestamp,
    string? GuildId           = null,
    string? Username          = null,
    string? ReplyToMessageId  = null
);
