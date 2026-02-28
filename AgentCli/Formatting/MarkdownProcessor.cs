using System.Text;
using System.Text.RegularExpressions;

namespace AgentCli;

/// <summary>
/// Core markdown→text transformation engine.
/// Mirrors OpenClaw's IR pipeline in pure C# (no JS dependency).
///
/// OpenClaw stages:
///   1. markdownToIR()           — parse markdown into semantic spans
///   2. renderMarkdownWithMarkers() — apply channel markers to spans
///
/// We implement the same two stages as static helpers here,
/// consumed by each IOutputFormatter implementation.
/// </summary>
public static class MarkdownProcessor
{
    // ─── Stage 1: IR ─────────────────────────────────────────────────────────

    public record StyleSpan(int Start, int End, string Style);
    public record LinkSpan(int Start, int End, string Href);
    public record MarkdownIR(string Text, IReadOnlyList<StyleSpan> Styles, IReadOnlyList<LinkSpan> Links);

    /// <summary>
    /// Parse a markdown string into a semantic IR.
    /// Handles: bold, italic, strikethrough, code, code_block, blockquote, links.
    /// Tables converted to text according to tableMode before IR parsing.
    /// </summary>
    public static MarkdownIR Parse(string markdown, TableMode tableMode = TableMode.Code,
        string blockquotePrefix = "", bool headingAsBold = false)
    {
        if (string.IsNullOrEmpty(markdown))
            return new MarkdownIR("", [], []);

        // Pre-process tables
        markdown = ConvertTables(markdown, tableMode);

        var styles = new List<StyleSpan>();
        var links  = new List<LinkSpan>();

        // We build plain text by stripping/tracking markdown syntax
        var sb       = new StringBuilder();
        var lines    = markdown.Split('\n');
        var inFence  = false;
        var fenceLang = "";

        for (int li = 0; li < lines.Length; li++)
        {
            var line = lines[li];

            // Code fence
            if (line.TrimStart().StartsWith("```"))
            {
                if (!inFence)
                {
                    inFence   = true;
                    fenceLang = line.TrimStart()[3..].Trim();
                    if (li > 0) sb.Append('\n');
                    continue;
                }
                else
                {
                    inFence = false;
                    // Code block span already open — closed above in content loop
                    sb.Append('\n');
                    continue;
                }
            }

            if (inFence)
            {
                int start = sb.Length;
                sb.Append(line); sb.Append('\n');
                styles.Add(new StyleSpan(start, sb.Length, "code_block"));
                continue;
            }

            // Blockquote
            if (line.StartsWith("> ") || line == ">")
            {
                var content = line.StartsWith("> ") ? line[2..] : "";
                if (!string.IsNullOrEmpty(blockquotePrefix))
                {
                    int bqStart = sb.Length;
                    sb.Append(blockquotePrefix);
                    AppendInline(sb, styles, links, content);
                    sb.Append('\n');
                    styles.Add(new StyleSpan(bqStart, sb.Length, "blockquote"));
                }
                else
                {
                    int bqStart = sb.Length;
                    AppendInline(sb, styles, links, content);
                    sb.Append('\n');
                    styles.Add(new StyleSpan(bqStart, sb.Length, "blockquote"));
                }
                continue;
            }

            // Headings
            var headingMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (headingMatch.Success)
            {
                var content = headingMatch.Groups[2].Value;
                if (headingAsBold)
                {
                    int hs = sb.Length;
                    AppendInline(sb, styles, links, content);
                    styles.Add(new StyleSpan(hs, sb.Length, "bold"));
                }
                else
                    AppendInline(sb, styles, links, content);
                sb.Append('\n');
                continue;
            }

            // Normal line
            AppendInline(sb, styles, links, line);
            if (li < lines.Length - 1) sb.Append('\n');
        }

        return new MarkdownIR(sb.ToString(), styles, links);
    }

    // ─── Stage 2: Render ──────────────────────────────────────────────────────

    public record StyleMarker(string Open, string Close);

