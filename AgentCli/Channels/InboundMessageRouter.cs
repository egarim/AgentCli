namespace AgentCli;

// ─── Routing options ──────────────────────────────────────────────────────────

public sealed class RoutingOptions
{
    /// <summary>
    /// In group chats, only respond when the bot is mentioned or the message
    /// is a direct reply to the bot. Matches OpenClaw's requireMention behaviour.
    /// </summary>
    public bool RequireMentionInGroups { get; init; } = true;

    /// <summary>
    /// Bot @username (without @). Used to detect mentions in group messages.
    /// e.g. "SivarOs_bot"
    /// </summary>
    public string? BotUsername { get; init; }

    /// <summary>
    /// Built-in slash commands that bypass the agent and are handled directly.
    /// </summary>
    public IReadOnlyList<IBotCommand> Commands { get; init; } = Array.Empty<IBotCommand>();
}

// ─── Bot commands ─────────────────────────────────────────────────────────────

/// <summary>A slash command handled before the agent sees the message.</summary>
public interface IBotCommand
{
    /// <summary>Command name without slash, e.g. "reset", "help".</summary>
    string Name { get; }

    /// <summary>Short description shown in /help.</summary>
    string Description { get; }

    Task<string?> ExecuteAsync(
        ChannelInboundMessage message,
        SessionManager sessions,
        CancellationToken ct);
}

// Built-in commands ─────────────────────────────────────────────────────────

public sealed class ResetCommand : IBotCommand
{
    public string Name        => "reset";
    public string Description => "Clear conversation history and start fresh";

    public async Task<string?> ExecuteAsync(
        ChannelInboundMessage message,
        SessionManager sessions,
        CancellationToken ct)
    {
        var key = ResolveSessionKey(message);
        await sessions.DeleteSessionAsync(key, ct);
        return "🔄 Conversation reset. Let's start fresh!";
    }

    private static string ResolveSessionKey(ChannelInboundMessage msg)
    {
        var chatId = msg.From.ChatId;
        var isGroup = chatId != null && chatId != msg.From.UserId;
        return isGroup
            ? SessionKey.Group(msg.From.Channel, chatId!)
            : SessionKey.Direct(msg.From.Channel, msg.From.UserId);
    }
}

public sealed class CompactCommand : IBotCommand
{
    public string Name        => "compact";
    public string Description => "Summarize and compress the conversation history";

    public async Task<string?> ExecuteAsync(
        ChannelInboundMessage message,
        SessionManager sessions,
        CancellationToken ct)
    {
        // Force compact by sending a system-style message that triggers the threshold
        // Simplest approach: just send an internal signal to the session manager
        return "🗜️ Compaction will happen automatically when the conversation grows long.\n" +
               "Use /reset to clear history completely.";
    }
}

public sealed class HelpCommand : IBotCommand
{
    private readonly IReadOnlyList<IBotCommand> _allCommands;

    public string Name        => "help";
    public string Description => "Show available commands";

    public HelpCommand(IReadOnlyList<IBotCommand> allCommands)
        => _allCommands = allCommands;

    public Task<string?> ExecuteAsync(
        ChannelInboundMessage message,
        SessionManager sessions,
        CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("📋 <b>Available commands:</b>");
        sb.AppendLine();
        foreach (var cmd in _allCommands)
            sb.AppendLine($"/{cmd.Name} — {cmd.Description}");
        return Task.FromResult<string?>(sb.ToString());
    }
}

public sealed class StatusCommand : IBotCommand
{
    public string Name        => "status";
    public string Description => "Show current session info";

    public async Task<string?> ExecuteAsync(
        ChannelInboundMessage message,
        SessionManager sessions,
        CancellationToken ct)
    {
        var key = SessionKey.Direct(message.From.Channel, message.From.UserId);
        var session = await sessions.GetSessionAsync(key, ct);
        return $"📊 <b>Session status</b>\n" +
               $"Channel: {message.From.Channel}\n" +
               $"Session key: <code>{key}</code>\n" +
               $"Messages: {session.Messages.Count}\n" +
               $"Compactions: {session.CompactionCount}\n" +
               $"Last updated: {session.UpdatedAt:u}";
    }
}

// ─── Inbound message router ───────────────────────────────────────────────────

/// <summary>
/// Routes inbound messages from any channel to the SessionManager.
///
/// Responsibilities:
///   1. User gate check (allow/deny/pending-auth)
///   2. Group mention gating (RequireMentionInGroups)
///   3. Slash command dispatch (before agent sees message)
///   4. Typing indicator lifecycle
///   5. Concurrency guard (one turn per session)
///   6. SessionManager.RunAsync → agent turn
///   7. Response chunking + send back via connector
///   8. Error reply on failure
///
/// This mirrors OpenClaw's MessageRouter + mention gating + delivery pipeline.
/// Also compatible with Sivar.Os.Gateway.Core.MessageRouter (same InboundMessage shape).
/// </summary>
public sealed class InboundMessageRouter : IDisposable
{
    private readonly SessionManager        _sessions;
    private readonly ChannelConnectorRegistry _registry;
    private readonly IUserGate             _gate;
    private readonly ConcurrencyGuard      _concurrency;
    private readonly RoutingOptions        _options;
    private readonly Dictionary<string, IBotCommand> _commands;

