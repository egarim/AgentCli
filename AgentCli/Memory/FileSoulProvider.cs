using System.Text.RegularExpressions;

namespace AgentCli;

/// <summary>
/// File-system implementation of ISoulProvider.
///
/// Reads from two human-editable files in the workspace:
///   SOUL.md          → SoulConfig.Prompt
///   WORKFLOW_AUTO.md → SoulConfig.StartupReads (bullet list of keys)
///
/// This is the file-mode serialisation of the same data that PostgresSoulProvider
/// stores as a single souls row with a startup_reads TEXT[] column.
///
/// Version is derived from file modification timestamps (max of both files)
/// so cache invalidation works the same way as the Postgres version check.
/// </summary>
public sealed class FileSoulProvider : ISoulProvider
{
    private readonly IMemoryProvider _memory;
    private readonly string          _agentType;

    // Simple version cache — based on file mtime
    private SoulConfig?    _cached;
    private long           _cachedVersion;

    public FileSoulProvider(IMemoryProvider memory, string agentType = "default")
    {
        _memory    = memory;
        _agentType = agentType;
    }

    public async Task<SoulConfig?> GetAsync(
        string agentType, CancellationToken ct = default)
    {
        if (agentType != _agentType) return null;

        var currentVersion = await GetVersionAsync(agentType, ct);
        if (_cached != null && _cachedVersion == currentVersion)
            return _cached;

        var prompt   = await _memory.ReadAsync(MemorySystem.KeySoul);
        var workflow = await _memory.ReadAsync(MemorySystem.KeyWorkflow);

        // No soul file → no soul
        if (prompt == null) return null;

        var startupReads = ParseWorkflowReads(workflow);

        _cached = new SoulConfig(
            AgentType:    _agentType,
            Name:         _agentType,
            Prompt:       prompt,
            StartupReads: startupReads,
            Version:      (int)(currentVersion & int.MaxValue),
            UpdatedAt:    DateTimeOffset.UtcNow);

        _cachedVersion = currentVersion;
        return _cached;
    }

    public async Task<int> GetVersionAsync(
        string agentType, CancellationToken ct = default)
    {
        if (agentType != _agentType) return 0;

        // Use a cheap hash of file existence + rough mtime as version signal.
        // We read the files via IMemoryProvider so we stay backend-agnostic;
        // FileMemoryProvider.Resolve gives the actual path for mtime.
        long version = 0;

        if (_memory is FileMemoryProvider fp)
        {
            var soulPath = fp.Resolve(MemorySystem.KeySoul);
            var wfPath   = fp.Resolve(MemorySystem.KeyWorkflow);

            if (File.Exists(soulPath))
                version = File.GetLastWriteTimeUtc(soulPath).Ticks;
            if (File.Exists(wfPath))
                version ^= File.GetLastWriteTimeUtc(wfPath).Ticks;
        }
        else
        {
            // Non-file backend: version = 1 if soul exists, 0 otherwise
            version = await _memory.ExistsAsync(MemorySystem.KeySoul) ? 1 : 0;
        }

        return await Task.FromResult((int)(version & int.MaxValue));
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses WORKFLOW_AUTO.md bullet list into a string array of keys.
    /// Ignores comment lines (starting with #) and blank lines.
    /// </summary>
    private static string[] ParseWorkflowReads(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];

        return content
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("- ") || l.StartsWith("* "))
            .Select(l => l[2..].Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"))
            .ToArray();
    }
}