    /// <summary>
    /// Render IR to a string using channel-specific markers.
    /// Mirrors OpenClaw's renderMarkdownWithMarkers().
    /// </summary>
    public static string Render(MarkdownIR ir,
        IReadOnlyDictionary<string, StyleMarker> markers,
        Func<string, string> escapeText,
        Func<LinkSpan, string, string?>? buildLink = null)
    {
        var text = ir.Text;
        if (string.IsNullOrEmpty(text)) return "";

        // Collect boundary points
        var boundaries = new SortedSet<int> { 0, text.Length };
        foreach (var s in ir.Styles) { boundaries.Add(s.Start); boundaries.Add(s.End); }

        // Link substitutions (open/close per link)
        var linkMarkers = new Dictionary<int, List<(int End, string Open, string Close)>>();
        if (buildLink != null)
        {
            foreach (var link in ir.Links)
            {
                var rendered = buildLink(link, text);
                if (rendered == null) continue;
                boundaries.Add(link.Start); boundaries.Add(link.End);
                if (!linkMarkers.TryGetValue(link.Start, out var bucket))
                    linkMarkers[link.Start] = bucket = new();
                bucket.Add((link.End, $"<a href=\"{EscapeHtmlAttr(link.Href)}\">", "</a>"));
            }
        }

        var points = boundaries.ToList();
        var stack  = new Stack<(int End, string Close)>();
        var sb     = new StringBuilder();

        for (int i = 0; i < points.Count - 1; i++)
        {
            int pos  = points[i];
            int next = points[i + 1];

            // Close spans ending here
            while (stack.Count > 0 && stack.Peek().End <= pos)
                sb.Append(stack.Pop().Close);

            // Open link markers
            if (linkMarkers.TryGetValue(pos, out var lms))
                foreach (var (end, open, close) in lms)
                { sb.Append(open); stack.Push((end, close)); }

            // Open style markers
            foreach (var span in ir.Styles.Where(s => s.Start == pos && markers.ContainsKey(s.Style)))
            {
                var m = markers[span.Style];
                sb.Append(m.Open);
                stack.Push((span.End, m.Close));
            }

            sb.Append(escapeText(text[pos..next]));
        }

        while (stack.Count > 0) sb.Append(stack.Pop().Close);
        return sb.ToString();
    }

    // ─── Plain text (strip everything) ───────────────────────────────────────

