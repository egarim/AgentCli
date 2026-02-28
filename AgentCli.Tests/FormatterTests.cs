using AgentCli;
using Xunit;

namespace AgentCli.Tests;

public class FormatterTests
{
    private const string Sample = """
        # Hello World

        This is **bold** and *italic* and ~~strike~~ and `inline code`.

        > A blockquote

        ```csharp
        var x = 42;
        ```

        | Name  | Age |
        |-------|-----|
        | Alice | 30  |
        | Bob   | 25  |

        [OpenClaw](https://openclaw.ai)
        """;

    // ─── Telegram ─────────────────────────────────────────────────────────────

    [Fact]
    public void Telegram_Bold_WrappedInBTags()
    {
        var f   = new TelegramFormatter();
        var out_ = f.Format("**bold text**").Text;
        Assert.Contains("<b>bold text</b>", out_);
    }

    [Fact]
    public void Telegram_Italic_WrappedInITags()
    {
        var out_ = new TelegramFormatter().Format("*italic*").Text;
        Assert.Contains("<i>italic</i>", out_);
    }

    [Fact]
    public void Telegram_Code_WrappedInCodeTags()
    {
        var out_ = new TelegramFormatter().Format("`code`").Text;
        Assert.Contains("<code>code</code>", out_);
    }

    [Fact]
    public void Telegram_CodeBlock_WrappedInPreCode()
    {
        var out_ = new TelegramFormatter().Format("```\nvar x = 1;\n```").Text;
        Assert.Contains("<pre><code>", out_);
        Assert.Contains("</code></pre>", out_);
    }

    [Fact]
    public void Telegram_Escapes_HtmlSpecialChars()
    {
        var out_ = new TelegramFormatter().Format("a < b & c > d").Text;
        Assert.Contains("&lt;", out_);
        Assert.Contains("&amp;", out_);
        Assert.Contains("&gt;", out_);
    }

    [Fact]
    public void Telegram_Chunks_At4096()
    {
        var f    = new TelegramFormatter();
        var long_ = string.Join("\n\n", Enumerable.Repeat("paragraph of text here", 300));
        var chunks = f.Chunk(long_, 4096);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 4096));
        Assert.True(chunks.Count > 1);
    }

    // ─── WhatsApp ─────────────────────────────────────────────────────────────

    [Fact]
    public void WhatsApp_ConvertsBold_ToSingleStar()
    {
        var out_ = new WhatsAppFormatter().Format("**bold**").Text;
        Assert.Contains("*bold*", out_);
        Assert.DoesNotContain("**", out_);
    }

    [Fact]
    public void WhatsApp_ConvertsStrike_ToSingleTilde()
    {
        var out_ = new WhatsAppFormatter().Format("~~strike~~").Text;
        Assert.Contains("~strike~", out_);
        Assert.DoesNotContain("~~", out_);
    }

    [Fact]
    public void WhatsApp_PreservesCodeFences()
    {
        var out_ = new WhatsAppFormatter().Format("```\ncode\n```").Text;
        Assert.Contains("```", out_);
    }

    [Fact]
    public void WhatsApp_TablesToBullets()
    {
        var out_ = new WhatsAppFormatter().Format(Sample, TableMode.Bullets).Text;
        Assert.DoesNotContain("|", out_); // table pipes gone
        Assert.Contains("Alice", out_);
        Assert.Contains("Bob",   out_);
    }

    // ─── Signal ───────────────────────────────────────────────────────────────

    [Fact]
    public void Signal_Bold_ProducesStyleRange()
    {
        var msg = new SignalFormatter().Format("**hello world**");
        Assert.NotNull(msg.StyleRanges);
        Assert.Contains(msg.StyleRanges!, r => r.Style == "BOLD");
    }

    [Fact]
    public void Signal_PlainText_NoBoldMarkers()
    {
        var msg = new SignalFormatter().Format("**hello**");
        Assert.DoesNotContain("**", msg.Text);
        Assert.DoesNotContain("<b>",  msg.Text);
    }

    [Fact]
    public void Signal_Code_ProducesMonospaceRange()
    {
        var msg = new SignalFormatter().Format("`code`");
        Assert.NotNull(msg.StyleRanges);
        Assert.Contains(msg.StyleRanges!, r => r.Style == "MONOSPACE");
    }

    // ─── Discord ──────────────────────────────────────────────────────────────

    [Fact]
    public void Discord_PassesMarkdownThrough()
    {
        var out_ = new DiscordFormatter().Format("**bold** and *italic*").Text;
        Assert.Contains("**bold**", out_);
        Assert.Contains("*italic*", out_);
    }

    // ─── Plain text ───────────────────────────────────────────────────────────

    [Fact]
    public void Plain_StripsBold()
    {
        var out_ = new PlainTextFormatter().Format("**hello**").Text;
        Assert.Equal("hello", out_);
    }

    [Fact]
    public void Plain_StripsHeadings()
    {
        var out_ = new PlainTextFormatter().Format("# Title\n\nBody").Text;
        Assert.DoesNotContain("#", out_);
        Assert.Contains("Title", out_);
    }

    [Fact]
    public void Plain_StripsLinks()
    {
        var out_ = new PlainTextFormatter().Format("[click here](https://example.com)").Text;
        Assert.Equal("click here", out_);
    }

    // ─── Registry ─────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_DefaultTableMode_Signal_IsBullets()
    {
        Assert.Equal(TableMode.Bullets, FormatterRegistry.DefaultTableMode("signal"));
        Assert.Equal(TableMode.Bullets, FormatterRegistry.DefaultTableMode("whatsapp"));
    }

    [Fact]
    public void Registry_DefaultTableMode_Telegram_IsCode()
    {
        Assert.Equal(TableMode.Code, FormatterRegistry.DefaultTableMode("telegram"));
    }

    [Fact]
    public void Registry_FallsBackToPlain_ForUnknownChannel()
    {
        var msg = FormatterRegistry.Default.Format("**bold**", "unknown-channel-xyz");
        Assert.Equal("bold", msg.Text);
    }

    [Fact]
    public void Registry_AllChannels_Registered()
    {
        var reg = FormatterRegistry.Default;
        foreach (var ch in new[] {
            ChannelIds.Terminal, ChannelIds.Plain, ChannelIds.Telegram,
            ChannelIds.Discord, ChannelIds.Signal, ChannelIds.WhatsApp,
            ChannelIds.Slack, ChannelIds.GoogleChat, ChannelIds.WebChat })
        {
            Assert.True(reg.TryGet(ch, out _), $"Channel '{ch}' not registered");
        }
    }

    [Fact]
    public void Registry_DefaultMaxChars_Telegram()
    {
        Assert.Equal(4096, FormatterRegistry.DefaultMaxChars("telegram"));
    }

    [Fact]
    public void Registry_DefaultMaxChars_Discord()
    {
        Assert.Equal(2000, FormatterRegistry.DefaultMaxChars("discord"));
    }
}
