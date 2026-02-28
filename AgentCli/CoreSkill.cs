using System.Text;
using System.Text.Json;

namespace AgentCli;

/// <summary>
/// Built-in core skill — memory, web, time.
/// Always registered via InProcessSkillProvider.
/// </summary>
public class CoreSkill : ISkill
{
    private readonly MemorySystem _memory;

    public CoreSkill(MemorySystem memory)
    {
        _memory  = memory;
        Manifest = new SkillManifest("core", "Core tools: memory, web fetch, time", new List<ToolSpec>
        {
            new("get_time",          "Returns the current local date and time",
                new { type = "object", properties = new { } }),

            new("web_fetch",         "Fetches plain text content from a URL",
                new { type = "object",
                      properties = new { url = new { type = "string", description = "URL to fetch" } },
                      required   = new[] { "url" } }),

            new("memory_write",      "Save an important fact to long-term memory (MEMORY.md)",
                new { type = "object",
                      properties = new { section = new { type = "string" }, content = new { type = "string" } },
                      required   = new[] { "section", "content" } }),

            new("memory_search",     "Keyword search across all memory files",
                new { type = "object",
                      properties = new { query = new { type = "string" } },
                      required   = new[] { "query" } }),

            new("memory_read_all",   "Read the full contents of MEMORY.md",
                new { type = "object", properties = new { } }),

            new("daily_note",        "Append a note to today's daily log (memory/YYYY-MM-DD.md)",
                new { type = "object",
                      properties = new { content = new { type = "string" } },
                      required   = new[] { "content" } }),

            new("workflow_auto_read",  "Read WORKFLOW_AUTO.md — shows which files load on startup",
                new { type = "object", properties = new { } }),

            new("workflow_auto_write", "Overwrite WORKFLOW_AUTO.md",
                new { type = "object",
                      properties = new { content = new { type = "string" } },
                      required   = new[] { "content" } }),
        });
    }

    public SkillManifest Manifest { get; }

    public async Task<string> InvokeAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        return toolName switch
        {
            "get_time" =>
                DateTime.Now.ToString("F"),

            "web_fetch" => await WebFetch(args.GetProperty("url").GetString()!, ct),

            "memory_write" => await MemoryWrite(
                args.GetProperty("section").GetString()!,
                args.GetProperty("content").GetString()!),

            "memory_search" => await MemorySearch(args.GetProperty("query").GetString()!),

            "memory_read_all" =>
                await _memory.ReadMemoryAsync() ?? "(MEMORY.md is empty)",

            "daily_note" => await DailyNote(args.GetProperty("content").GetString()!),

            "workflow_auto_read" =>
                await _memory.ReadWorkflowAutoAsync() ?? "(WORKFLOW_AUTO.md not found)",

            "workflow_auto_write" => await WorkflowWrite(args.GetProperty("content").GetString()!),

            _ => $"Error: CoreSkill has no tool '{toolName}'"
        };
    }

    private static async Task<string> WebFetch(string url, CancellationToken ct)
    {
        using var wh = new HttpClient();
        wh.DefaultRequestHeaders.Add("User-Agent", "AgentCli/1.0");
        var html = await wh.GetStringAsync(url, ct);
        return html.Length > 2000 ? html[..2000] + "\n…(truncated)" : html;
    }

    private async Task<string> MemoryWrite(string section, string content)
    {
        await _memory.AppendMemoryAsync(section, content);
        return $"Saved to memory: [{section}]";
    }

    private async Task<string> MemorySearch(string query)
    {
        var results = await _memory.SearchAsync(query, 5);
        if (results.Count == 0) return "No memory found.";
        var sb = new StringBuilder();
        foreach (var r in results)
        {
            sb.AppendLine($"[{Path.GetFileName(r.Path)}:{r.Line}] score={r.Score:F2}");
            sb.AppendLine(r.Snippet);
            sb.AppendLine();
        }
        return sb.ToString().Trim();
    }

    private async Task<string> DailyNote(string content)
    {
        await _memory.AppendDailyAsync(content);
        return $"Appended to daily note ({DateTime.Today:yyyy-MM-dd})";
    }

    private async Task<string> WorkflowWrite(string content)
    {
        await _memory.WriteWorkflowAutoAsync(content);
        return "WORKFLOW_AUTO.md updated.";
    }
}
