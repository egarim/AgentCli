using System.Text;
using System.Text.RegularExpressions;

namespace AgentCli;

/// <summary>
/// Domain-level coordinator on top of IMemoryProvider.
/// Handles the SOUL / MEMORY / WORKFLOW_AUTO / daily-notes conventions.
/// Swap providers by passing a different IMemoryProvider implementation.
///
/// Keys used:
///   SOUL.md            — agent personality
///   MEMORY.md          — long-term curated memory
///   WORKFLOW_AUTO.md   — required startup reads
///   memory/YYYY-MM-DD.md — daily notes
/// </summary>
public class MemorySystem
{
    private readonly IMemoryProvider _provider;

    public const string KeySoul         = "SOUL.md";
    public const string KeyMemory       = "MEMORY.md";
    public const string KeyWorkflow     = "WORKFLOW_AUTO.md";
    public const string PrefixDaily     = "memory/";

    public IMemoryProvider Provider => _provider;
    public string          ProviderName => _provider.Name;

    // Keep a workspace path hint for display — only FileMemoryProvider has one.
    public string? WorkspaceDir =>
        (_provider as FileMemoryProvider)?.Resolve("").TrimEnd(Path.DirectorySeparatorChar);

    public MemorySystem(IMemoryProvider provider)
    {
        _provider = provider;
    }

    // ─── Convenience factory ──────────────────────────────────────────────────

    /// <summary>Creates a MemorySystem backed by the local filesystem.</summary>
    public static MemorySystem CreateFile(string? dir = null)
    {
        var root = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "workspace");
        return new MemorySystem(new FileMemoryProvider(root));
    }

    // ─── Read ─────────────────────────────────────────────────────────────────

    public Task<string?> ReadSoulAsync()         => _provider.ReadAsync(KeySoul);
    public Task<string?> ReadMemoryAsync()       => _provider.ReadAsync(KeyMemory);
    public Task<string?> ReadWorkflowAutoAsync() => _provider.ReadAsync(KeyWorkflow);

    public Task<string?> ReadDailyAsync(DateOnly? date = null)
    {
        var d = date ?? DateOnly.FromDateTime(DateTime.Today);
        return _provider.ReadAsync($"{PrefixDaily}{d:yyyy-MM-dd}.md");
    }

    // ─── Write ────────────────────────────────────────────────────────────────

    public Task WriteSoulAsync(string content)         => _provider.WriteAsync(KeySoul, content);
    public Task WriteMemoryAsync(string content)       => _provider.WriteAsync(KeyMemory, content);
    public Task WriteWorkflowAutoAsync(string content) => _provider.WriteAsync(KeyWorkflow, content);

    public async Task AppendMemoryAsync(string section, string content)
    {
        var existing = await ReadMemoryAsync() ?? "";
        var entry    = $"\n\n## {section} ({DateTime.Now:yyyy-MM-dd})\n\n{content.Trim()}";
        await _provider.WriteAsync(KeyMemory, existing.TrimEnd() + entry);
    }

    public async Task AppendDailyAsync(string content, DateOnly? date = null)
    {
        var d    = date ?? DateOnly.FromDateTime(DateTime.Today);
        var key  = $"{PrefixDaily}{d:yyyy-MM-dd}.md";
        var ts   = DateTime.Now.ToString("HH:mm");
        await _provider.AppendAsync(key, $"\n\n### {ts}\n\n{content.Trim()}");
    }

    // ─── WORKFLOW_AUTO ────────────────────────────────────────────────────────

    /// <summary>
    /// Parses WORKFLOW_AUTO.md and loads all listed files.
    /// Resolves YYYY-MM-DD placeholder → today + yesterday.
    /// </summary>
    public async Task<List<StartupFile>> RunStartupReadsAsync()
    {
        var results = new List<StartupFile>();

        var raw = await ReadWorkflowAutoAsync();
        if (raw == null) return results;

        var entries = raw
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ") || l.StartsWith("* "))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0)
            .ToList();

        foreach (var entry in entries)
        {
            if (Regex.IsMatch(entry, @"YYYY-MM-DD", RegexOptions.IgnoreCase))
            {
                foreach (var offset in new[] { 0, -1 })
                {
                    var date     = DateOnly.FromDateTime(DateTime.Today.AddDays(offset));
                    var resolved = Regex.Replace(entry, @"YYYY-MM-DD", date.ToString("yyyy-MM-dd"), RegexOptions.IgnoreCase);
                    var content  = await _provider.ReadAsync(resolved);
                    if (content != null)
                        results.Add(new StartupFile(resolved, content));
                }
                continue;
            }

            var raw2 = await _provider.ReadAsync(entry);
            if (raw2 != null)
                results.Add(new StartupFile(entry, raw2));
        }

        return results;
    }

    // ─── Context assembly ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds the full context string injected into the system prompt.
    /// Order: SOUL → MEMORY → daily notes → WORKFLOW_AUTO extras.
    /// Deduplicates so files in WORKFLOW_AUTO aren't injected twice.
    /// </summary>
    public async Task<string> BuildContextAsync()
    {
        var sb       = new StringBuilder();
        var injected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Append(string header, string content, string key)
        {
            sb.AppendLine($"## {header}");
            sb.AppendLine(content);
            sb.AppendLine();
            injected.Add(key);
        }

        var soul = await ReadSoulAsync();
        if (soul != null) Append("SOUL", soul, KeySoul);

        var mem = await ReadMemoryAsync();
        if (mem != null) Append("MEMORY", mem, KeyMemory);

        foreach (var offset in new[] { 0, -1 })
        {
            var date  = DateOnly.FromDateTime(DateTime.Today.AddDays(offset));
            var daily = await ReadDailyAsync(date);
            var key   = $"{PrefixDaily}{date:yyyy-MM-dd}.md";
            if (daily != null) Append($"DAILY NOTE ({date:yyyy-MM-dd})", daily, key);
        }

        var startupFiles = await RunStartupReadsAsync();
        foreach (var f in startupFiles)
        {
            if (!injected.Contains(f.RelativePath))
                Append($"STARTUP FILE ({f.RelativePath})", f.Content, f.RelativePath);
        }

        return sb.ToString().Trim();
    }

    // ─── Search (delegates to provider) ──────────────────────────────────────

    public Task<List<MemorySearchResult>> SearchAsync(string query, int maxResults = 5)
        => _provider.SearchAsync(query, maxResults);
}
