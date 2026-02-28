# AgentCli

A minimal C# console agent using **GitHub Copilot** as the LLM backend — device auth flow, streaming responses, and tool calling. Extracted from how [OpenClaw](https://github.com/openclaw/openclaw) does it.

## How it works

```
GitHub Device Auth (Iv1.b507a08c87ecfe98)
    ↓  ghu_... OAuth token (saved to ~/.agentcli/config.json)
    ↓  GET https://api.github.com/copilot_internal/v2/token
Short-lived Copilot API token (cached 5 min)
    ↓  POST https://api.individual.githubcopilot.com/chat/completions
Streaming SSE → tool calls → loop → final answer
```

## Requirements

- .NET 9 SDK
- GitHub account with **GitHub Copilot** subscription

## Run

```bash
dotnet run --project AgentCli
```

First run opens your browser to authorize. Token saved to `~/.agentcli/config.json`.

To re-login:
```bash
dotnet run --project AgentCli -- --login
```

## Built-in tools

| Tool | Description |
|------|-------------|
| `get_time` | Returns current local date/time |
| `web_fetch` | Fetches text content from a URL |

## Adding tools

```csharp
agent.RegisterTool(
    name: "my_tool",
    description: "What it does",
    schema: new { type = "object", properties = new { input = new { type = "string" } }, required = new[] { "input" } },
    handler: async args => {
        var input = args.GetProperty("input").GetString()!;
        return $"Result: {input}";
    }
);
```

## Key files

| File | Purpose |
|------|---------|
| `GitHubDeviceAuth.cs` | Device code flow — request code, poll for token |
| `CopilotTokenService.cs` | Exchange `ghu_` token → short-lived Copilot API token (disk cache) |
| `CopilotChatClient.cs` | Streaming SSE chat completions client |
| `AgentLoop.cs` | Agentic loop — tool execution, history management |
| `Program.cs` | REPL, login flow, tool registration |
