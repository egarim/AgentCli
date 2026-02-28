namespace AgentCli;

/// <summary>
/// Core storage abstraction for the memory system.
///
/// A provider handles raw persistence — keys, content, search.
/// Keys are relative paths (e.g. "MEMORY.md", "memory/2026-02-28.md").
/// MemorySystem sits on top and handles domain logic (SOUL, WORKFLOW_AUTO, context assembly).
///
/// To add a new backend: implement this interface and pass it to MemorySystem.
/// </summary>
public interface IMemoryProvider
{
    /// <summary>Human-readable name shown in banners (e.g. "file", "sqlite", "postgres").</summary>
    string Name { get; }

    /// <summary>Returns file content, or null if it doesn't exist.</summary>
    Task<string?> ReadAsync(string key);

    /// <summary>Writes (creates or overwrites) a file.</summary>
    Task WriteAsync(string key, string content);

    /// <summary>Appends content to a file (creates if absent).</summary>
    Task AppendAsync(string key, string content);

    /// <summary>Returns true if the key exists.</summary>
    Task<bool> ExistsAsync(string key);

    /// <summary>
    /// Lists all keys matching a prefix, e.g. "memory/" returns all daily notes.
    /// Results are relative keys, sorted ascending.
    /// </summary>
    Task<IReadOnlyList<string>> ListAsync(string prefix = "");

    /// <summary>Keyword/semantic search across all stored content.</summary>
    Task<List<MemorySearchResult>> SearchAsync(string query, int maxResults = 5);
}
