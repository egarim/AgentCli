using System.Text.Json;

namespace AgentCli;

/// <summary>
/// A single skill — knows its manifest and how to invoke each of its tools.
/// Implement this in C# for compiled skills; FileSkillProvider wraps script-based ones.
/// </summary>
public interface ISkill
{
    SkillManifest Manifest { get; }

    /// <summary>Invoke a named tool with the given args. Return the result string.</summary>
    Task<string> InvokeAsync(string toolName, JsonElement args, CancellationToken ct = default);
}

/// <summary>
/// Discovers and vends skills.
/// Swap implementations: in-process, file-system, remote registry, etc.
/// </summary>
public interface ISkillProvider
{
    string ProviderName { get; }

    /// <summary>Return all available skills.</summary>
    Task<IReadOnlyList<ISkill>> ListAsync(CancellationToken ct = default);

    /// <summary>Return a specific skill by id, or null if not found.</summary>
    Task<ISkill?> GetAsync(string skillId, CancellationToken ct = default);
}
