namespace AgentCli;

// ─── Registry ─────────────────────────────────────────────────────────────────

/// <summary>
/// Holds all registered transcribers. Resolves by provider ID.
/// Supports primary + automatic fallback.
/// </summary>
public sealed class AudioTranscriberRegistry
{
    private readonly Dictionary<string, IAudioTranscriber> _transcribers = new();
    private readonly AudioOptions _options;

    public AudioTranscriberRegistry(AudioOptions options)
        => _options = options;

    public void Register(IAudioTranscriber transcriber)
        => _transcribers[transcriber.ProviderId] = transcriber;

    public IAudioTranscriber? Get(string providerId)
        => _transcribers.GetValueOrDefault(providerId);

    public IReadOnlyList<IAudioTranscriber> All
        => _transcribers.Values.ToList();

    /// <summary>
    /// Build from environment variables + options.
    /// Registers all providers whose credentials are available.
    /// </summary>
    public static AudioTranscriberRegistry Build(AudioOptions options, HttpClient http)
    {
        var registry = new AudioTranscriberRegistry(options);

        // OpenAI Whisper
        var openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(openAiKey))
            registry.Register(new OpenAiWhisperTranscriber(http, openAiKey));

        // Groq Whisper
        var groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        if (!string.IsNullOrEmpty(groqKey))
            registry.Register(new GroqWhisperTranscriber(http, groqKey));

        // Azure Whisper
        var azureKey        = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
        var azureEndpoint   = Environment.GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        var azureDeployment = Environment.GetEnvironmentVariable("AZURE_WHISPER_DEPLOYMENT");
        if (!string.IsNullOrEmpty(azureKey) && !string.IsNullOrEmpty(azureEndpoint)
            && !string.IsNullOrEmpty(azureDeployment))
            registry.Register(new AzureWhisperTranscriber(http, azureKey, azureEndpoint, azureDeployment));

        // OpenRouter Whisper
        var openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        if (!string.IsNullOrEmpty(openRouterKey))
            registry.Register(new OpenRouterWhisperTranscriber(http, openRouterKey));

        // Google Speech
        var googleKey = Environment.GetEnvironmentVariable("GOOGLE_SPEECH_API_KEY");
        if (!string.IsNullOrEmpty(googleKey))
            registry.Register(new GoogleSpeechTranscriber(http, googleKey));

        // AssemblyAI
        var assemblyKey = Environment.GetEnvironmentVariable("ASSEMBLYAI_API_KEY");
        if (!string.IsNullOrEmpty(assemblyKey))
            registry.Register(new AssemblyAiTranscriber(http, assemblyKey));

        // Deepgram
        var deepgramKey = Environment.GetEnvironmentVariable("DEEPGRAM_API_KEY");
        if (!string.IsNullOrEmpty(deepgramKey))
            registry.Register(new DeepgramTranscriber(http, deepgramKey));

        // Local Whisper — always register if binary exists
        var whisperExe   = Environment.GetEnvironmentVariable("WHISPER_CLI_PATH")
                        ?? FindInPath("whisper-cli")
                        ?? FindInPath("main");
        var whisperModel = Environment.GetEnvironmentVariable("WHISPER_MODEL_PATH")
                        ?? FindWhisperModel();

        if (whisperExe != null && whisperModel != null)
            registry.Register(new LocalWhisperTranscriber(whisperExe, whisperModel));

        return registry;
    }

    private static string? FindInPath(string name)
    {
        try
        {
            var result = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "which", Arguments = name,
                RedirectStandardOutput = true, UseShellExecute = false
            });
            var path = result?.StandardOutput.ReadLine()?.Trim();
            result?.WaitForExit();
            return string.IsNullOrEmpty(path) ? null : path;
        }
        catch { return null; }
    }

    private static string? FindWhisperModel()
    {
        var candidates = new[]
        {
            "/opt/whisper/models/ggml-small.bin",
            "/usr/local/share/whisper/ggml-small.bin",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".agentcli", "whisper", "ggml-small.bin"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}

// ─── Pipeline ────────────────────────────────────────────────────────────────

/// <summary>
/// The main audio processing pipeline.
/// 1. Validates audio (duration, mime type)
/// 2. Calls primary transcriber
/// 3. Falls back to fallback transcriber on failure
/// 4. Returns transcript + optional echo message
/// Injected into InboundMessageRouter.
/// </summary>
public sealed class AudioPipeline
{
    private readonly AudioTranscriberRegistry _registry;
    private readonly AudioOptions             _options;

    public AudioPipeline(AudioTranscriberRegistry registry, AudioOptions options)
    {
        _registry = registry;
        _options  = options;
    }

    /// <summary>
    /// Process an inbound audio message.
    /// Returns (transcriptText, echoMessage?) for the router to use.
    /// echoMessage is non-null when AudioOptions.EchoTranscript = true.
    /// </summary>
    public async Task<AudioPipelineResult> ProcessAsync(
        AudioInput input, CancellationToken ct = default)
    {
        var primary  = _registry.Get(_options.Provider);
        var fallback = _options.FallbackProvider != null
            ? _registry.Get(_options.FallbackProvider)
            : null;

        if (primary == null && fallback == null)
            throw new AudioTranscriptionException("none",
                $"No transcription provider available. Configured: '{_options.Provider}'. " +
                $"Registered: {string.Join(", ", _registry.All.Select(t => t.ProviderId))}");

        TranscriptionResult? result = null;
        Exception?           lastEx = null;

        // Try primary
        if (primary != null)
        {
            try { result = await primary.TranscribeAsync(input, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastEx = ex;
                // fall through to fallback
            }
        }

        // Try fallback
        if (result == null && fallback != null)
        {
            try { result = await fallback.TranscribeAsync(input, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new AudioTranscriptionException("all",
                    $"Both primary ({_options.Provider}) and fallback ({_options.FallbackProvider}) failed. " +
                    $"Primary error: {lastEx?.Message}. Fallback error: {ex.Message}",
                    inner: lastEx);
            }
        }

        if (result == null)
            throw lastEx ?? new AudioTranscriptionException("none", "No result from any provider");

        // Build echo message (shown to user so they know what was transcribed)
        string? echoMessage = _options.EchoTranscript && !string.IsNullOrWhiteSpace(result.Text)
            ? string.Format(_options.EchoFormat, result.Text)
            : null;

        // Build agent message (what gets passed into SessionManager.RunAsync)
        var agentText = string.IsNullOrWhiteSpace(result.Text)
            ? "[Voice message contained no audible speech]"
            : string.Format(_options.AgentFormat, result.Text);

        return new AudioPipelineResult(agentText, echoMessage, result);
    }

    /// <summary>True if any transcriber is registered and ready.</summary>
    public bool IsAvailable => _registry.All.Count > 0;
}

/// <summary>Result from AudioPipeline.ProcessAsync.</summary>
public sealed record AudioPipelineResult(
    string               AgentText,    // passed to SessionManager.RunAsync
    string?              EchoMessage,  // sent back to user before AI reply (nullable)
    TranscriptionResult  Raw           // full result for logging/debugging
);