    public InboundMessageRouter(
        SessionManager           sessions,
        ChannelConnectorRegistry registry,
        IUserGate?               gate    = null,
        RoutingOptions?          options = null)
    {
        _sessions    = sessions;
        _registry    = registry;
        _gate        = gate    ?? AllowAllUserGate.Instance;
        _concurrency = new ConcurrencyGuard();
        _options     = options ?? new RoutingOptions();

        // Build command table (include /help last so it lists the others)
        var cmds = new List<IBotCommand>(_options.Commands)
        {
            new ResetCommand(),
            new CompactCommand(),
            new StatusCommand(),
        };
        cmds.Add(new HelpCommand(cmds.AsReadOnly()));
        _commands = cmds.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    // ─── Main entry point ─────────────────────────────────────────────────────

    public async Task OnInboundAsync(ChannelInboundMessage msg, CancellationToken ct)
    {
        // 1. User gate
        var access = await _gate.CheckAsync(msg.From, ct);
        if (access == AccessDecision.Deny)
        {
            await ReplyAsync(msg, "⛔ Access denied.", ct);
            return;
        }
        if (access == AccessDecision.PendingAuth)
        {
            await ReplyAsync(msg,
                "👋 You're not authorized yet. Contact the bot admin to get access.", ct);
            return;
        }

        // 2. Mention gating (groups only)
        if (!ShouldRespond(msg)) return;

        // 3. Slash command check
        var text = msg.Text.Trim();
        if (text.StartsWith('/'))
        {
            await HandleCommandAsync(msg, text, ct);
            return;
        }

        // 4. Agent turn (with concurrency guard)
        var sessionKey = ResolveSessionKey(msg);
        using var slot = await _concurrency.AcquireAsync(sessionKey, ct);
        await RunAgentTurnAsync(msg, sessionKey, ct);
    }

    // ─── Group mention gating ─────────────────────────────────────────────────

    private bool ShouldRespond(ChannelInboundMessage msg)
    {
        if (!_options.RequireMentionInGroups) return true;

        var chatId  = msg.From.ChatId;
        var isGroup = chatId != null && chatId != msg.From.UserId;
        if (!isGroup) return true; // DMs always respond

        // Group: respond if bot is mentioned or it's a reply to bot
        var text = msg.Text;
        var botMention = _options.BotUsername != null &&
                         text.Contains($"@{_options.BotUsername}", StringComparison.OrdinalIgnoreCase);
        var isReply    = msg.ReplyToMessageId != null;

        return botMention || isReply;
    }

    // ─── Command dispatch ─────────────────────────────────────────────────────

    private async Task HandleCommandAsync(
        ChannelInboundMessage msg,
        string                text,
        CancellationToken     ct)
    {
        // Parse "/reset@BotName args" → "reset"
        var parts       = text.TrimStart('/').Split(' ', '@');
        var commandName = parts[0].ToLowerInvariant();

        if (_commands.TryGetValue(commandName, out var cmd))
        {
            var reply = await cmd.ExecuteAsync(msg, _sessions, ct);
            if (reply != null)
                await ReplyAsync(msg, reply, ct);
        }
        else
        {
            await ReplyAsync(msg, $"❓ Unknown command /{commandName}. Try /help.", ct);
        }
    }

    // ─── Agent turn ───────────────────────────────────────────────────────────

    private async Task RunAgentTurnAsync(
        ChannelInboundMessage msg,
        string                sessionKey,
        CancellationToken     ct)
    {
        // Get the connector for typing indicator
        ITypingIndicator typing = NullTypingIndicator.Instance;
        if (_registry.TryGet(msg.From.Channel, out var connector) &&
            connector is ITypingIndicator ti)
        {
            typing = ti;
        }

        await typing.StartAsync(msg.From, ct);
        try
        {
            var response = await _sessions.RunAsync(sessionKey, msg.Text, ct);
            await typing.StopAsync(msg.From, ct);
            await ReplyChunkedAsync(msg, response, ct);
        }
        catch (Exception ex)
        {
            await typing.StopAsync(msg.From, ct);
            await ReplyAsync(msg, "⚠️ Sorry, something went wrong. Please try again.", ct);
            throw; // let caller log
        }
    }

    // ─── Reply helpers ────────────────────────────────────────────────────────

    private async Task ReplyAsync(
        ChannelInboundMessage msg,
        string                text,
        CancellationToken     ct)
    {
        if (!_registry.TryGet(msg.From.Channel, out var connector)) return;
        var outbound = new ChannelOutboundMessage(To: msg.From, Text: text);
        await connector!.SendAsync(outbound, ct);
    }

    /// <summary>
    /// Format + chunk the response for the target channel, send each chunk sequentially.
    /// Uses FormatterRegistry for channel-aware formatting and chunking.
    /// </summary>
    private async Task ReplyChunkedAsync(
        ChannelInboundMessage msg,
        string                text,
        CancellationToken     ct)
    {
        if (!_registry.TryGet(msg.From.Channel, out var connector)) return;

        var formatted = FormatterRegistry.Default.Format(text, msg.From.Channel);
        var chunks    = FormatterRegistry.Default.Chunk(formatted.Text ?? text, msg.From.Channel);

        foreach (var chunk in chunks)
        {
            var outbound = new ChannelOutboundMessage(To: msg.From, Text: chunk.Text);
            await connector!.SendAsync(outbound, ct);

            // Small delay between chunks to avoid flood limits
            if (chunks.Count > 1)
                await Task.Delay(300, ct);
        }
    }

    private static string ResolveSessionKey(ChannelInboundMessage msg)
    {
        var chatId  = msg.From.ChatId;
        var isGroup = chatId != null && chatId != msg.From.UserId;
        return isGroup
            ? SessionKey.Group(msg.From.Channel, chatId!)
            : SessionKey.Direct(msg.From.Channel, msg.From.UserId);
    }

    public void Dispose() => _concurrency.Dispose();
}
