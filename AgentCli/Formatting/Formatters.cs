using System.Text.RegularExpressions;

namespace AgentCli;

// ─── Base ─────────────────────────────────────────────────────────────────────

/// <summary>
/// Base formatter — handles chunking; subclasses override Format().
/// </summary>
public abstract class BaseFormatter : IOutputFormatter
{
    public abstract string ChannelId { get; }

    public abstract FormattedMessage Format(string markdown,
        TableMode tableMode = TableMode.Code);

    public virtual IReadOnlyList<FormattedMessage> Chunk(string markdown,
        int maxChars = int.MaxValue, TableMode tableMode = TableMode.Code)
    {
        var msg = Format(markdown, tableMode);
        if (maxChars == int.MaxValue || msg.Text.Length <= maxChars)
            return [msg];

        // Split on paragraph boundaries first, then hard-cut
        var chunks = new List<FormattedMessage>();
        var text   = msg.Text;
        while (text.Length > maxChars)
        {
            var cut = FindSplitPoint(text, maxChars);
            chunks.Add(new FormattedMessage(text[..cut].TrimEnd()));
            text = text[cut..].TrimStart();
        }
        if (text.Length > 0) chunks.Add(new FormattedMessage(text));
        return chunks;
    }

    private static int FindSplitPoint(string text, int max)
    {
        // Prefer paragraph break
        var para = text.LastIndexOf("\n\n", max, StringComparison.Ordinal);
        if (para > max / 2) return para;
        // Prefer newline
        var nl = text.LastIndexOf('\n', max);
        if (nl > max / 2) return nl;
        // Hard cut
        return max;
    }
}

// ─── Terminal ─────────────────────────────────────────────────────────────────

/// <summary>
/// Terminal formatter — preserves markdown as-is (terminals render it fine
/// for reading, and code blocks stay readable). Strips nothing.
/// </summary>
public class TerminalFormatter : BaseFormatter
{
    public override string ChannelId => ChannelIds.Terminal;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Code)
        => new(markdown ?? "");
}

// ─── Plain text ───────────────────────────────────────────────────────────────

/// <summary>
/// Plain text formatter — strips all markdown.
/// Use for SMS, basic email, or any channel with no formatting support.
/// Mirrors OpenClaw's stripMarkdown().
/// </summary>
public class PlainTextFormatter : BaseFormatter
{
    public override string ChannelId => ChannelIds.Plain;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Code)
        => new(MarkdownProcessor.ToPlainText(
            MarkdownProcessor.ConvertTables(markdown ?? "", tableMode)));
}

// ─── Telegram (HTML) ──────────────────────────────────────────────────────────

/// <summary>
/// Telegram HTML formatter.
/// parse_mode: "HTML"  — uses &lt;b&gt;, &lt;i&gt;, &lt;code&gt;, &lt;pre&gt;, &lt;tg-spoiler&gt;, &lt;blockquote&gt;
///
/// Mirrors OpenClaw's renderTelegramHtml() + markdownToTelegramHtml().
/// Telegram message limit: 4096 chars.
/// </summary>
public class TelegramFormatter : BaseFormatter
{
    private static readonly IReadOnlyDictionary<string, MarkdownProcessor.StyleMarker> Markers =
        new Dictionary<string, MarkdownProcessor.StyleMarker>
        {
            ["bold"]          = new("<b>",            "</b>"),
            ["italic"]        = new("<i>",            "</i>"),
            ["strikethrough"] = new("<s>",            "</s>"),
            ["code"]          = new("<code>",         "</code>"),
            ["code_block"]    = new("<pre><code>",    "</code></pre>"),
            ["spoiler"]       = new("<tg-spoiler>",   "</tg-spoiler>"),
            ["blockquote"]    = new("<blockquote>",   "</blockquote>"),
        };

    public override string ChannelId => ChannelIds.Telegram;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Code)
    {
        var ir  = MarkdownProcessor.Parse(markdown ?? "", tableMode,
            blockquotePrefix: "", headingAsBold: false);
        var html = MarkdownProcessor.Render(ir, Markers,
            MarkdownProcessor.EscapeHtml, BuildLink);
        return new FormattedMessage(html);
    }

    public override IReadOnlyList<FormattedMessage> Chunk(string markdown,
        int maxChars = 4096, TableMode tableMode = TableMode.Code)
        => base.Chunk(markdown, maxChars, tableMode);

    private static string? BuildLink(MarkdownProcessor.LinkSpan link, string text)
    {
        var href = link.Href.Trim();
        if (string.IsNullOrEmpty(href)) return null;
        return $"<a href=\"{MarkdownProcessor.EscapeHtmlAttr(href)}\">";
        // Note: actual open/close handled by Render() — returned as prefix only
    }
}