    public static string ToPlainText(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;
        var s = markdown;
        s = Regex.Replace(s, @"\*\*(.+?)\*\*", "$1");
        s = Regex.Replace(s, @"__(.+?)__",      "$1");
        s = Regex.Replace(s, @"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)", "$1");
        s = Regex.Replace(s, @"(?<!_)_(?!_)(.+?)(?<!_)_(?!_)", "$1");
        s = Regex.Replace(s, @"~~(.+?)~~",   "$1");
        s = Regex.Replace(s, @"^#{1,6}\s+(.+)$", "$1", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^>\s?(.*)$",  "$1", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^[-*_]{3,}$", "",  RegexOptions.Multiline);
        s = Regex.Replace(s, @"`([^`]+)`",   "$1");
        s = Regex.Replace(s, @"```[\s\S]*?```", m => {
            var inner = Regex.Replace(m.Value, @"^```\w*\n?", "");
            return Regex.Replace(inner, @"```$", "").Trim();
        });
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]+\)", "$1"); // [text](url) → text
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    // ─── Table conversion ─────────────────────────────────────────────────────

    public static string ConvertTables(string text, TableMode mode)
    {
        if (mode == TableMode.Off) return RemoveTables(text);
        if (mode == TableMode.Bullets) return TablesToBullets(text);
        return TablesToCode(text);   // TableMode.Code — fenced monospace block
    }

    // ─── Inline parsing ───────────────────────────────────────────────────────

    private static void AppendInline(StringBuilder sb, List<StyleSpan> styles,
        List<LinkSpan> links, string text)
    {
        // Process inline code first (protect from further parsing)
        var segments = SplitByInlineCode(text);
        foreach (var (content, isCode) in segments)
        {
            if (isCode)
            {
                int cs = sb.Length;
                sb.Append(content);
                styles.Add(new StyleSpan(cs, sb.Length, "code"));
                continue;
            }
            AppendStyledText(sb, styles, links, content);
        }
    }

    private static void AppendStyledText(StringBuilder sb, List<StyleSpan> styles,
        List<LinkSpan> links, string text)
    {
        // Links: [text](url)
        text = Regex.Replace(text, @"\[([^\]]+)\]\(([^)]+)\)", m =>
        {
            var label = m.Groups[1].Value;
            var href  = m.Groups[2].Value;
            int ls    = sb.Length + (text.IndexOf(m.Value, StringComparison.Ordinal));
            // Approximate — we track position below
            return m.Value; // handle below
        });

        // Walk the text with regex for bold/italic/strikethrough/links
        int pos = 0;
        var patterns = new (Regex Re, string Style)[]
        {
            (new Regex(@"\*\*(.+?)\*\*"),  "bold"),
            (new Regex(@"__(.+?)__"),      "bold"),
            (new Regex(@"(?<!\*)\*(?!\*)(.+?)(?<!\*)\*(?!\*)"), "italic"),
            (new Regex(@"(?<!_)_(?!_)(.+?)(?<!_)_(?!_)"),      "italic"),
            (new Regex(@"~~(.+?)~~"),      "strikethrough"),
            (new Regex(@"\|\|(.+?)\|\|"), "spoiler"),
        };

        // Simple two-pass: collect all matches, sort by start, output
        var matches = new List<(int Start, int End, int InnerStart, int InnerEnd, string Style, string Full)>();
        foreach (var (re, style) in patterns)
            foreach (Match m in re.Matches(text))
                matches.Add((m.Index, m.Index + m.Length,
                    m.Index + (m.Length - m.Groups[1].Length) / 2,
                    m.Index + (m.Length - m.Groups[1].Length) / 2 + m.Groups[1].Length,
                    style, m.Value));

        // Link matches
        foreach (Match m in Regex.Matches(text, @"\[([^\]]+)\]\(([^)]+)\)"))
            matches.Add((m.Index, m.Index + m.Length,
                m.Groups[1].Index, m.Groups[1].Index + m.Groups[1].Length,
                "link", m.Value));

        matches.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : b.End.CompareTo(a.End));

        int cursor = 0;
        var used   = new bool[text.Length + 1];

        foreach (var match in matches)
        {
            if (match.Start < cursor) continue;
            if (used[match.Start]) continue;

            // Plain text before this match
            if (match.Start > cursor)
                sb.Append(text[cursor..match.Start]);

            if (match.Style == "link")
            {
                var lm    = Regex.Match(match.Full, @"\[([^\]]+)\]\(([^)]+)\)");
                int ls    = sb.Length;
                sb.Append(lm.Groups[1].Value);
                links.Add(new LinkSpan(ls, sb.Length, lm.Groups[2].Value));
            }
            else
            {
                int ss = sb.Length;
                // Inner content (recurse for nested)
                var inner = text[match.InnerStart..match.InnerEnd];
                AppendStyledText(sb, styles, links, inner);
                styles.Add(new StyleSpan(ss, sb.Length, match.Style));
            }

            cursor = match.End;
            for (int i = match.Start; i < match.End; i++) used[i] = true;
        }

        if (cursor < text.Length)
            sb.Append(text[cursor..]);
    }

    private static IEnumerable<(string Content, bool IsCode)> SplitByInlineCode(string text)
    {
        var result = new List<(string, bool)>();
        int pos    = 0;
        foreach (Match m in Regex.Matches(text, @"`([^`\n]+)`"))
        {
            if (m.Index > pos)
                result.Add((text[pos..m.Index], false));
            result.Add((m.Groups[1].Value, true));
            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            result.Add((text[pos..], false));
        return result;
    }

    // ─── Table helpers ────────────────────────────────────────────────────────

    private static string RemoveTables(string text) =>
        Regex.Replace(text, @"(\|.+\|\n)+(\|[-: |]+\|\n)((\|.+\|\n)*)", "");

    private static string TablesToCode(string text)
    {
        return Regex.Replace(text,
            @"(\|[^\n]+\|\n\|[-| :]+\|\n(?:\|[^\n]+\|\n?)*)",
            m => "```\n" + m.Value.TrimEnd() + "\n```\n",
            RegexOptions.Multiline);
    }

    private static string TablesToBullets(string text)
    {
        return Regex.Replace(text,
            @"(\|[^\n]+\|\n)(\|[-| :]+\|\n)((?:\|[^\n]+\|\n?)*)",
            m =>
            {
                var headerLine = m.Groups[1].Value.Trim();
                var rowsText   = m.Groups[3].Value;
                var headers    = ParseTableRow(headerLine);
                var rows       = rowsText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                         .Select(ParseTableRow).ToList();
                var sb2 = new StringBuilder();
                foreach (var row in rows)
                {
                    if (row.Count == 0) continue;
                    if (headers.Count > 1 && row.Count > 0)
                    {
                        sb2.AppendLine($"**{row[0]}**");
                        for (int i = 1; i < Math.Min(headers.Count, row.Count); i++)
                            sb2.AppendLine($"  • {headers[i]}: {row[i]}");
                    }
                    else
                        sb2.AppendLine($"• {string.Join(" | ", row)}");
                }
                return sb2.ToString();
            },
            RegexOptions.Multiline);
    }

    private static List<string> ParseTableRow(string line) =>
        line.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToList();

    // ─── Escape helpers ───────────────────────────────────────────────────────

    public static string EscapeHtml(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    public static string EscapeHtmlAttr(string text) =>
        EscapeHtml(text).Replace("\"", "&quot;");

    public static string EscapePlain(string text) => text;
}
