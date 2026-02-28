using AgentCli;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentCli.Tests;

/// <summary>
/// Unit tests for the audio transcription subsystem.
/// Uses a mock HttpMessageHandler — no real API calls.
/// Integration tests require env vars (skip cleanly without them).
/// </summary>
public class AudioTranscriberTests
{
    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static AudioInput MakeInput(string text = "hello world",
        string mime = "audio/ogg", string? lang = null)
    {
        var bytes  = Encoding.UTF8.GetBytes(text); // dummy bytes stand in for audio
        var stream = new MemoryStream(bytes);
        return new AudioInput(stream, mime, lang);
    }

    private static HttpClient MakeClient(string responseJson,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new MockHttpHandler(responseJson, status);
        return new HttpClient(handler);
    }

    // ─── IAudioTranscriber contract ───────────────────────────────────────────

    [Fact]
    public void AudioInput_Records_AreEqual()
    {
        var s1 = new MemoryStream([1, 2, 3]);
        var s2 = new MemoryStream([1, 2, 3]);
        var a  = new AudioInput(s1, "audio/ogg", "en");
        var b  = new AudioInput(s2, "audio/ogg", "en");
        // records compare by value for primitive fields
        Assert.Equal(a.MimeType,  b.MimeType);
        Assert.Equal(a.Language,  b.Language);
    }

    [Fact]
    public void TranscriptionResult_ProviderId_IsSet()
    {
        var r = new TranscriptionResult("hello", "en", 0.95f,
            TimeSpan.FromSeconds(2), "groq-whisper");
        Assert.Equal("groq-whisper", r.ProviderId);
        Assert.Equal("hello",        r.Text);
        Assert.Equal("en",           r.DetectedLanguage);
    }

    // ─── OpenAI Whisper ───────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAiWhisper_ParsesResponse()
    {
        var json   = """{"text":"Hello from OpenAI","language":"en","duration":3.5}""";
        var client = MakeClient(json);
        var sut    = new OpenAiWhisperTranscriber(client, "sk-test");

        var result = await sut.TranscribeAsync(MakeInput());

        Assert.Equal("openai-whisper",    result.ProviderId);
        Assert.Equal("Hello from OpenAI", result.Text);
        Assert.Equal("en",                result.DetectedLanguage);
        Assert.Equal(3.5,                 result.Duration.TotalSeconds, precision: 1);
    }

    [Fact]
    public async Task OpenAiWhisper_ThrowsOnError()
    {
        var client = MakeClient("""{"error":{"message":"Invalid API key"}}""",
            HttpStatusCode.Unauthorized);
        var sut = new OpenAiWhisperTranscriber(client, "bad-key");

        var ex = await Assert.ThrowsAsync<AudioTranscriptionException>(
            () => sut.TranscribeAsync(MakeInput()));

        Assert.Equal("openai-whisper", ex.ProviderId);
        Assert.Equal(401,              ex.StatusCode);
    }

    [Fact]
    public void OpenAiWhisper_HasCorrectProviderId()
    {
        var sut = new OpenAiWhisperTranscriber(new HttpClient(), "sk-test");
        Assert.Equal("openai-whisper", sut.ProviderId);
        Assert.Equal("OpenAI Whisper", sut.DisplayName);
    }

    // ─── Groq Whisper ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GroqWhisper_ParsesResponse()
    {
        var json   = """{"text":"Hola desde Groq","language":"es","duration":2.1}""";
        var client = MakeClient(json);
        var sut    = new GroqWhisperTranscriber(client, "gsk_test");

        var result = await sut.TranscribeAsync(MakeInput(lang: "es"));

        Assert.Equal("groq-whisper", result.ProviderId);
        Assert.Equal("Hola desde Groq", result.Text);
        Assert.Equal("es",              result.DetectedLanguage);
    }

    [Fact]
    public void GroqWhisper_HasCorrectProviderId()
    {
        var sut = new GroqWhisperTranscriber(new HttpClient(), "gsk_test");
        Assert.Equal("groq-whisper",  sut.ProviderId);
        Assert.Equal("Groq Whisper",  sut.DisplayName);
    }