// ─── Discord ──────────────────────────────────────────────────────────────────

/// <summary>
/// Discord formatter — passes markdown through mostly as-is.
/// Discord uses its own markdown: **bold**, *italic*, ~~strike~~, `code`, ```block```, ||spoiler||
/// Standard markdown is already compatible, so minimal transformation needed.
///
/// Mirrors OpenClaw: Discord gets the raw markdown string chunked at 2000 chars.
/// Discord message limit: 2000 chars.
/// </summary>
public class DiscordFormatter : BaseFormatter
{
    public override string ChannelId => ChannelIds.Discord;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Code)
    {
        var text = MarkdownProcessor.ConvertTables(markdown ?? "", tableMode);
        return new FormattedMessage(text);
    }

    public override IReadOnlyList<FormattedMessage> Chunk(string markdown,
        int maxChars = 2000, TableMode tableMode = TableMode.Code)
        => base.Chunk(markdown, maxChars, tableMode);
}

// ─── Signal ───────────────────────────────────────────────────────────────────

/// <summary>
/// Signal formatter — plain text + out-of-band style range objects.
/// Signal Desktop/CLI accepts a "text-style" parameter:  "start:length:style"
/// Styles: BOLD, ITALIC, STRIKETHROUGH, MONOSPACE, SPOILER
///
/// Mirrors OpenClaw's renderSignalText() — links get "(url)" appended if label differs.
/// Tables default to Bullets mode (Signal has no monospace tables).
/// </summary>
public class SignalFormatter : BaseFormatter
{
    public override string ChannelId => ChannelIds.Signal;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Bullets)
    {
        var ir     = MarkdownProcessor.Parse(markdown ?? "", tableMode,
            blockquotePrefix: "> ", headingAsBold: true);
        var ranges = new List<SignalStyleRange>();

        // Map our style names → Signal style names
        static string? ToSignalStyle(string style) => style switch
        {
            "bold"          => "BOLD",
            "italic"        => "ITALIC",
            "strikethrough" => "STRIKETHROUGH",
            "code"          => "MONOSPACE",
            "code_block"    => "MONOSPACE",
            "spoiler"       => "SPOILER",
            _               => null
        };

        foreach (var span in ir.Styles)
        {
            var signalStyle = ToSignalStyle(span.Style);
            if (signalStyle == null) continue;
            ranges.Add(new SignalStyleRange(span.Start, span.End - span.Start, signalStyle));
        }

        // Append (url) for links where label != url (mirrors OpenClaw)
        var text = ir.Text;
        var insertions = new List<(int Pos, string Text)>();
        foreach (var link in ir.Links.OrderByDescending(l => l.End))
        {
            var label = text[link.Start..link.End].Trim();
            var href  = link.Href.Trim();
            if (!string.IsNullOrEmpty(href) && NormalizeUrl(label) != NormalizeUrl(href))
                insertions.Add((link.End, $" ({href})"));
        }
        // Apply insertions from end to start to preserve offsets
        var sb = new System.Text.StringBuilder(text);
        foreach (var (pos, ins) in insertions.OrderByDescending(x => x.Pos))
            sb.Insert(pos, ins);

        return new FormattedMessage(sb.ToString(), ranges.Count > 0 ? ranges : null);
    }

    private static string NormalizeUrl(string url)
    {
        var s = url.ToLowerInvariant().TrimEnd('/');
        if (s.StartsWith("mailto:")) s = s[7..];
        return s;
    }
}

// ─── WhatsApp ─────────────────────────────────────────────────────────────────

