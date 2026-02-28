namespace AgentCli;

// ─── Channel identifiers ──────────────────────────────────────────────────────

/// <summary>
/// Known channel IDs — mirrors OpenClaw's channel list.
/// Use these constants instead of raw strings.
/// </summary>
public static class ChannelIds
{
    public const string Terminal  = "terminal";   // local console (ANSI-capable)
    public const string Telegram  = "telegram";   // HTML parse_mode
    public const string Discord   = "discord";    // native markdown passthrough
    public const string Signal    = "signal";     // plain text + style ranges
    public const string WhatsApp  = "whatsapp";   // limited markdown (*bold* ~strike~)
    public const string Slack     = "slack";      // mrkdwn syntax
    public const string GoogleChat = "googlechat";
    public const string WebChat   = "webchat";    // internal web UI
    public const string Plain     = "plain";      // strip everything

    /// <summary>Channels that support some form of markdown.</summary>
    public static readonly IReadOnlySet<string> MarkdownCapable = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        Terminal, Telegram, Discord, Signal, Slack, GoogleChat, WebChat
    };

    public static bool IsMarkdownCapable(string channelId) =>
        MarkdownCapable.Contains(channelId);
}

// ─── Table mode ───────────────────────────────────────────────────────────────

/// <summary>
/// How to render markdown tables for a channel.
/// Mirrors OpenClaw's resolveMarkdownTableMode().
/// </summary>
public enum TableMode
{
    /// <summary>Render as a fenced code block (monospace, safe everywhere).</summary>
    Code,
    /// <summary>Convert rows to bullet lists (Signal, WhatsApp — no monospace tables).</summary>
    Bullets,
    /// <summary>Drop the table entirely.</summary>
    Off,
}

// ─── Formatted output ─────────────────────────────────────────────────────────

/// <summary>
/// The result of formatting a message for a specific channel.
/// Some channels (Signal) carry inline style ranges alongside the text.
/// </summary>
public record FormattedMessage(
    string            Text,
    /// <summary>
    /// Optional inline style ranges (Signal protocol).
    /// Each entry: start offset, length, style name ("bold"/"italic"/etc.)
    /// </summary>
    IReadOnlyList<SignalStyleRange>? StyleRanges = null
);

/// <summary>Signal-style inline formatting range.</summary>
public record SignalStyleRange(int Start, int Length, string Style);

// ─── Interface ────────────────────────────────────────────────────────────────

/// <summary>
/// Formats agent output for a specific delivery channel.
///
/// OpenClaw pipeline:
///   markdown string
///     → markdownToIR()        (semantic IR: text + style spans + links)
///     → renderWithMarkers()   (channel-specific open/close tags or plain)
///     → FormattedMessage
/// </summary>
public interface IOutputFormatter
{
    /// <summary>Channel this formatter targets (matches ChannelIds constants).</summary>
    string ChannelId { get; }

    /// <summary>
    /// Format a markdown string for the target channel.
    /// Returns channel-ready text (HTML for Telegram, plain for WhatsApp, etc.)
    /// </summary>
    FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Code);

    /// <summary>
    /// Split a long message into chunks the channel can deliver.
    /// Default: return as single chunk. Override for channels with size limits.
    /// </summary>
    IReadOnlyList<FormattedMessage> Chunk(string markdown, int maxChars = int.MaxValue,
        TableMode tableMode = TableMode.Code);
}
