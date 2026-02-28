using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace AgentCli;

/// <summary>
/// Telegram channel connector.
///
/// Implements IChannelConnector + ITypingIndicator so InboundMessageRouter
/// picks up the typing loop automatically.
///
/// Two receive modes:
///   Long-polling (default) — StartAsync() starts the polling loop in background.
///   Webhook — call UseWebhookAsync() to register the URL; inbound updates are
///              pumped via OnWebhookUpdateAsync() from your controller.
///
/// Compatible with Sivar.Os.Gateway.Core.IChannelAdapter:
///   ChannelName = "telegram", SendAsync(ChannelOutboundMessage) works identically.
///
/// Features:
///   - sendChatAction("typing") loop every 4s while model thinks
///   - HTML parse_mode (matches TelegramFormatter)
///   - Text chunking at 4096 chars (Telegram hard limit)
///   - /setMyCommands registration on startup
///   - Group mention detection via entities
/// </summary>
public sealed class TelegramConnector : IChannelConnector, ITypingIndicator
{
    private readonly TelegramBotClient _client;
    private readonly string            _botToken;
    private readonly ILogger?          _log;

    // Typing: one CTS per active chat
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        _typingLoops = new();

    public string ChannelName => "telegram";

    public TelegramConnector(string botToken, ILogger? logger = null)
    {
        _botToken = botToken;
        _client   = new TelegramBotClient(botToken);
        _log      = logger;
    }

    // ─── IChannelConnector.StartAsync — long-polling ──────────────────────────

