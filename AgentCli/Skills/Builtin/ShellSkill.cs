using System.Text.Json;

namespace AgentCli;

/// <summary>
/// Sample in-process skill — shell execution + file reading.
///
/// Demonstrates:
///   - Implementing ISkill in C#
///   - Multiple tools in one skill
///   - Why the gate matters: shell_exec is powerful, interactive mode will prompt
///
/// Register in Program.cs:
///   inProcess.Register(new ShellSkill());
/// </summary>
public class ShellSkill : ISkill
{
    public ShellSkill()
    {
        Manifest = new SkillManifest("shell", "Run shell commands and read files on the local machine",
        [
            new ToolSpec(
                "shell_exec",
                "Run a shell command and return stdout. Use for git, ls, cat, etc. Requires permission.",
                new
                {
                    type       = "object",
                    properties = new
                    {
                        command = new { type = "string", description = "Shell command to run" },
                        cwd     = new { type = "string", description = "Working directory (optional)" },
                    },
                    required = new[] { "command" }
                }),

            new ToolSpec(
                "read_file",
                "Read the contents of a local file. Path can be absolute or relative to cwd.",
                new
                {
                    type       = "object",
                    properties = new
                    {
                        path  = new { type = "string", description = "File path to read" },
                        lines = new { type = "integer", description = "Max lines to return (default 200)" },
                    },
                    required = new[] { "path" }
                }),
        ]);
    }

    public SkillManifest Manifest { get; }

    public Task<string> InvokeAsync(string toolName, JsonElement args, CancellationToken ct = default) =>
        toolName switch
        {
            "shell_exec" => RunShell(
                args.GetProperty("command").GetString()!,
                args.TryGetProperty("cwd", out var cwd) ? cwd.GetString() : null,
                ct),

            "read_file" => ReadFile(
                args.GetProperty("path").GetString()!,
                args.TryGetProperty("lines", out var ln) ? ln.GetInt32() : 200),

            _ => Task.FromResult($"Error: ShellSkill has no tool '{toolName}'")
        };

    // ─── Handlers ─────────────────────────────────────────────────────────────

    private static async Task<string> RunShell(string command, string? cwd, CancellationToken ct)
    {
        var isWindows = OperatingSystem.IsWindows();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = isWindows ? "cmd.exe" : "/bin/sh",
            Arguments              = isWindows ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            WorkingDirectory       = cwd ?? Directory.GetCurrentDirectory(),
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;

        // Read with timeout via CancellationToken
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        var combined = (stdout + (string.IsNullOrWhiteSpace(stderr) ? "" : "\n[stderr]\n" + stderr)).Trim();
        if (string.IsNullOrWhiteSpace(combined)) combined = $"(exit {proc.ExitCode}, no output)";

        // Truncate to keep model context reasonable
        const int max = 3000;
        return combined.Length > max ? combined[..max] + $"\n…(truncated, {combined.Length} total chars)" : combined;
    }

    private static async Task<string> ReadFile(string path, int maxLines)
    {
        if (!File.Exists(path)) return $"Error: file not found — {path}";

        var lines = await File.ReadAllLinesAsync(path);
        var taken = lines.Take(maxLines).ToArray();
        var result = string.Join('\n', taken);

        if (taken.Length < lines.Length)
            result += $"\n…({lines.Length - taken.Length} more lines, increase 'lines' to see them)";

        return result;
    }
}
