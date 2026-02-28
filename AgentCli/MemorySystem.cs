using System.Text;
using System.Text.RegularExpressions;

namespace AgentCli;

/// <summary>
/// File-based memory system — same pattern as OpenClaw.
/// 
/// Files:
///   ~/.agentcli/workspace/SOUL.md            — agent personality (read-only by agent)
///   ~/.agentcli/workspace/MEMORY.md          — long-term curated memory (agent reads + writes)
///   ~/.agentcli/workspace/WORKFLOW_AUTO.md   — required startup reads (auto-loaded every session)
///   ~/.agentcli/workspace/memory/            — daily notes YYYY-MM-DD.md
///
/// WORKFLOW_AUTO.md format:
///   # Required startup files
///   - MEMORY.md
///   - memory/YYYY-MM-DD.md   (resolved to today + yesterday)
///   - any/other/path.md
/// </summary>
public class MemorySystem
{
    private readonly string _workspaceDir;

    public string SoulPath         => Path.Combine(_workspaceDir, "SOUL.md");
    public string MemoryPath       => Path.Combine(_workspaceDir, "MEMORY.md");
    public string WorkflowAutoPath => Path.Combine(_workspaceDir, "WORKFLOW_AUTO.md");
    public string MemoryDir        => Path.Combine(_workspaceDir, "memory");

