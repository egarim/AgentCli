namespace AgentCli;

/// <summary>
/// Abstraction over any LLM inference provider.
/// To add a new provider: implement this interface and register it.
///
/// Providers supported:
///   github-copilot  — device auth, short-lived token exchange
///   openai          — API key, https://api.openai.com/v1
///   azure-openai    — API key + endpoint + deployment
///   anthropic       — API key, https://api.anthropic.com
///   groq            — API key, https://api.groq.com/openai/v1
///   mistral         — API key, https://api.mistral.ai/v1
///   ollama          — no auth, http://localhost:11434/v1
///   openrouter      — API key, https://openrouter.ai/api/v1
///   xai             — API key, https://api.x.ai/v1
///   together        — API key, https://api.together.xyz/v1
///   google-gemini   — API key, https://generativelanguage.googleapis.com/v1beta
/// </summary>
public interface IAiProvider
{
    /// <summary>Unique id, e.g. "openai", "github-copilot", "azure-openai".</summary>
    string Id { get; }

    /// <summary>Human-readable name for display.</summary>
    string DisplayName { get; }

    /// <summary>Default model for this provider (used when model not specified).</summary>
    string DefaultModel { get; }

    /// <summary>
    /// Streams a chat completion. Yields raw SSE data JSON strings (OpenAI format).
    /// Provider is responsible for auth, base URL, and required headers.
    /// </summary>
    IAsyncEnumerable<string> StreamAsync(
        List<ChatMessage>     messages,
        string                model,
        List<ToolDefinition>? tools = null,
        CancellationToken     ct    = default
    );
}
