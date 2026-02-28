namespace AgentCli;

/// <summary>
/// Registry of IOutputFormatter instances keyed by channel ID.
///
/// Default formatters are registered for all known channels.
/// Use Register() to override or add custom channels.
///
/// Usage:
///   var formatted = FormatterRegistry.Default.Format("**hello**", "telegram");
///   var chunks    = FormatterRegistry.Default.Chunk(longText, "telegram", maxChars: 4096);
/// </summary>
public class FormatterRegistry
{
    private readonly Dictionary<string, IOutputFormatter> _formatters =
        new(StringComparer.OrdinalIgnoreCase);

    // ─── Singleton ────────────────────────────────────────────────────────────

    private static readonly Lazy<FormatterRegistry> _default =
        new(() => CreateDefault());

    /// <summary>Pre-built registry with all built-in formatters registered.</summary>
    public static FormatterRegistry Default => _default.Value;

    // ─── Registration ─────────────────────────────────────────────────────────

    public void Register(IOutputFormatter formatter) =>
        _formatters[formatter.ChannelId] = formatter;

    public bool TryGet(string channelId, out IOutputFormatter? formatter) =>
        _formatters.TryGetValue(channelId, out formatter);

    public IOutputFormatter Get(string channelId) =>
        _formatters.TryGetValue(channelId, out var f)
            ? f
            : _formatters[ChannelIds.Plain]; // safe fallback

    public IReadOnlyList<IOutputFormatter> All =>
        _formatters.Values.OrderBy(f => f.ChannelId).ToList();

    // ─── Convenience methods ──────────────────────────────────────────────────

    /// <summary>Format markdown for a channel. Falls back to plain text if channel unknown.</summary>
    public FormattedMessage Format(string markdown, string channelId,
        TableMode? tableMode = null)
    {
        var formatter = Get(channelId);
        var mode      = tableMode ?? DefaultTableMode(channelId);
        return formatter.Format(markdown, mode);
    }

    /// <summary>Split into deliverable chunks for a channel.</summary>
    public IReadOnlyList<FormattedMessage> Chunk(string markdown, string channelId,
        int? maxChars = null, TableMode? tableMode = null)
    {
        var formatter = Get(channelId);
        var mode      = tableMode ?? DefaultTableMode(channelId);
        var limit     = maxChars  ?? DefaultMaxChars(channelId);
        return formatter.Chunk(markdown, limit, mode);
    }

    // ─── Channel defaults ─────────────────────────────────────────────────────

    /// <summary>
    /// Default table mode per channel.
    /// Mirrors OpenClaw's DEFAULT_TABLE_MODES map.
    /// </summary>
    public static TableMode DefaultTableMode(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            ChannelIds.Signal    => TableMode.Bullets,
            ChannelIds.WhatsApp  => TableMode.Bullets,
            ChannelIds.GoogleChat => TableMode.Bullets,
            ChannelIds.Slack     => TableMode.Bullets,
            _                    => TableMode.Code,
        };

    /// <summary>Default max message size per channel (chars).</summary>
    public static int DefaultMaxChars(string channelId) =>
        channelId.ToLowerInvariant() switch
        {
            ChannelIds.Telegram   => 4096,
            ChannelIds.Discord    => 2000,
            ChannelIds.WhatsApp   => 65536,
            ChannelIds.Slack      => 40000,
            ChannelIds.Signal     => 64000,
            _                     => int.MaxValue,
        };

    // ─── Factory ──────────────────────────────────────────────────────────────

    public static FormatterRegistry CreateDefault()
    {
        var reg = new FormatterRegistry();
        reg.Register(new TerminalFormatter());
        reg.Register(new PlainTextFormatter());
        reg.Register(new TelegramFormatter());
        reg.Register(new DiscordFormatter());
        reg.Register(new SignalFormatter());
        reg.Register(new WhatsAppFormatter());
        reg.Register(new SlackFormatter());
        reg.Register(new GoogleChatFormatter());
        reg.Register(new WebChatFormatter());
        return reg;
    }
}