    [Fact]
    public void GroqWhisper_SupportsManyMimeTypes()
    {
        var sut = new GroqWhisperTranscriber(new HttpClient(), "gsk_test");
        Assert.Contains("audio/ogg",  sut.SupportedMimeTypes);
        Assert.Contains("audio/mpeg", sut.SupportedMimeTypes);
        Assert.Contains("audio/mp4",  sut.SupportedMimeTypes);
        Assert.Contains("audio/wav",  sut.SupportedMimeTypes);
        Assert.Contains("audio/webm", sut.SupportedMimeTypes);
    }

    // ─── Azure Whisper ────────────────────────────────────────────────────────

    [Fact]
    public async Task AzureWhisper_ParsesResponse()
    {
        var json   = """{"text":"Hello from Azure","duration":4.0}""";
        var client = MakeClient(json);
        var sut    = new AzureWhisperTranscriber(client,
            "azure-key", "https://myresource.openai.azure.com", "whisper-dep");

        var result = await sut.TranscribeAsync(MakeInput());

        Assert.Equal("azure-whisper",    result.ProviderId);
        Assert.Equal("Hello from Azure", result.Text);
    }

    [Fact]
    public void AzureWhisper_BuildsCorrectEndpoint()
    {
        var sut = new AzureWhisperTranscriber(new HttpClient(),
            "key", "https://myresource.openai.azure.com", "my-deployment", "2024-06-01");
        Assert.Equal("azure-whisper", sut.ProviderId);
        // EndpointUrl is protected — just verify the provider builds without error
    }

    // ─── Deepgram ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deepgram_ParsesResponse()
    {
        var json = """
            {
              "metadata": { "duration": 5.2 },
              "results": {
                "channels": [{
                  "detected_language": "es",
                  "alternatives": [{
                    "transcript": "Buenos días mundo",
                    "confidence": 0.98
                  }]
                }]
              }
            }
            """;
        var client = MakeClient(json);
        var sut    = new DeepgramTranscriber(client, "dg_test");

        var result = await sut.TranscribeAsync(MakeInput());

        Assert.Equal("deepgram",            result.ProviderId);
        Assert.Equal("Buenos días mundo",   result.Text);
        Assert.Equal("es",                  result.DetectedLanguage);
        Assert.Equal(0.98f,                 result.Confidence!.Value, precision: 2);
        Assert.Equal(5.2,                   result.Duration.TotalSeconds, precision: 1);
    }

    [Fact]
    public async Task Deepgram_ThrowsOnError()
    {
        var client = MakeClient("""{"err_code":"INVALID_AUTH"}""", HttpStatusCode.Unauthorized);
        var sut    = new DeepgramTranscriber(client, "bad-key");

        var ex = await Assert.ThrowsAsync<AudioTranscriptionException>(
            () => sut.TranscribeAsync(MakeInput()));

        Assert.Equal("deepgram", ex.ProviderId);
        Assert.Equal(401,        ex.StatusCode);
    }

    // ─── Google Speech ────────────────────────────────────────────────────────

    [Fact]
    public async Task GoogleSpeech_ParsesResponse()
    {
        var json = """
            {
              "results": [{
                "alternatives": [{
                  "transcript": "hola mundo",
                  "confidence": 0.92
                }]
              }]
            }
            """;
        var client = MakeClient(json);
        var sut    = new GoogleSpeechTranscriber(client, "goog_test");

        var result = await sut.TranscribeAsync(MakeInput(mime: "audio/ogg"));

        Assert.Equal("google-speech", result.ProviderId);
        Assert.Equal("hola mundo",    result.Text);
        Assert.Equal(0.92f,           result.Confidence!.Value, precision: 2);
    }

    [Fact]
    public async Task GoogleSpeech_ReturnsEmpty_WhenNoResults()
    {
        var client = MakeClient("""{}"""); // no "results" key
        var sut    = new GoogleSpeechTranscriber(client, "goog_test");

        var result = await sut.TranscribeAsync(MakeInput());

        Assert.Equal("google-speech", result.ProviderId);
        Assert.Equal("",              result.Text);
    }

    // ─── OpenRouter Whisper ───────────────────────────────────────────────────

