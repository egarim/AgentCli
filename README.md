# AgentCli

A local-first C# console AI agent with persistent memory, multi-provider support, and a tool-call loop. Think of it as a personal assistant that runs in your terminal and remembers things between sessions.

## Features

- **10 AI providers** — GitHub Copilot, OpenAI, Azure OpenAI, Anthropic, Groq, Mistral, xAI, OpenRouter, Together AI, Ollama
- **Persistent memory** — SOUL.md, MEMORY.md, daily notes — all in `~/.agentcli/workspace/`
- **Tool-call loop** — agent can call tools (web fetch, memory read/write, time, etc.) and loop until done
- **Streaming** — all providers stream token-by-token to the console
- **Config file** — `~/.agentcli/providers.json` for persistent API keys and defaults
- **Live switching** — change provider mid-session with `/switch`

---

## Quick Start

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- A GitHub account with [GitHub Copilot](https://github.com/features/copilot) (for the default provider), **or** an API key for any other supported provider

### Run

```bash
git clone https://github.com/egarim/AgentCli
cd AgentCli
dotnet run --project AgentCli
```

On first run you'll be prompted to authorize GitHub Copilot via device flow (open a URL, enter a code). After that, the token is cached and reused.

---

## Providers

| ID | Service | Default model |
|---|---|---|
| `github-copilot` | GitHub Copilot (default) | claude-sonnet-4.5 |
| `openai` | OpenAI | gpt-4o |
| `azure-openai` | Azure OpenAI | *(your deployment)* |
| `anthropic` | Anthropic Claude | claude-sonnet-4-5 |
| `groq` | Groq | llama-3.3-70b-versatile |
| `mistral` | Mistral AI | mistral-large-latest |
| `xai` | xAI (Grok) | grok-3-fast |
| `openrouter` | OpenRouter | openrouter/auto |
| `together` | Together AI | Llama-3.3-70B |
| `ollama` | Ollama (local) | llama3.2 |

Ollama is always available — no API key needed. All other providers are registered automatically when a key is found (from config file or env var).

---

## Configuration

Settings are resolved in this order — **highest priority wins**:

```
CLI flags  >  providers.json  >  environment variables
```

### 1. Config file (recommended)

Use `/config` commands inside the REPL. Changes are saved immediately and take effect without restarting.

```
/config set openai apiKey sk-abc123...
/config set openai model gpt-4o-mini
/config set anthropic apiKey sk-ant-...
/config set azure-openai apiKey <key>
/config set azure-openai endpoint https://myresource.openai.azure.com
/config set azure-openai deployment gpt-4o
/config set azure-openai apiVersion 2024-02-01
/config set ollama baseUrl http://192.168.1.100:11434
/config set ollama model llama3.2
/config default openai
/config show
```

The config is stored at `~/.agentcli/providers.json`:

```json
{
  "default": "openai",
  "providers": {
    "openai": { "apiKey": "sk-...", "model": "gpt-4o-mini" },
    "anthropic": { "apiKey": "sk-ant-..." },
    "ollama": { "baseUrl": "http://localhost:11434", "model": "llama3.2" }
  }
}
```

### 2. Environment variables

| Provider | Variables |
|---|---|
| `openai` | `OPENAI_API_KEY` |
| `azure-openai` | `AZURE_OPENAI_API_KEY`, `AZURE_OPENAI_ENDPOINT`, `AZURE_OPENAI_DEPLOYMENT`, `AZURE_OPENAI_API_VERSION` |
| `anthropic` | `ANTHROPIC_API_KEY` |
| `groq` | `GROQ_API_KEY` |
| `mistral` | `MISTRAL_API_KEY` |
| `xai` | `XAI_API_KEY` |
| `openrouter` | `OPENROUTER_API_KEY` |
| `together` | `TOGETHER_API_KEY` |
| `ollama` | `OLLAMA_BASE_URL` *(optional, defaults to localhost:11434)* |

```bash
OPENAI_API_KEY=sk-... dotnet run --project AgentCli
```

### 3. CLI flags

```bash
# Select provider and model at startup
dotnet run --project AgentCli -- --provider groq --model llama-3.3-70b-versatile

# Force re-login for GitHub Copilot
dotnet run --project AgentCli -- --login
```

---

## REPL Commands

### Providers

| Command | Description |
|---|---|
| `/providers` | List all registered providers — shows active, default model, config/env key status |
| `/switch <id>` | Switch active provider mid-session (rebuilds agent, keeps memory context) |

### Config

| Command | Description |
|---|---|
| `/config show` | Show full config (API keys masked) |
| `/config set <provider> <key> <value>` | Set a config value, save, rebuild registry |
| `/config get <provider> <key>` | Read a config value |
| `/config unset <provider> <key>` | Remove a config value |
| `/config default <provider>` | Set the default provider |

### Memory

| Command | Description |
|---|---|
| `/memory` | Print `MEMORY.md` |
| `/soul` | Print `SOUL.md` (agent personality) |
| `/daily` | Print today's daily note |
| `/workflow` | Print `WORKFLOW_AUTO.md` (startup file list) |

---

## Memory System

The agent has persistent memory across sessions stored in `~/.agentcli/workspace/`:

| File | Purpose |
|---|---|
| `SOUL.md` | Agent personality — injected into every system prompt |
| `MEMORY.md` | Long-term curated memory — agent writes here via `memory_write` tool |
| `memory/YYYY-MM-DD.md` | Daily notes — agent appends via `daily_note` tool |
| `WORKFLOW_AUTO.md` | List of files auto-loaded on every startup |

On first run, `SOUL.md`, `MEMORY.md`, and `WORKFLOW_AUTO.md` are created with sensible defaults. Edit them to customize the agent's personality and persistent context.

### Tools available to the agent

| Tool | Description |
|---|---|
| `get_time` | Current local date and time |
| `web_fetch` | Fetch plain text from a URL |
| `memory_write` | Save a fact to MEMORY.md |
| `memory_search` | Keyword search across all memory files |
| `memory_read_all` | Read full MEMORY.md |
| `daily_note` | Append to today's daily note |
| `workflow_auto_read` | Read WORKFLOW_AUTO.md |
| `workflow_auto_write` | Overwrite WORKFLOW_AUTO.md |

---

## Architecture

```
AgentCli/
├── Program.cs                  # Entry point, REPL, tool registration
├── AgentLoop.cs                # Tool-call loop — provider-agnostic
├── IAiProvider.cs              # Provider interface
├── OpenAiCompatibleProvider.cs # Base class for OpenAI-compatible endpoints
├── AiProviders.cs              # All provider implementations
├── GitHubCopilotProvider.cs    # Copilot device auth + token exchange
├── ProviderRegistry.cs         # Registry + Build() factory (merges 3 config sources)
├── ProviderConfig.cs           # Load/save ~/.agentcli/providers.json
├── GitHubDeviceAuth.cs         # GitHub OAuth device flow
├── CopilotTokenService.cs      # Copilot short-lived token cache
├── MemorySystem.cs             # Memory coordinator (soul, long-term, daily, workflow)
├── IMemoryProvider.cs          # Storage backend interface
├── FileMemoryProvider.cs       # File-system implementation
└── MemoryTypes.cs              # Shared records (ChatMessage, ToolDefinition, etc.)
```

### Adding a new provider

1. Extend `OpenAiCompatibleProvider` (for OpenAI-compatible APIs) or implement `IAiProvider` directly
2. Register it in `ProviderRegistry.Build()`

```csharp
public class MyProvider : OpenAiCompatibleProvider
{
    public MyProvider(HttpClient http, string apiKey)
        : base(http, "my-provider", "My Service", "https://api.example.com/v1", "my-model") =>
        _apiKey = apiKey;

    protected override void SetAuthHeaders(HttpRequestMessage req) =>
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");

    private readonly string _apiKey;
}
```

### Adding a new tool

```csharp
agent.RegisterTool(
    name:        "my_tool",
    description: "Does something useful",
    schema: new {
        type       = "object",
        properties = new { input = new { type = "string" } },
        required   = new[] { "input" }
    },
    handler: async args => {
        var input = args.GetProperty("input").GetString()!;
        return await DoSomethingAsync(input);
    });
```

---

## License

MIT
