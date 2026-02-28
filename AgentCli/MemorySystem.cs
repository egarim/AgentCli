using System.Text;

namespace AgentCli;

/// <summary>
/// File-based memory system — same pattern as OpenClaw.
/// 
/// Files:
///   ~/.agentcli/workspace/SOUL.md       — agent personality (read-only by agent)
///   ~/.agentcli/workspace/MEMORY.md     — long-term curated memory (agent reads + writes)
///   ~/.agentcli/workspace/memory/       — daily notes YYYY-MM-DD.md
/// </summary>
public class MemorySystem
{
    private readonly string _workspaceDir;

    public string SoulPath    => Path.Combine(_workspaceDir, "SOUL.md");
    public string MemoryPath  => Path.Combine(_workspaceDir, "MEMORY.md");
    public string MemoryDir   => Path.Combine(_workspaceDir, "memory");

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
    public async Task<string> BuildContextAsync()
    {
        var sb = new StringBuilder();

        var soul = await ReadSoulAsync();
        if (soul != null)
        {
            sb.AppendLine("## SOUL");
            sb.AppendLine(soul);
            sb.AppendLine();
        }

        var memory = await ReadMemoryAsync();
        if (memory != null)
        {
            sb.AppendLine("## MEMORY");
            sb.AppendLine(memory);
            sb.AppendLine();
        }

        // Today + yesterday daily notes
        foreach (var offset in new[] { 0, -1 })
        {
            var date = DateOnly.FromDateTime(DateTime.Today.AddDays(offset));
            var daily = await ReadDailyAsync(date);
            if (daily != null)
            {
                sb.AppendLine($"## DAILY NOTE ({date:yyyy-MM-dd})");
                sb.AppendLine(daily);
                sb.AppendLine();
            }
        }

        return sb.ToString().Trim();
    }

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

    // ─── Search (simple keyword search — no embeddings needed) ───────────────

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