    [Fact]
    public async Task OpenRouterWhisper_ParsesResponse()
    {
        var json   = """{"text":"OpenRouter transcript","language":"en"}""";
        var client = MakeClient(json);
        var sut    = new OpenRouterWhisperTranscriber(client, "or_test");

        var result = await sut.TranscribeAsync(MakeInput());

        Assert.Equal("openrouter-whisper",  result.ProviderId);
        Assert.Equal("OpenRouter transcript", result.Text);
    }

    // ─── AudioPipeline ────────────────────────────────────────────────────────

    [Fact]
    public async Task Pipeline_UsesPrimaryProvider()
    {
        var opts     = new AudioOptions { Provider = "groq-whisper", EchoTranscript = true };
        var registry = new AudioTranscriberRegistry(opts);
        registry.Register(new GroqWhisperTranscriber(
            MakeClient("""{"text":"Test transcript","language":"en"}"""), "key"));

        var pipeline = new AudioPipeline(registry, opts);
        var result   = await pipeline.ProcessAsync(MakeInput());

        Assert.Equal("[Voice message]: Test transcript", result.AgentText);
        Assert.NotNull(result.EchoMessage);
        Assert.Contains("Test transcript", result.EchoMessage);
        Assert.Equal("groq-whisper", result.Raw.ProviderId);
    }

    [Fact]
    public async Task Pipeline_FallsBackOnPrimaryFailure()
    {
        var opts = new AudioOptions
        {
            Provider         = "openai-whisper",
            FallbackProvider = "groq-whisper",
            EchoTranscript   = false
        };
        var registry = new AudioTranscriberRegistry(opts);

        // Primary always fails
        registry.Register(new OpenAiWhisperTranscriber(
            MakeClient("""{"error":"rate limited"}""", HttpStatusCode.TooManyRequests), "key"));

        // Fallback succeeds
        registry.Register(new GroqWhisperTranscriber(
            MakeClient("""{"text":"Fallback worked","language":"es"}"""), "key"));

        var pipeline = new AudioPipeline(registry, opts);
        var result   = await pipeline.ProcessAsync(MakeInput());

        Assert.Equal("groq-whisper",                   result.Raw.ProviderId);
        Assert.Equal("[Voice message]: Fallback worked", result.AgentText);
        Assert.Null(result.EchoMessage); // EchoTranscript = false
    }

    [Fact]
    public async Task Pipeline_ThrowsWhenBothFail()
    {
        var opts = new AudioOptions
        {
            Provider         = "openai-whisper",
            FallbackProvider = "groq-whisper"
        };
        var registry = new AudioTranscriberRegistry(opts);

        registry.Register(new OpenAiWhisperTranscriber(
            MakeClient("err", HttpStatusCode.InternalServerError), "key"));
        registry.Register(new GroqWhisperTranscriber(
            MakeClient("err", HttpStatusCode.InternalServerError), "key"));

        var pipeline = new AudioPipeline(registry, opts);

        await Assert.ThrowsAsync<AudioTranscriptionException>(
            () => pipeline.ProcessAsync(MakeInput()));
    }

    [Fact]
    public void Pipeline_IsAvailable_WhenRegistered()
    {
        var opts     = new AudioOptions { Provider = "groq-whisper" };
        var registry = new AudioTranscriberRegistry(opts);
        var pipeline = new AudioPipeline(registry, opts);

        Assert.False(pipeline.IsAvailable);

        registry.Register(new GroqWhisperTranscriber(new HttpClient(), "key"));
        Assert.True(pipeline.IsAvailable);
    }

    [Fact]
    public async Task Pipeline_HandlesEmptyTranscript()
    {
        var opts     = new AudioOptions { Provider = "groq-whisper" };
        var registry = new AudioTranscriberRegistry(opts);
        registry.Register(new GroqWhisperTranscriber(
            MakeClient("""{"text":""}"""), "key"));

        var pipeline = new AudioPipeline(registry, opts);
        var result   = await pipeline.ProcessAsync(MakeInput());

        Assert.Contains("no audible speech", result.AgentText);
        Assert.Null(result.EchoMessage);
    }

