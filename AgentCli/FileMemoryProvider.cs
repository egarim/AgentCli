using System.Text;

namespace AgentCli;

/// <summary>
/// File-system implementation of IMemoryProvider.
/// Keys map directly to paths under the workspace directory.
/// e.g. key "memory/2026-02-28.md" → {workspaceDir}/memory/2026-02-28.md
/// </summary>
public class FileMemoryProvider : IMemoryProvider
{
    private readonly string _root;

    public string Name => "file";

    public FileMemoryProvider(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "memory"));
    }

    public string Resolve(string key) =>
        Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));

    public Task<string?> ReadAsync(string key)
    {
        var path = Resolve(key);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        return File.ReadAllTextAsync(path).ContinueWith(t => (string?)t.Result);
    }

    public async Task WriteAsync(string key, string content)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    public async Task AppendAsync(string key, string content)
    {
        var path = Resolve(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.AppendAllTextAsync(path, content);
    }

    public Task<bool> ExistsAsync(string key) =>
        Task.FromResult(File.Exists(Resolve(key)));

    public async Task<IReadOnlyList<string>> ListAsync(string prefix = "")
    {
        var dir = string.IsNullOrEmpty(prefix)
            ? _root
            : Path.GetFullPath(Path.Combine(_root, prefix.TrimEnd('/', '\\')));

        if (!Directory.Exists(dir)) return Array.Empty<string>();

        var files = Directory.GetFiles(dir, "*.md", SearchOption.AllDirectories);
        var keys  = files
            .Select(f => Path.GetRelativePath(_root, f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(k => k)
            .ToList();

        return await Task.FromResult<IReadOnlyList<string>>(keys);
    }

    public async Task<List<MemorySearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        var results  = new List<MemorySearchResult>();
        var keywords = query.ToLower().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var allKeys  = await ListAsync();

        foreach (var key in allKeys)
        {
            var path = Resolve(key);
            if (!File.Exists(path)) continue;

            var lines = await File.ReadAllLinesAsync(path);
            for (int i = 0; i < lines.Length; i++)
            {
                var lower = lines[i].ToLower();
                var score = keywords.Count(k => lower.Contains(k));
                if (score == 0) continue;

                var start   = Math.Max(0, i - 1);
                var end     = Math.Min(lines.Length - 1, i + 3);
                var snippet = string.Join("\n", lines[start..(end + 1)]);

                results.Add(new MemorySearchResult(
                    Path:    key,
                    Line:    i + 1,
                    Score:   (float)score / keywords.Length,
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
