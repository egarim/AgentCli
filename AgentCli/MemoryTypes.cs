namespace AgentCli;

/// <summary>A single memory search hit.</summary>
public record MemorySearchResult(
    string Path,
    int    Line,
    float  Score,
    string Snippet
);

/// <summary>A file loaded from WORKFLOW_AUTO.md on startup.</summary>
public record StartupFile(string RelativePath, string Content);
