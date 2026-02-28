# Sample Skills

Ready-to-use skills you can drop into `~/.agentcli/skills/`.

---

## weather (script-based, Python)

Fetches current weather and a short forecast from [wttr.in](https://wttr.in) — no API key needed.

**Install:**

```bash
cp -r sample-skills/weather ~/.agentcli/skills/weather
chmod +x ~/.agentcli/skills/weather/run.py
```

Then inside AgentCli:

```
/skills reload
```

**Tools exposed:**

| Tool | Description |
|---|---|
| `get_weather` | Current conditions + forecast for any city, airport code, or coordinates |

**Example prompts:**
- `What's the weather in Berlin?`
- `Will it rain in Tokyo tomorrow?`
- `Compare weather in New York and London`

**Requirements:** Python 3 (uses only stdlib — `urllib`, `json`, `sys`)

---

## How script skills work

Each skill is a folder with two files:

```
~/.agentcli/skills/
  my-skill/
    skill.json   ← manifest: id, description, tools[]
    run.py       ← called by AgentCli with (toolName, argsJson)
```

AgentCli calls your script as:

```bash
./run.py <toolName> '<argsJson>'
```

Stdout is returned to the agent as the tool result. Non-zero exit code → error.

The script can be any language — Python, Bash, Node, Ruby, compiled binary. Just make it executable and name it `run` (with any extension, or none).

**Minimal Bash example:**

```bash
#!/bin/bash
# run.sh
TOOL=$1
ARGS=$2

if [ "$TOOL" = "say_hello" ]; then
  NAME=$(echo "$ARGS" | python3 -c "import sys,json; print(json.load(sys.stdin)['name'])")
  echo "Hello, $NAME!"
else
  echo "Error: unknown tool '$TOOL'" >&2
  exit 1
fi
```

---

## How C# in-process skills work

Implement `ISkill` and register in `Program.cs`:

```csharp
public class MySkill : ISkill
{
    public SkillManifest Manifest { get; } = new("my-skill", "Does something useful",
    [
        new ToolSpec("my_tool", "Does the thing",
            new { type = "object", properties = new { input = new { type = "string" } }, required = new[] { "input" } })
    ]);

    public Task<string> InvokeAsync(string toolName, JsonElement args, CancellationToken ct = default) =>
        Task.FromResult($"You said: {args.GetProperty("input").GetString()}");
}
```

```csharp
// Program.cs
inProcess.Register(new MySkill());
```

No restart needed — just `/skills reload` after registering dynamically, or restart if adding at startup.

---

## Built-in skills (always loaded)

| Skill | Tools |
|---|---|
| `core` | `get_time`, `web_fetch`, `memory_write`, `memory_search`, `memory_read_all`, `daily_note`, `workflow_auto_read`, `workflow_auto_write` |
| `shell` | `shell_exec`, `read_file` |

> **Note:** `shell_exec` is powerful. In the default `interactive` gate mode, AgentCli will always ask before running it. Use `/permissions deny shell_exec` to block it entirely, or `/permissions allow shell_exec` to allow without prompting.