    // ─── Registry ─────────────────────────────────────────────────────────────

    [Fact]
    public void Registry_Get_ReturnsNull_ForUnknownId()
    {
        var registry = new AudioTranscriberRegistry(new AudioOptions());
        Assert.Null(registry.Get("nonexistent"));
    }

    [Fact]
    public void Registry_Register_And_Get()
    {
        var registry = new AudioTranscriberRegistry(new AudioOptions());
        var sut      = new GroqWhisperTranscriber(new HttpClient(), "key");
        registry.Register(sut);

        var found = registry.Get("groq-whisper");
        Assert.NotNull(found);
        Assert.Equal("groq-whisper", found!.ProviderId);
    }

    [Fact]
    public void Registry_All_ReturnsAllRegistered()
    {
        var registry = new AudioTranscriberRegistry(new AudioOptions());
        registry.Register(new GroqWhisperTranscriber(new HttpClient(), "key1"));
        registry.Register(new OpenAiWhisperTranscriber(new HttpClient(), "key2"));

        Assert.Equal(2, registry.All.Count);
    }

    // ─── AudioOptions defaults ────────────────────────────────────────────────

    [Fact]
    public void AudioOptions_Defaults_AreValid()
    {
        var opts = new AudioOptions();
        Assert.Equal("groq-whisper",   opts.Provider);
        Assert.Equal("local-whisper",  opts.FallbackProvider);
        Assert.Equal("auto",           opts.Language);
        Assert.True(opts.EchoTranscript);
        Assert.Equal(TimeSpan.FromMinutes(5), opts.MaxDuration);
    }

    // ─── Integration tests (skip without env vars) ────────────────────────────

    [SkippableFact]
    public async Task Integration_GroqWhisper_TranscribesWavFile()
    {
        var key = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        Skip.If(key is null, "GROQ_API_KEY not set");

        // Use a minimal WAV file (44-byte header + silence)
        var wavBytes = CreateSilentWav(durationMs: 500);
        var input    = new AudioInput(new MemoryStream(wavBytes), "audio/wav", "en");
        var sut      = new GroqWhisperTranscriber(new HttpClient(), key!);

        var result = await sut.TranscribeAsync(input);

        Assert.Equal("groq-whisper", result.ProviderId);
        // Silence produces empty or near-empty transcript — just verify no exception
        Assert.NotNull(result.Text);
    }

    [SkippableFact]
    public async Task Integration_OpenAiWhisper_TranscribesWavFile()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        Skip.If(key is null, "OPENAI_API_KEY not set");

        var wavBytes = CreateSilentWav(durationMs: 500);
        var input    = new AudioInput(new MemoryStream(wavBytes), "audio/wav", "en");
        var sut      = new OpenAiWhisperTranscriber(new HttpClient(), key!);

        var result = await sut.TranscribeAsync(input);
        Assert.Equal("openai-whisper", result.ProviderId);
        Assert.NotNull(result.Text);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Create a minimal valid WAV file with silence.</summary>
    private static byte[] CreateSilentWav(int durationMs = 500,
        int sampleRate = 16000, short channels = 1, short bitsPerSample = 16)
    {
        int numSamples  = (sampleRate * durationMs) / 1000;
        int dataSize    = numSamples * channels * (bitsPerSample / 8);
        int fileSize    = 44 + dataSize;

        using var ms  = new MemoryStream(fileSize);
        using var bw  = new System.IO.BinaryWriter(ms);

        // RIFF header
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(fileSize - 8);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        // fmt chunk
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);               // chunk size
        bw.Write((short)1);         // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8)); // byte rate
        bw.Write((short)(channels * (bitsPerSample / 8)));     // block align
        bw.Write(bitsPerSample);

        // data chunk
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        bw.Write(new byte[dataSize]); // silence

        return ms.ToArray();
    }

    // ─── Mock HTTP ────────────────────────────────────────────────────────────

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly string         _response;
        private readonly HttpStatusCode _status;

        public MockHttpHandler(string response, HttpStatusCode status = HttpStatusCode.OK)
        {
            _response = response;
            _status   = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_response, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(resp);
        }
    }
}
