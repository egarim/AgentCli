using System.Text.Json;

namespace AgentCli;

// ─── Skill manifest ───────────────────────────────────────────────────────────

/// <summary>Describes a skill and the tools it exposes.</summary>
public record SkillManifest(
    string           Id,
    string           Description,
    IReadOnlyList<ToolSpec> Tools
);

/// <summary>One tool exposed by a skill.</summary>
public record ToolSpec(
    string Name,
    string Description,
    object Schema         // anonymous object or JsonElement — serialised to JSON for the model
);

// ─── Permission types ─────────────────────────────────────────────────────────

public enum PermissionDecision { Allow, Deny }

public record PermissionResult(PermissionDecision Decision, string? Reason = null)
{
    public bool IsAllowed => Decision == PermissionDecision.Allow;
    public static PermissionResult Allow()                     => new(PermissionDecision.Allow);
    public static PermissionResult Deny(string? reason = null) => new(PermissionDecision.Deny, reason);
}
