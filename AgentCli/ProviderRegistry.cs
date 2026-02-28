namespace AgentCli;

/// <summary>
/// Registry of available AI providers.
///
/// Priority for each setting (highest wins):
///   1. CLI flags (--provider, --model)
///   2. providers.json config file  (~/.agentcli/providers.json)
///   3. Environment variables       (OPENAI_API_KEY, etc.)
/// </summary>
public class ProviderRegistry
{
    private readonly Dictionary<string, IAiProvider> _providers = new(StringComparer.OrdinalIgnoreCase);
    private string _activeId;

    public ProviderRegistry(string defaultProviderId)
    {
        _activeId = defaultProviderId;
    }

    public void Register(IAiProvider provider) =>
        _providers[provider.Id] = provider;

    public IAiProvider Active => _providers.TryGetValue(_activeId, out var p)
        ? p
        : throw new Exception($"Provider '{_activeId}' not registered. Available: {string.Join(", ", _providers.Keys)}");

    public string ActiveId => _activeId;

    public void SetActive(string id)
    {
        if (!_providers.ContainsKey(id))
            throw new Exception($"Unknown provider '{id}'. Available: {string.Join(", ", _providers.Keys)}");
        _activeId = id;
    }

    public IReadOnlyList<IAiProvider> All =>
        _providers.Values.OrderBy(p => p.Id).ToList();

    public bool TryGet(string id, out IAiProvider? provider) =>
        _providers.TryGetValue(id, out provider);

    // ─── Factory ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the registry by merging three config sources (highest priority first):
    ///   1. CLI flags   — overrideProvider, overrideApiKeys
    ///   2. Config file — cfg (providers.json)
    ///   3. Env vars    — OPENAI_API_KEY, ANTHROPIC_API_KEY, etc.
    ///
    /// Always registers github-copilot (needs device auth token).
    /// Always registers ollama (no auth).
    /// Others registered only when an API key is available from any source.
    /// </summary>
    public static ProviderRegistry Build(
        HttpClient           http,
        CopilotTokenService? copilotTokenService = null,
        string?              githubToken         = null,
        ProviderConfig?      cfg                 = null,
        string?              overrideProvider    = null,
        Dictionary<string, string>? overrideApiKeys = null)
    {
        // Resolve default: CLI > config > "github-copilot"
        var defaultId = overrideProvider
                        ?? cfg?.DefaultProvider
                        ?? "github-copilot";

        var registry = new ProviderRegistry(defaultId);

        // Helper: resolve a value with priority — CLI override > config > env
        string? Resolve(string providerId, string configKey, string? envVar = null)
        {
            if (overrideApiKeys?.TryGetValue($"{providerId}.{configKey}", out var ov) == true && ov != null)
                return ov;
            var fromCfg = cfg?.Get(providerId, configKey);
            if (fromCfg != null) return fromCfg;
            return envVar != null ? Env(envVar) : null;
        }

        // ── GitHub Copilot (always if token available) ────────────────────────
        if (githubToken != null && copilotTokenService != null)
            registry.Register(new GitHubCopilotProvider(http, copilotTokenService, githubToken));

        // ── OpenAI ────────────────────────────────────────────────────────────
        var openAiKey = Resolve("openai", "apiKey", "OPENAI_API_KEY");
        if (openAiKey != null)
            registry.Register(new OpenAiProvider(http, openAiKey));

        // ── Azure OpenAI ──────────────────────────────────────────────────────
        var azureKey        = Resolve("azure-openai", "apiKey",     "AZURE_OPENAI_API_KEY");
        var azureEndpoint   = Resolve("azure-openai", "endpoint",   "AZURE_OPENAI_ENDPOINT");
        var azureDeployment = Resolve("azure-openai", "deployment", "AZURE_OPENAI_DEPLOYMENT");
        var azureApiVersion = Resolve("azure-openai", "apiVersion", "AZURE_OPENAI_API_VERSION");
        if (azureKey != null && azureEndpoint != null && azureDeployment != null)
            registry.Register(new AzureOpenAiProvider(
                http, azureKey, azureEndpoint, azureDeployment,
                azureApiVersion ?? "2024-02-01"));

        // ── Anthropic ─────────────────────────────────────────────────────────
        var anthropicKey = Resolve("anthropic", "apiKey", "ANTHROPIC_API_KEY");
        if (anthropicKey != null)
            registry.Register(new AnthropicProvider(http, anthropicKey));

        // ── Groq ──────────────────────────────────────────────────────────────
        var groqKey = Resolve("groq", "apiKey", "GROQ_API_KEY");
        if (groqKey != null)
            registry.Register(new GroqProvider(http, groqKey));

        // ── Mistral ───────────────────────────────────────────────────────────
        var mistralKey = Resolve("mistral", "apiKey", "MISTRAL_API_KEY");
        if (mistralKey != null)
            registry.Register(new MistralProvider(http, mistralKey));

        // ── xAI ───────────────────────────────────────────────────────────────
        var xaiKey = Resolve("xai", "apiKey", "XAI_API_KEY");
        if (xaiKey != null)
            registry.Register(new XAiProvider(http, xaiKey));

        // ── OpenRouter ────────────────────────────────────────────────────────
        var openRouterKey = Resolve("openrouter", "apiKey", "OPENROUTER_API_KEY");
        if (openRouterKey != null)
            registry.Register(new OpenRouterProvider(http, openRouterKey));

        // ── Together AI ───────────────────────────────────────────────────────
        var togetherKey = Resolve("together", "apiKey", "TOGETHER_API_KEY");
        if (togetherKey != null)
            registry.Register(new TogetherProvider(http, togetherKey));

        // ── Ollama (always — no auth) ─────────────────────────────────────────
        var ollamaUrl = Resolve("ollama", "baseUrl", "OLLAMA_BASE_URL") ?? "http://localhost:11434";
        registry.Register(new OllamaProvider(http, ollamaUrl));

        // Fall back to first available if default isn't registered
        if (!registry._providers.ContainsKey(defaultId) && registry._providers.Count > 0)
            registry._activeId = registry._providers.Keys.First();

        return registry;
    }

    private static string? Env(string key) =>
        Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } v ? v : null;

    // Keep old method as convenience shim
    public static ProviderRegistry FromEnvironment(
        HttpClient           http,
        CopilotTokenService? tokenService = null,
        string?              githubToken  = null,
        string               defaultId    = "github-copilot")
        => Build(http, tokenService, githubToken);
}