/// <summary>
/// WhatsApp formatter.
/// WhatsApp markdown: *bold*, _italic_, ~strikethrough~, `code`, ```block```
/// No spoilers, no headings, no tables (convert to bullets).
///
/// Mirrors OpenClaw's markdownToWhatsApp():
///   - **bold** → *bold*
///   - __bold__ → *bold*
///   - ~~strike~~ → ~strike~
///   - Code blocks preserved as-is (``` stays ```)
///   - Inline code preserved (` stays `)
///   - Headings stripped to plain text
/// </summary>
public class WhatsAppFormatter : BaseFormatter
{
    public override string ChannelId => ChannelIds.WhatsApp;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Bullets)
    {
        if (string.IsNullOrEmpty(markdown)) return new FormattedMessage("");

        var text = MarkdownProcessor.ConvertTables(markdown, tableMode);
        text = Convert(text);
        return new FormattedMessage(text);
    }

    internal static string Convert(string text)
    {
        // Protect code fences
        var fences = new List<string>();
        text = Regex.Replace(text, @"```[\s\S]*?```", m =>
        { fences.Add(m.Value); return $"\x00FENCE{fences.Count - 1}\x00"; });

        // Protect inline code
        var inlines = new List<string>();
        text = Regex.Replace(text, @"`[^`\n]+`", m =>
        { inlines.Add(m.Value); return $"\x00CODE{inlines.Count - 1}\x00"; });

        // **bold** / __bold__ → *bold*
        text = Regex.Replace(text, @"\*\*(.+?)\*\*", "*$1*");
        text = Regex.Replace(text, @"__(.+?)__",      "*$1*");

        // ~~strike~~ → ~strike~
        text = Regex.Replace(text, @"~~(.+?)~~", "~$1~");

        // Strip headings
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);

        // Restore
        for (int i = 0; i < inlines.Count; i++)
            text = text.Replace($"\x00CODE{i}\x00", inlines[i]);
        for (int i = 0; i < fences.Count; i++)
            text = text.Replace($"\x00FENCE{i}\x00", fences[i]);

        return text;
    }
}

// ─── Slack ────────────────────────────────────────────────────────────────────

/// <summary>
/// Slack mrkdwn formatter.
/// Slack uses: *bold*, _italic_, ~strike~, `code`, ```block```, &gt; blockquote
/// Links: &lt;url|label&gt;
/// No spoilers. Tables → bullets (Slack has no table support).
/// </summary>
public class SlackFormatter : BaseFormatter
{
    private static readonly IReadOnlyDictionary<string, MarkdownProcessor.StyleMarker> Markers =
        new Dictionary<string, MarkdownProcessor.StyleMarker>
        {
            ["bold"]          = new("*",   "*"),
            ["italic"]        = new("_",   "_"),
            ["strikethrough"] = new("~",   "~"),
            ["code"]          = new("`",   "`"),
            ["code_block"]    = new("```\n", "\n```"),
            ["blockquote"]    = new("> ",  ""),
        };

    public override string ChannelId => ChannelIds.Slack;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Bullets)
    {
        var ir = MarkdownProcessor.Parse(markdown ?? "", tableMode,
            blockquotePrefix: "> ", headingAsBold: true);
        var text = MarkdownProcessor.Render(ir, Markers,
            EscapeSlack, BuildSlackLink);
        return new FormattedMessage(text);
    }

    private static string EscapeSlack(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string? BuildSlackLink(MarkdownProcessor.LinkSpan link, string text)
    {
        var href  = link.Href.Trim();
        var label = text[link.Start..link.End].Trim();
        if (string.IsNullOrEmpty(href)) return null;
        // Slack link format: <url|label> — handled as open tag; Render() appends close
        return $"<{href}|{label}>";
    }
}

// ─── Google Chat ──────────────────────────────────────────────────────────────

/// <summary>
/// Google Chat formatter — uses a subset of markdown similar to Slack.
/// Bold: *bold*, italic: _italic_, strike: ~strike~, code: `code`, block: ```
/// No spoilers, no tables.
/// </summary>
public class GoogleChatFormatter : BaseFormatter
{
    public override string ChannelId => ChannelIds.GoogleChat;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Bullets)
    {
        // Google Chat markdown is very close to WhatsApp/Slack hybrid
        var text = MarkdownProcessor.ConvertTables(markdown ?? "", tableMode);
        text = WhatsAppFormatter.Convert(text); // same transformations apply
        return new FormattedMessage(text);
    }
}

// ─── WebChat (internal) ───────────────────────────────────────────────────────

/// <summary>
/// WebChat formatter — returns raw markdown.
/// The web UI renders it client-side.
/// </summary>
public class WebChatFormatter : BaseFormatter
{
    public override string ChannelId => ChannelIds.WebChat;

    public override FormattedMessage Format(string markdown, TableMode tableMode = TableMode.Code)
        => new(markdown ?? "");
}