    public async Task StartAsync(
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        // Register bot commands in Telegram UI
        await RegisterBotCommandsAsync(ct);

        var receiverOptions = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery],
            DropPendingUpdates = true,
        };

        _log?.LogInformation("Telegram connector starting long-polling...");

        _client.StartReceiving(
            updateHandler:      (bot, update, token) => HandleUpdateAsync(onMessage, update, token),
            errorHandler:        (bot, ex,     token) =>
            {
                _log?.LogError(ex, "Telegram polling error");
                return Task.CompletedTask;
            },
            receiverOptions: receiverOptions,
            cancellationToken: ct);

        _log?.LogInformation("Telegram connector started (polling).");
    }

    public Task StopAsync(CancellationToken ct)
    {
        _log?.LogInformation("Telegram connector stopped.");
        return Task.CompletedTask;
    }

    // ─── Webhook mode ─────────────────────────────────────────────────────────

    /// <summary>
    /// Register a webhook URL with Telegram. Call this instead of StartAsync
    /// when running in webhook mode (e.g. inside ASP.NET pipeline).
    /// </summary>
    public async Task UseWebhookAsync(string webhookUrl, CancellationToken ct = default)
    {
        await _client.SetWebhook(webhookUrl, cancellationToken: ct);
        await RegisterBotCommandsAsync(ct);
        _log?.LogInformation("Telegram webhook registered: {Url}", webhookUrl);
    }

    /// <summary>
    /// Pump a raw Telegram Update from your webhook controller into the router.
    /// Usage: await connector.OnWebhookUpdateAsync(update, onMessage, ct);
    /// </summary>
    public Task OnWebhookUpdateAsync(
        Update update,
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        CancellationToken ct)
        => HandleUpdateAsync(onMessage, update, ct);

    // ─── IChannelConnector.SendAsync ──────────────────────────────────────────

    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        var chatId = message.To.ChatId ?? message.To.UserId;

        // Chunk at Telegram's 4096-char hard limit
        var chunks = ChunkText(message.Text, 4096);

        foreach (var chunk in chunks)
        {
            try
            {
                await _client.SendMessage(
                    chatId:    chatId,
                    text:      chunk,
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);

                if (chunks.Count > 1)
                    await Task.Delay(300, ct); // flood avoidance between chunks
            }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Failed to send Telegram message to {ChatId}", chatId);
                throw;
            }
        }
    }

    // ─── ITypingIndicator ─────────────────────────────────────────────────────

    public async Task StartAsync(ChannelAddress target, CancellationToken ct)
    {
        var key = TypingKey(target);
        var cts = new CancellationTokenSource();
        _typingLoops.TryAdd(key, cts);

        // Fire-and-forget loop — sendChatAction("typing") every 4 seconds
        _ = Task.Run(async () =>
        {
            var chatId = target.ChatId ?? target.UserId;
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    await _client.SendChatAction(
                        chatId:    chatId,
                        action:    ChatAction.Typing,
                        cancellationToken: cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _log?.LogDebug(ex, "Typing indicator send failed (non-fatal)");
                    break;
                }
                try { await Task.Delay(4_000, cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        }, cts.Token);

        await Task.CompletedTask;
    }

    public async Task StopAsync(ChannelAddress target, CancellationToken ct)
    {
        var key = TypingKey(target);
        if (_typingLoops.TryRemove(key, out var cts))
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var cts in _typingLoops.Values)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }
        _typingLoops.Clear();
    }

    // ─── Update handling ──────────────────────────────────────────────────────

    private async Task HandleUpdateAsync(
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        Update update,
        CancellationToken ct)
    {
        if (update.Message is not { } msg) return;
        if (string.IsNullOrWhiteSpace(msg.Text))  return;
        if (msg.From == null) return;

        var isGroup = msg.Chat.Type is ChatType.Group or ChatType.Supergroup or ChatType.Channel;
        var chatId  = msg.Chat.Id.ToString();
        var userId  = msg.From.Id.ToString();

        // Detect reply-to (for group mention gating)
        var replyToId = msg.ReplyToMessage?.MessageId.ToString();

        var inbound = new ChannelInboundMessage(
            MessageId:         $"telegram:{msg.MessageId}",
            From:              new ChannelAddress(
                                   Channel: "telegram",
                                   UserId:  userId,
                                   ChatId:  isGroup ? chatId : null),
            Timestamp:         new DateTimeOffset(msg.Date, TimeSpan.Zero),
            Text:              msg.Text,
            ReplyToMessageId:  replyToId,
            Metadata: new Dictionary<string, string>
            {
                ["firstName"] = msg.From.FirstName ?? "",
                ["lastName"]  = msg.From.LastName  ?? "",
                ["username"]  = msg.From.Username  ?? "",
                ["chatType"]  = msg.Chat.Type.ToString().ToLowerInvariant(),
            });

        try
        {
            await onMessage(inbound, ct);
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Error processing Telegram message from {UserId}", userId);
        }
    }

    // ─── Bot command registration ─────────────────────────────────────────────

    private async Task RegisterBotCommandsAsync(CancellationToken ct)
    {
        try
        {
            await _client.SetMyCommands(
                new[]
                {
                    new BotCommand { Command = "help",    Description = "Show available commands" },
                    new BotCommand { Command = "reset",   Description = "Clear conversation history" },
                    new BotCommand { Command = "compact", Description = "Compress conversation history" },
                    new BotCommand { Command = "status",  Description = "Show session info" },
                },
                cancellationToken: ct);
            _log?.LogInformation("Telegram bot commands registered.");
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Failed to register bot commands (non-fatal).");
        }
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string TypingKey(ChannelAddress a) =>
        $"{a.Channel}:{a.ChatId ?? a.UserId}";

    internal static IReadOnlyList<string> ChunkText(string text, int maxChars)
    {
        if (text.Length <= maxChars) return [text];

        var chunks = new List<string>();
        var remaining = text.AsSpan();
        while (remaining.Length > 0)
        {
            if (remaining.Length <= maxChars)
            {
                chunks.Add(remaining.ToString());
                break;
            }

            // Try to split at a newline within the window
            var window = remaining[..maxChars];
            var cut = window.LastIndexOf('\n');
            if (cut < maxChars / 2) cut = maxChars; // no good split point, hard cut

            chunks.Add(remaining[..cut].ToString());
            remaining = remaining[cut..].TrimStart('\n');
        }
        return chunks;
    }
}
