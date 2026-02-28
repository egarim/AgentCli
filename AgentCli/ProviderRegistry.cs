namespace AgentCli;

/// <summary>
/// Registry of available AI providers.
/// Handles selection, config loading from env vars, and active provider switching.
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

    public IReadOnlyList<IAiProvider> All => _providers.Values.OrderBy(p => p.Id).ToList();

    public bool TryGet(string id, out IAiProvider? provider) =>
        _providers.TryGetValue(id, out provider);

    /// <summary>
    /// Creates a registry from environment variables.
    /// Always registers github-copilot if githubToken provided.
    /// Registers others based on env vars (OPENAI_API_KEY, ANTHROPIC_API_KEY, etc.)
    /// </summary>
    public static ProviderRegistry FromEnvironment(
        HttpClient           http,
        CopilotTokenService? tokenService = null,
        string?              githubToken  = null,
        string               defaultId    = "github-copilot")
    {
        var registry = new ProviderRegistry(defaultId);

        // GitHub Copilot
        if (githubToken != null && tokenService != null)
            registry.Register(new GitHubCopilotProvider(http, tokenService, githubToken));

        // OpenAI
        var openAiKey = Env("OPENAI_API_KEY");
        if (openAiKey != null)
            registry.Register(new OpenAiProvider(http, openAiKey));

        // Azure OpenAI
        var azureKey        = Env("AZURE_OPENAI_API_KEY");
        var azureEndpoint   = Env("AZURE_OPENAI_ENDPOINT");
        var azureDeployment = Env("AZURE_OPENAI_DEPLOYMENT");
        if (azureKey != null && azureEndpoint != null && azureDeployment != null)
            registry.Register(new AzureOpenAiProvider(http, azureKey, azureEndpoint, azureDeployment));

        // Anthropic
        var anthropicKey = Env("ANTHROPIC_API_KEY");
        if (anthropicKey != null)
            registry.Register(new AnthropicProvider(http, anthropicKey));

        // Groq
        var groqKey = Env("GROQ_API_KEY");
        if (groqKey != null)
            registry.Register(new GroqProvider(http, groqKey));

        // Mistral
        var mistralKey = Env("MISTRAL_API_KEY");
        if (mistralKey != null)
            registry.Register(new MistralProvider(http, mistralKey));

        // xAI
        var xaiKey = Env("XAI_API_KEY");
        if (xaiKey != null)
            registry.Register(new XAiProvider(http, xaiKey));

        // OpenRouter
        var openRouterKey = Env("OPENROUTER_API_KEY");
        if (openRouterKey != null)
            registry.Register(new OpenRouterProvider(http, openRouterKey));

        // Together AI
        var togetherKey = Env("TOGETHER_API_KEY");
        if (togetherKey != null)
            registry.Register(new TogetherProvider(http, togetherKey));

        // Ollama (always available locally — no key needed)
        var ollamaUrl = Env("OLLAMA_BASE_URL") ?? "http://localhost:11434";
        registry.Register(new OllamaProvider(http, ollamaUrl));

        // If default provider isn't registered, fall back to first available
        if (!registry._providers.ContainsKey(defaultId) && registry._providers.Count > 0)
            registry._activeId = registry._providers.Keys.First();

        return registry;
    }

    private static string? Env(string key) =>
        Environment.GetEnvironmentVariable(key)?.Trim() is { Length: > 0 } v ? v : null;
}
