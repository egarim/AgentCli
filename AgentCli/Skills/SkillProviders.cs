using System.Text.Json;

namespace AgentCli;

/// <summary>
/// Holds registered C# ISkill instances.
/// The default provider — always loaded, no disk/network needed.
/// </summary>
public class InProcessSkillProvider : ISkillProvider
{
    private readonly Dictionary<string, ISkill> _skills = new(StringComparer.OrdinalIgnoreCase);

    public string ProviderName => "in-process";

    public void Register(ISkill skill) => _skills[skill.Manifest.Id] = skill;

    public Task<IReadOnlyList<ISkill>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ISkill>>(_skills.Values.ToList());

    public Task<ISkill?> GetAsync(string skillId, CancellationToken ct = default) =>
        Task.FromResult(_skills.TryGetValue(skillId, out var s) ? s : null);
}

/// <summary>
/// Discovers skills from folders under a root directory.
///
/// Layout:
///   {root}/
///     my-skill/
///       skill.json     ← manifest
///       run            ← executable (run.sh / run.exe / run.py …)
///
/// skill.json:
/// {
///   "id": "my-skill",
///   "description": "Does something",
///   "tools": [
///     {
///       "name":        "do_thing",
///       "description": "Does the thing",
///       "schema":      { "type": "object", "properties": { "input": { "type": "string" } } }
///     }
///   ]
/// }
///
/// Invocation: run '{toolName}' '{argsJson}'
/// stdout → result string returned to agent
/// </summary>
public class FileSkillProvider : ISkillProvider
{
    private readonly string _root;

    public FileSkillProvider(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "skills");
    }

    public string ProviderName => $"file:{_root}";

    public async Task<IReadOnlyList<ISkill>> ListAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return [];
        var skills = new List<ISkill>();
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var skill = await TryLoadAsync(dir, ct);
            if (skill != null) skills.Add(skill);
        }
        return skills;
    }

    public async Task<ISkill?> GetAsync(string skillId, CancellationToken ct = default)
    {
        var dir = Path.Combine(_root, skillId);
        return Directory.Exists(dir) ? await TryLoadAsync(dir, ct) : null;
    }

    private static async Task<ISkill?> TryLoadAsync(string dir, CancellationToken ct)
    {
        var manifestPath = Path.Combine(dir, "skill.json");
        if (!File.Exists(manifestPath)) return null;
        try
        {
            var json    = await File.ReadAllTextAsync(manifestPath, ct);
            var doc     = JsonDocument.Parse(json).RootElement;
            var id      = doc.GetProperty("id").GetString()!;
            var desc    = doc.GetProperty("description").GetString()!;
            var toolArr = doc.GetProperty("tools").EnumerateArray();
            var tools   = toolArr.Select(t => new ToolSpec(
                t.GetProperty("name").GetString()!,
                t.GetProperty("description").GetString()!,
                t.GetProperty("schema")
            )).ToList();

            return new FileSkill(id, desc, tools, dir);
        }
        catch { return null; }
    }
}

/// <summary>Script-backed skill loaded by FileSkillProvider.</summary>
internal class FileSkill : ISkill
{
    private readonly string _dir;

    public FileSkill(string id, string description, List<ToolSpec> tools, string dir)
    {
        _dir     = dir;
        Manifest = new SkillManifest(id, description, tools);
    }

    public SkillManifest Manifest { get; }

    public async Task<string> InvokeAsync(string toolName, JsonElement args, CancellationToken ct = default)
    {
        // Find executable: run, run.sh, run.py, run.exe, run.ps1
        var exts  = new[] { "", ".sh", ".py", ".exe", ".ps1", ".cmd" };
        var exe   = exts.Select(e => Path.Combine(_dir, "run" + e)).FirstOrDefault(File.Exists);
        if (exe == null)
            return $"Error: no run executable found in {_dir}";

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = $"{toolName} {args}",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = _dir,
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            return $"Error (exit {proc.ExitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}";

        return string.IsNullOrWhiteSpace(stdout) ? "(no output)" : stdout.Trim();
    }
}

/// <summary>
/// Composite provider — aggregates multiple ISkillProvider instances.
/// ListAsync returns skills from all providers (first registration wins on id conflict).
/// </summary>
public class CompositeSkillProvider : ISkillProvider
{
    private readonly List<ISkillProvider> _providers;

    public CompositeSkillProvider(params ISkillProvider[] providers) =>
        _providers = [..providers];

    public string ProviderName =>
        string.Join("+", _providers.Select(p => p.ProviderName));

    public async Task<IReadOnlyList<ISkill>> ListAsync(CancellationToken ct = default)
    {
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ISkill>();
        foreach (var p in _providers)
        {
            foreach (var s in await p.ListAsync(ct))
            {
                if (seen.Add(s.Manifest.Id))
                    result.Add(s);
            }
        }
        return result;
    }

    public async Task<ISkill?> GetAsync(string skillId, CancellationToken ct = default)
    {
        foreach (var p in _providers)
        {
            var s = await p.GetAsync(skillId, ct);
            if (s != null) return s;
        }
        return null;
    }
}