    public MemorySystem(string? workspaceDir = null)
    {
        _workspaceDir = workspaceDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "workspace");
        Directory.CreateDirectory(_workspaceDir);
        Directory.CreateDirectory(MemoryDir);
    }

    // ─── Read ─────────────────────────────────────────────────────────────────

    public async Task<string?> ReadSoulAsync()
    {
        if (!File.Exists(SoulPath)) return null;
        return await File.ReadAllTextAsync(SoulPath);
    }

    public async Task<string?> ReadMemoryAsync()
    {
        if (!File.Exists(MemoryPath)) return null;
        return await File.ReadAllTextAsync(MemoryPath);
    }

    public async Task<string?> ReadDailyAsync(DateOnly? date = null)
    {
        var d = date ?? DateOnly.FromDateTime(DateTime.Today);
        var path = Path.Combine(MemoryDir, $"{d:yyyy-MM-dd}.md");
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path);
    }

    /// <summary>Builds the full memory context to inject into system prompt.</summary>
    // ─── Write ────────────────────────────────────────────────────────────────

    public async Task WriteMemoryAsync(string content)
    {
        await File.WriteAllTextAsync(MemoryPath, content);
    }

    public async Task AppendMemoryAsync(string section, string content)
    {
        var existing = await ReadMemoryAsync() ?? "";
        var entry = $"\n\n## {section} ({DateTime.Now:yyyy-MM-dd})\n\n{content.Trim()}";
        await File.WriteAllTextAsync(MemoryPath, existing.TrimEnd() + entry);
    }

    public async Task AppendDailyAsync(string content, DateOnly? date = null)
    {
        var d = date ?? DateOnly.FromDateTime(DateTime.Today);
        var path = Path.Combine(MemoryDir, $"{d:yyyy-MM-dd}.md");
        var existing = File.Exists(path) ? await File.ReadAllTextAsync(path) : "";
        var timestamp = DateTime.Now.ToString("HH:mm");
        var entry = $"\n\n### {timestamp}\n\n{content.Trim()}";
        await File.WriteAllTextAsync(path, existing.TrimEnd() + entry);
    }

    public async Task WriteSoulAsync(string content)
    {
        await File.WriteAllTextAsync(SoulPath, content);
    }

    // ─── WORKFLOW_AUTO.md ─────────────────────────────────────────────────────

    /// <summary>
    /// Reads WORKFLOW_AUTO.md and returns its content (or null if absent).
    /// </summary>
    public async Task<string?> ReadWorkflowAutoAsync()
    {
        if (!File.Exists(WorkflowAutoPath)) return null;
        return await File.ReadAllTextAsync(WorkflowAutoPath);
    }

    public async Task WriteWorkflowAutoAsync(string content)
    {
        await File.WriteAllTextAsync(WorkflowAutoPath, content);
    }

    /// <summary>
    /// Parses WORKFLOW_AUTO.md, resolves each listed path (supporting
    /// date placeholders like memory/YYYY-MM-DD.md), reads the files,
    /// and returns a dictionary of path → content for files that exist.
    ///
    /// Called automatically on startup — results are injected into the
    /// system prompt so the agent always starts with its protocols restored.
    /// </summary>
    public async Task<List<StartupFile>> RunStartupReadsAsync()
    {
        var results = new List<StartupFile>();

        var workflowRaw = await ReadWorkflowAutoAsync();
        if (workflowRaw == null) return results;

        // Parse bullet list: lines starting with "- " or "* "
        var entries = workflowRaw
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ") || l.StartsWith("* "))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

        foreach (var entry in entries)
        {
            // Resolve date placeholders: memory/YYYY-MM-DD.md → today + yesterday
            if (Regex.IsMatch(entry, @"YYYY-MM-DD", RegexOptions.IgnoreCase))
            {
                foreach (var offset in new[] { 0, -1 })
                {
                    var date     = DateOnly.FromDateTime(DateTime.Today.AddDays(offset));
                    var resolved = Regex.Replace(entry, @"YYYY-MM-DD", date.ToString("yyyy-MM-dd"), RegexOptions.IgnoreCase);
                    var full     = Path.Combine(_workspaceDir, resolved);
                    if (File.Exists(full))
                        results.Add(new StartupFile(resolved, await File.ReadAllTextAsync(full)));
                }
                continue;
            }

            var path = Path.Combine(_workspaceDir, entry);
            if (File.Exists(path))
                results.Add(new StartupFile(entry, await File.ReadAllTextAsync(path)));
        }

        return results;
    }

    /// <summary>
    /// Builds the full context: SOUL + MEMORY + daily notes + WORKFLOW_AUTO startup files.
    /// Deduplicates so files listed in WORKFLOW_AUTO.md aren't injected twice.
    /// </summary>
    public async Task<string> BuildContextAsync()
    {
        var sb      = new StringBuilder();
        var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var soul = await ReadSoulAsync();
        if (soul != null)
        {
            sb.AppendLine("## SOUL");
            sb.AppendLine(soul);
            sb.AppendLine();
            injected.Add("SOUL.md");
        }

        var memory = await ReadMemoryAsync();
        if (memory != null)
        {
            sb.AppendLine("## MEMORY");
            sb.AppendLine(memory);
            sb.AppendLine();
            injected.Add("MEMORY.md");
        }

        // Today + yesterday daily notes
        foreach (var offset in new[] { 0, -1 })
        {
            var date  = DateOnly.FromDateTime(DateTime.Today.AddDays(offset));
            var daily = await ReadDailyAsync(date);
            if (daily != null)
            {
                var key = $"memory/{date:yyyy-MM-dd}.md";
                sb.AppendLine($"## DAILY NOTE ({date:yyyy-MM-dd})");
                sb.AppendLine(daily);
                sb.AppendLine();
                injected.Add(key);
            }
        }

        // WORKFLOW_AUTO startup reads (deduplicated)
        var startupFiles = await RunStartupReadsAsync();
        foreach (var f in startupFiles)
        {
            if (injected.Contains(f.RelativePath)) continue;
            sb.AppendLine($"## STARTUP FILE ({f.RelativePath})");
            sb.AppendLine(f.Content);
            sb.AppendLine();
        }

        return sb.ToString().Trim();
    }

    public async Task<List<MemorySearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        var results = new List<MemorySearchResult>();
        var keywords = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var files = new List<string>();
        if (File.Exists(MemoryPath))  files.Add(MemoryPath);
        if (File.Exists(SoulPath))    files.Add(SoulPath);
        files.AddRange(Directory.GetFiles(MemoryDir, "*.md"));

        foreach (var file in files)
        {
            var lines = await File.ReadAllLinesAsync(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var lower = line.ToLower();
                var score = keywords.Count(k => lower.Contains(k));
                if (score == 0) continue;

                // Grab snippet: a few lines of context
                var start   = Math.Max(0, i - 1);
                var end     = Math.Min(lines.Length - 1, i + 3);
                var snippet = string.Join("\n", lines[start..(end + 1)]);

                results.Add(new MemorySearchResult(
                    Path: file,
                    Line: i + 1,
                    Score: (float)score / keywords.Length,
                    Snippet: snippet
                ));
            }
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(maxResults)
            .ToList();
    }
}

public record MemorySearchResult(string Path, int Line, float Score, string Snippet);

/// <summary>A file loaded from WORKFLOW_AUTO.md on startup.</summary>
public record StartupFile(string RelativePath, string Content);
