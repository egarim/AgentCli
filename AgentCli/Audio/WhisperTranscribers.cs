using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentCli;

// ─── Shared base for OpenAI-compatible Whisper endpoints ─────────────────────

/// <summary>
/// Base for all providers that implement the OpenAI /audio/transcriptions endpoint.
/// Groq, OpenAI, Azure OpenAI, and any OpenAI-compatible proxy all share this.
/// </summary>
public abstract class WhisperCompatibleTranscriber : IAudioTranscriber
{
    protected readonly HttpClient _http;

    protected WhisperCompatibleTranscriber(HttpClient http) => _http = http;

    public abstract string   ProviderId   { get; }
    public abstract string   DisplayName  { get; }
    public abstract TimeSpan? MaxDuration { get; }

    public virtual IReadOnlyList<string> SupportedMimeTypes =>
    [
        "audio/ogg", "audio/mpeg", "audio/mp3", "audio/mp4",
        "audio/m4a", "audio/wav", "audio/webm", "audio/flac",
        "audio/x-m4a", "video/mp4"
    ];

    protected abstract string EndpointUrl { get; }
    protected abstract void   SetAuthHeaders(HttpRequestMessage req);
    protected virtual  string ModelName    => "whisper-1";

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioInput input, CancellationToken ct = default)
    {
        // Read stream into buffer (needed for multipart form)
        using var ms = new MemoryStream();
        await input.Audio.CopyToAsync(ms, ct);
        ms.Position = 0;

        using var form    = new MultipartFormDataContent();
        var       content = new StreamContent(ms);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(input.MimeType);

        var fileName = input.FileName
            ?? MimeToExtension.GetOrDefault(input.MimeType, "audio.bin");

        form.Add(content, "file", fileName);
        form.Add(new StringContent(ModelName), "model");
        form.Add(new StringContent("json"),    "response_format");

        if (input.Language is { Length: > 0 } lang && lang != "auto")
            form.Add(new StringContent(lang), "language");

        if (input.Hint is { Length: > 0 } hint)
            form.Add(new StringContent(hint), "prompt");

        using var req = new HttpRequestMessage(HttpMethod.Post, EndpointUrl);
        req.Content = form;
        SetAuthHeaders(req);

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var resp = await _http.SendAsync(req, ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new AudioTranscriptionException(
                ProviderId,
                $"{DisplayName} returned {(int)resp.StatusCode}: {body}",
                (int)resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text     = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var langOut  = root.TryGetProperty("language", out var l) ? l.GetString() : null;
        var duration = root.TryGetProperty("duration", out var d)
            ? TimeSpan.FromSeconds(d.GetDouble())
            : sw.Elapsed;

        return new TranscriptionResult(
            Text:             text.Trim(),
            DetectedLanguage: langOut,
            Confidence:       null,     // Whisper API doesn't return confidence
            Duration:         duration,
            ProviderId:       ProviderId);
    }

    private static class MimeToExtension
    {
        private static readonly Dictionary<string, string> Map = new()
        {
            ["audio/ogg"]    = "audio.ogg",
            ["audio/mpeg"]   = "audio.mp3",
            ["audio/mp3"]    = "audio.mp3",
            ["audio/mp4"]    = "audio.mp4",
            ["audio/m4a"]    = "audio.m4a",
            ["audio/x-m4a"]  = "audio.m4a",
            ["audio/wav"]    = "audio.wav",
            ["audio/webm"]   = "audio.webm",
            ["audio/flac"]   = "audio.flac",
            ["video/mp4"]    = "audio.mp4",
        };

        public static string GetOrDefault(string mime, string fallback)
            => Map.TryGetValue(mime, out var ext) ? ext : fallback;
    }
}

// ─── Provider 1: OpenAI Whisper ───────────────────────────────────────────────

/// <summary>
/// OpenAI Whisper — POST https://api.openai.com/v1/audio/transcriptions
/// Cost: $0.006/minute. Best accuracy across languages.
/// Env: OPENAI_API_KEY
/// </summary>
public sealed class OpenAiWhisperTranscriber : WhisperCompatibleTranscriber
{
    private readonly string _apiKey;

    public OpenAiWhisperTranscriber(HttpClient http, string apiKey)
        : base(http) => _apiKey = apiKey;

    public override string    ProviderId   => "openai-whisper";
    public override string    DisplayName  => "OpenAI Whisper";
    public override TimeSpan? MaxDuration  => TimeSpan.FromMinutes(25); // OpenAI limit
    protected override string EndpointUrl  => "https://api.openai.com/v1/audio/transcriptions";
    protected override string ModelName    => "whisper-1";

    protected override void SetAuthHeaders(HttpRequestMessage req)
        => req.Headers.Add("Authorization", $"Bearer {_apiKey}");
}

// ─── Provider 2: Groq Whisper ─────────────────────────────────────────────────

/// <summary>
/// Groq Whisper — POST https://api.groq.com/openai/v1/audio/transcriptions
/// Cost: FREE tier (2 hrs/day), then $0.0017/minute. Fastest inference.
/// Model: whisper-large-v3-turbo (better than whisper-1, faster)
/// Env: GROQ_API_KEY
/// </summary>
public sealed class GroqWhisperTranscriber : WhisperCompatibleTranscriber
{
    private readonly string _apiKey;

    public GroqWhisperTranscriber(HttpClient http, string apiKey)
        : base(http) => _apiKey = apiKey;

    public override string    ProviderId   => "groq-whisper";
    public override string    DisplayName  => "Groq Whisper";
    public override TimeSpan? MaxDuration  => TimeSpan.FromMinutes(25);
    protected override string EndpointUrl  => "https://api.groq.com/openai/v1/audio/transcriptions";
    protected override string ModelName    => "whisper-large-v3-turbo";

    protected override void SetAuthHeaders(HttpRequestMessage req)
        => req.Headers.Add("Authorization", $"Bearer {_apiKey}");
}

// ─── Provider 3: Azure OpenAI Whisper ────────────────────────────────────────

/// <summary>
/// Azure OpenAI Whisper deployment.
/// Endpoint format: https://{resource}.openai.azure.com/openai/deployments/{deployment}/audio/transcriptions?api-version=2024-02-01
/// Env: AZURE_OPENAI_API_KEY, AZURE_OPENAI_ENDPOINT, AZURE_WHISPER_DEPLOYMENT
/// </summary>
public sealed class AzureWhisperTranscriber : WhisperCompatibleTranscriber
{
    private readonly string _apiKey;
    private readonly string _endpoint;

    public AzureWhisperTranscriber(
        HttpClient http, string apiKey, string resourceEndpoint, string deployment,
        string apiVersion = "2024-02-01")
        : base(http)
    {
        _apiKey   = apiKey;
        _endpoint = $"{resourceEndpoint.TrimEnd('/')}/openai/deployments/{deployment}" +
                    $"/audio/transcriptions?api-version={apiVersion}";
    }

    public override string    ProviderId   => "azure-whisper";
    public override string    DisplayName  => "Azure OpenAI Whisper";
    public override TimeSpan? MaxDuration  => TimeSpan.FromMinutes(25);
    protected override string EndpointUrl  => _endpoint;
    protected override string ModelName    => "whisper-1"; // ignored by Azure (uses deployment)

    protected override void SetAuthHeaders(HttpRequestMessage req)
        => req.Headers.Add("api-key", _apiKey);
}

// ─── Provider 4: OpenRouter (via whisper-compatible endpoint) ────────────────

/// <summary>
/// OpenRouter audio transcription — routes to the cheapest available Whisper model.
/// Same OpenAI-compatible API. Check openrouter.ai/models?modality=audio for models.
/// Env: OPENROUTER_API_KEY
/// </summary>
public sealed class OpenRouterWhisperTranscriber : WhisperCompatibleTranscriber
{
    private readonly string _apiKey;
    private readonly string _model;

    public OpenRouterWhisperTranscriber(
        HttpClient http, string apiKey,
        string model = "openai/whisper")
        : base(http)
    {
        _apiKey = apiKey;
        _model  = model;
    }

    public override string    ProviderId   => "openrouter-whisper";
    public override string    DisplayName  => "OpenRouter Whisper";
    public override TimeSpan? MaxDuration  => null;
    protected override string EndpointUrl  => "https://openrouter.ai/api/v1/audio/transcriptions";
    protected override string ModelName    => _model;

    protected override void SetAuthHeaders(HttpRequestMessage req)
    {
        req.Headers.Add("Authorization", $"Bearer {_apiKey}");
        req.Headers.Add("HTTP-Referer", "https://github.com/egarim/AgentCli");
        req.Headers.Add("X-Title",      "AgentCli");
    }
}
