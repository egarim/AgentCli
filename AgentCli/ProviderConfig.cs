using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentCli;

/// <summary>
/// Persistent provider configuration stored in ~/.agentcli/providers.json
///
/// Structure:
/// {
///   "default": "openai",
///   "providers": {
///     "openai":       { "apiKey": "sk-...", "model": "gpt-4o" },
///     "azure-openai": { "apiKey": "...", "endpoint": "https://x.openai.azure.com",
///                       "deployment": "gpt-4o", "apiVersion": "2024-02-01" },
///     "anthropic":    { "apiKey": "sk-ant-..." },
///     "groq":         { "apiKey": "gsk_..." },
///     "mistral":      { "apiKey": "..." },
///     "xai":          { "apiKey": "..." },
///     "openrouter":   { "apiKey": "..." },
///     "together":     { "apiKey": "..." },
///     "ollama":       { "baseUrl": "http://localhost:11434", "model": "llama3.2" }
///   }
/// }
/// </summary>
public class ProviderConfig
{
    private readonly string _path;
    private JsonObject _root;

    public ProviderConfig(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".agentcli", "providers.json");
        _root = new JsonObject();
    }

    // ─── Load / Save ──────────────────────────────────────────────────────────

    public async Task LoadAsync()
    {
        if (!File.Exists(_path)) return;
        try
        {
            var text = await File.ReadAllTextAsync(_path);
            _root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
        }
        catch { _root = new JsonObject(); }
    }

    public async Task SaveAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var opts = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(_path, _root.ToJsonString(opts));
    }

    // ─── Default provider ─────────────────────────────────────────────────────

    public string? DefaultProvider
    {
        get => _root["default"]?.GetValue<string>();
        set
        {
            if (value == null) _root.Remove("default");
            else _root["default"] = value;
        }
    }

    // ─── Per-provider settings ────────────────────────────────────────────────

    public string? Get(string providerId, string key)
    {
        var section = _root["providers"]?[providerId];
        return section?[key]?.GetValue<string>();
    }

    public void Set(string providerId, string key, string? value)
    {
        var providers = _root["providers"] as JsonObject ?? new JsonObject();
        _root["providers"] = providers;

        var section = providers[providerId] as JsonObject ?? new JsonObject();
        providers[providerId] = section;

        if (value == null) section.Remove(key);
        else section[key] = value;
    }

    public void Remove(string providerId)
    {
        (_root["providers"] as JsonObject)?.Remove(providerId);
    }

    public IReadOnlyDictionary<string, JsonObject> AllProviders()
    {
        var result = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (_root["providers"] is not JsonObject providers) return result;
        foreach (var kvp in providers)
        {
            if (kvp.Value is JsonObject obj)
                result[kvp.Key] = obj;
        }
        return result;
    }

    // ─── Convenience getters ──────────────────────────────────────────────────

    public string? ApiKey(string id)      => Get(id, "apiKey");
    public string? Model(string id)       => Get(id, "model");
    public string? BaseUrl(string id)     => Get(id, "baseUrl");
    public string? Endpoint(string id)    => Get(id, "endpoint");
    public string? Deployment(string id)  => Get(id, "deployment");
    public string? ApiVersion(string id)  => Get(id, "apiVersion");

    public string ConfigPath => _path;
}
