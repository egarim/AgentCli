using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentCli;

// ─── Provider 5: Google Speech-to-Text ───────────────────────────────────────

/// <summary>
/// Google Cloud Speech-to-Text v1 REST API.
/// Cost: $0.016/minute (standard). Excellent for Latin American Spanish.
/// Requires audio to be base64-encoded in the request body.
/// Env: GOOGLE_SPEECH_API_KEY  (or use a service account JSON via GOOGLE_APPLICATION_CREDENTIALS)
///
/// Supported mime types are mapped to Google encoding enums.
/// Note: Google requires LINEAR16 or FLAC for best accuracy;
///       for OGG/Opus (Telegram/WhatsApp) use encoding = OGG_OPUS.
/// </summary>
public sealed class GoogleSpeechTranscriber : IAudioTranscriber
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;

    public GoogleSpeechTranscriber(HttpClient http, string apiKey)
    {
        _http   = http;
        _apiKey = apiKey;
    }

    public string    ProviderId  => "google-speech";
    public string    DisplayName => "Google Speech-to-Text";
    public TimeSpan? MaxDuration => TimeSpan.FromMinutes(1); // synchronous limit; use LongRunning for more
    public IReadOnlyList<string> SupportedMimeTypes =>
    [
        "audio/ogg", "audio/flac", "audio/wav", "audio/mpeg",
        "audio/mp3", "audio/webm", "audio/mp4", "audio/m4a"
    ];

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioInput input, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();
        await input.Audio.CopyToAsync(ms, ct);
        var audioB64 = Convert.ToBase64String(ms.ToArray());

        var encoding     = MimeToGoogleEncoding(input.MimeType);
        var sampleRate   = encoding == "OGG_OPUS" ? 48000 : 16000;
        var languageCode = NormalizeLanguage(input.Language);

        var body = new
        {
            config = new
            {
                encoding,
                sampleRateHertz  = sampleRate,
                languageCode,
                enableAutomaticPunctuation = true,
                model            = "latest_long",
                speechContexts   = input.Hint != null
                    ? new[] { new { phrases = new[] { input.Hint } } }
                    : null
            },
            audio = new { content = audioB64 }
        };

        var url     = $"https://speech.googleapis.com/v1/speech:recognize?key={_apiKey}";
        var reqBody = new StringContent(
            JsonSerializer.Serialize(body, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
            Encoding.UTF8, "application/json");

        using var req  = new HttpRequestMessage(HttpMethod.Post, url) { Content = reqBody };
        var sw         = System.Diagnostics.Stopwatch.StartNew();
        var resp       = await _http.SendAsync(req, ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new AudioTranscriptionException(ProviderId,
                $"Google Speech returned {(int)resp.StatusCode}: {err}", (int)resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;

        if (!root.TryGetProperty("results", out var results))
            return new TranscriptionResult("", languageCode, null, sw.Elapsed, ProviderId);

        var sb         = new StringBuilder();
        float totalConf = 0f;
        int   count    = 0;

        foreach (var result in results.EnumerateArray())
        {
            if (!result.TryGetProperty("alternatives", out var alts)) continue;
            var alt = alts.EnumerateArray().FirstOrDefault();
            if (alt.ValueKind == JsonValueKind.Undefined) continue;

            if (alt.TryGetProperty("transcript", out var tr))
                sb.Append(tr.GetString()).Append(' ');

            if (alt.TryGetProperty("confidence", out var conf))
            {
                totalConf += conf.GetSingle();
                count++;
            }
        }

        return new TranscriptionResult(
            Text:             sb.ToString().Trim(),
            DetectedLanguage: languageCode,
            Confidence:       count > 0 ? totalConf / count : null,
            Duration:         sw.Elapsed,
            ProviderId:       ProviderId);
    }

    private static string MimeToGoogleEncoding(string mime) => mime switch
    {
        "audio/ogg"  or "audio/ogg; codecs=opus" => "OGG_OPUS",
        "audio/flac"                              => "FLAC",
        "audio/wav"  or "audio/x-wav"            => "LINEAR16",
        "audio/webm"                              => "WEBM_OPUS",
        "audio/mp3"  or "audio/mpeg"              => "MP3",
        "audio/mp4"  or "audio/m4a" or "audio/x-m4a" => "MP3", // best effort
        _                                         => "ENCODING_UNSPECIFIED"
    };

    private static string NormalizeLanguage(string? lang)
        => string.IsNullOrEmpty(lang) || lang == "auto" ? "es-SV" : lang;
    //   ^^^^ default to El Salvador Spanish — change via AudioOptions.Language
}

// ─── Provider 6: AssemblyAI ───────────────────────────────────────────────────

/// <summary>
/// AssemblyAI transcription API.
/// Cost: $0.0065/minute (cheaper than OpenAI, more features than Whisper).
/// Features: speaker diarization, content moderation, auto chapters, sentiment.
/// Two-step: upload audio → poll for completion.
/// Env: ASSEMBLYAI_API_KEY
/// </summary>
public sealed class AssemblyAiTranscriber : IAudioTranscriber
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private readonly TimeSpan   _pollInterval;

    public AssemblyAiTranscriber(
        HttpClient http, string apiKey,
        TimeSpan?  pollInterval = null)
    {
        _http         = http;
        _apiKey       = apiKey;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public string    ProviderId  => "assemblyai";
    public string    DisplayName => "AssemblyAI";
    public TimeSpan? MaxDuration => null; // supports hours via async
    public IReadOnlyList<string> SupportedMimeTypes =>
    [
        "audio/ogg", "audio/mpeg", "audio/mp3", "audio/mp4",
        "audio/m4a", "audio/wav", "audio/webm", "audio/flac",
        "audio/aac", "video/mp4"
    ];

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioInput input, CancellationToken ct = default)
    {
        // Step 1: upload audio
        using var ms = new MemoryStream();
        await input.Audio.CopyToAsync(ms, ct);
        ms.Position = 0;

        using var uploadReq = new HttpRequestMessage(HttpMethod.Post,
            "https://api.assemblyai.com/v2/upload");
        uploadReq.Headers.Add("authorization", _apiKey);
        uploadReq.Content = new StreamContent(ms);
        uploadReq.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(input.MimeType);

        var uploadResp = await _http.SendAsync(uploadReq, ct);
        if (!uploadResp.IsSuccessStatusCode)
        {
            var err = await uploadResp.Content.ReadAsStringAsync(ct);
            throw new AudioTranscriptionException(ProviderId,
                $"AssemblyAI upload failed {(int)uploadResp.StatusCode}: {err}",
                (int)uploadResp.StatusCode);
        }

        var uploadJson = await uploadResp.Content.ReadAsStringAsync(ct);
        using var uploadDoc = JsonDocument.Parse(uploadJson);
        var uploadUrl = uploadDoc.RootElement.GetProperty("upload_url").GetString()!;

        // Step 2: submit transcription job
        var transcribeBody = new
        {
            audio_url         = uploadUrl,
            language_code     = NormalizeLanguage(input.Language),
            punctuate         = true,
            format_text       = true,
            boost_param       = input.Hint != null ? "high" : (string?)null,
            word_boost        = input.Hint != null ? new[] { input.Hint } : null
        };

        using var transcribeReq = new HttpRequestMessage(HttpMethod.Post,
            "https://api.assemblyai.com/v2/transcript");
        transcribeReq.Headers.Add("authorization", _apiKey);
        transcribeReq.Content = new StringContent(
            JsonSerializer.Serialize(transcribeBody,
                new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }),
            Encoding.UTF8, "application/json");

        var transcribeResp = await _http.SendAsync(transcribeReq, ct);
        var transcribeJson = await transcribeResp.Content.ReadAsStringAsync(ct);
        using var transcribeDoc = JsonDocument.Parse(transcribeJson);
        var transcriptId = transcribeDoc.RootElement.GetProperty("id").GetString()!;

        // Step 3: poll for completion
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(_pollInterval, ct);

            using var pollReq = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.assemblyai.com/v2/transcript/{transcriptId}");
            pollReq.Headers.Add("authorization", _apiKey);

            var pollResp = await _http.SendAsync(pollReq, ct);
            var pollJson = await pollResp.Content.ReadAsStringAsync(ct);
            using var pollDoc  = JsonDocument.Parse(pollJson);
            var pollRoot       = pollDoc.RootElement;
            var status         = pollRoot.GetProperty("status").GetString();

            if (status == "completed")
            {
                sw.Stop();
                var text = pollRoot.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                var conf = pollRoot.TryGetProperty("confidence", out var c) ? (float?)c.GetSingle() : null;
                var dur  = pollRoot.TryGetProperty("audio_duration", out var d)
                    ? TimeSpan.FromSeconds(d.GetDouble())
                    : sw.Elapsed;
                var lang = pollRoot.TryGetProperty("language_code", out var l) ? l.GetString() : null;

                return new TranscriptionResult(
                    Text:             text.Trim(),
                    DetectedLanguage: lang,
                    Confidence:       conf,
                    Duration:         dur,
                    ProviderId:       ProviderId);
            }

            if (status == "error")
            {
                var errMsg = pollRoot.TryGetProperty("error", out var e) ? e.GetString() : "unknown";
                throw new AudioTranscriptionException(ProviderId,
                    $"AssemblyAI transcription failed: {errMsg}");
            }

            // status == "queued" | "processing" → keep polling
            if (sw.Elapsed > TimeSpan.FromMinutes(10))
                throw new AudioTranscriptionException(ProviderId,
                    "AssemblyAI transcription timed out after 10 minutes");
        }

        ct.ThrowIfCancellationRequested();
        return new TranscriptionResult("", null, null, sw.Elapsed, ProviderId);
    }

    private static string? NormalizeLanguage(string? lang)
        => string.IsNullOrEmpty(lang) || lang == "auto" ? null : lang;
    //   ^^^^ null = AssemblyAI auto-detects
}

// ─── Provider 7: Local Whisper via whisper.cpp CLI ────────────────────────────

/// <summary>
/// Local Whisper via whisper.cpp CLI executable.
/// Cost: $0.00 — runs on your server CPU.
/// Requires whisper.cpp compiled and model downloaded:
///   https://github.com/ggerganov/whisper.cpp
///   ./models/download-ggml-model.sh small  (or medium, base, tiny)
///
/// Default: looks for 'whisper-cli' or 'main' in PATH, or WhisperExePath config.
/// Model:   looks for ggml-small.bin in WhisperModelPath.
///
/// Note: whisper.cpp requires WAV input. This provider converts OGG/MP3 via
///       ffmpeg if available, or rejects unsupported formats gracefully.
/// </summary>
public sealed class LocalWhisperTranscriber : IAudioTranscriber
{
    private readonly string  _execPath;
    private readonly string  _modelPath;
    private readonly string? _ffmpegPath;

    public LocalWhisperTranscriber(
        string  whisperExePath,
        string  modelPath,
        string? ffmpegPath = null)
    {
        _execPath   = whisperExePath;
        _modelPath  = modelPath;
        _ffmpegPath = ffmpegPath ?? FindFfmpeg();
    }

    public string    ProviderId  => "local-whisper";
    public string    DisplayName => "Local Whisper (whisper.cpp)";
    public TimeSpan? MaxDuration => null; // only limited by CPU time

    public IReadOnlyList<string> SupportedMimeTypes =>
        _ffmpegPath != null
            ? ["audio/wav", "audio/mpeg", "audio/mp3", "audio/ogg",
               "audio/mp4", "audio/m4a", "audio/webm", "audio/flac"]
            : ["audio/wav"]; // without ffmpeg, only WAV

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioInput input, CancellationToken ct = default)
    {
        string wavPath;
        bool   needsCleanup = false;

        if (input.MimeType is "audio/wav" or "audio/x-wav")
        {
            wavPath      = Path.GetTempFileName() + ".wav";
            needsCleanup = true;
            await using var fs = File.Create(wavPath);
            await input.Audio.CopyToAsync(fs, ct);
        }
        else if (_ffmpegPath != null)
        {
            var inputPath = Path.GetTempFileName() + MimeToExt(input.MimeType);
            wavPath       = Path.GetTempFileName() + ".wav";
            needsCleanup  = true;

            await using (var fs = File.Create(inputPath))
                await input.Audio.CopyToAsync(fs, ct);

            var ffmpegPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = _ffmpegPath,
                Arguments              = $"-y -i \"{inputPath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{wavPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };
            var ffmpegProc = System.Diagnostics.Process.Start(ffmpegPsi)!;
            await ffmpegProc.WaitForExitAsync(ct);
            File.Delete(inputPath);

            if (ffmpegProc.ExitCode != 0)
                throw new AudioTranscriptionException(ProviderId, "ffmpeg conversion failed");
        }
        else
        {
            throw new AudioTranscriptionException(ProviderId,
                $"Cannot process {input.MimeType} without ffmpeg.");
        }

        try
        {
            var lang = input.Language is { Length: > 0 } l && l != "auto"
                ? $" --language {l}"
                : "";

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = _execPath,
                Arguments              = $"-m \"{_modelPath}\" -f \"{wavPath}\" --output-txt{lang} --no-timestamps -t 4",
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false
            };

            var sw   = System.Diagnostics.Stopwatch.StartNew();
            var proc = System.Diagnostics.Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            sw.Stop();

            if (proc.ExitCode != 0)
            {
                var stderr = await proc.StandardError.ReadToEndAsync();
                throw new AudioTranscriptionException(ProviderId,
                    $"whisper.cpp exited {proc.ExitCode}: {stderr}");
            }

            return new TranscriptionResult(
                Text:             stdout.Trim(),
                DetectedLanguage: input.Language == "auto" ? null : input.Language,
                Confidence:       null,
                Duration:         sw.Elapsed,
                ProviderId:       ProviderId);
        }
        finally
        {
            if (needsCleanup && File.Exists(wavPath))
                File.Delete(wavPath);
        }
    }

    private static string MimeToExt(string mime) => mime switch
    {
        "audio/ogg"  => ".ogg",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4"  or "audio/m4a" or "audio/x-m4a" => ".m4a",
        "audio/webm" => ".webm",
        "audio/flac" => ".flac",
        _            => ".audio"
    };

    private static string? FindFfmpeg()
    {
        foreach (var name in new[] { "ffmpeg", "/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg" })
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = name, Arguments = "-version",
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false
                });
                p?.WaitForExit();
                if (p?.ExitCode == 0) return name;
            }
            catch { /* not found */ }
        }
        return null;
    }

}

// ─── Provider 8: Deepgram ─────────────────────────────────────────────────────

/// <summary>
/// Deepgram Nova-2 transcription API.
/// Cost: $0.0043/minute (cheapest paid API). Real-time streaming + async batch.
/// Excellent accuracy, very fast (beats Whisper on speed).
/// Env: DEEPGRAM_API_KEY
/// </summary>
public sealed class DeepgramTranscriber : IAudioTranscriber
{
    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private readonly string     _model;

    public DeepgramTranscriber(
        HttpClient http, string apiKey,
        string model = "nova-2")
    {
        _http   = http;
        _apiKey = apiKey;
        _model  = model;
    }

    public string    ProviderId  => "deepgram";
    public string    DisplayName => "Deepgram Nova-2";
    public TimeSpan? MaxDuration => null;
    public IReadOnlyList<string> SupportedMimeTypes =>
    [
        "audio/ogg", "audio/mpeg", "audio/mp3", "audio/mp4",
        "audio/m4a", "audio/wav", "audio/webm", "audio/flac",
        "audio/aac", "audio/ogg; codecs=opus"
    ];

    public async Task<TranscriptionResult> TranscribeAsync(
        AudioInput input, CancellationToken ct = default)
    {
        var lang    = NormalizeLanguage(input.Language);
        var url     = $"https://api.deepgram.com/v1/listen" +
                      $"?model={_model}&punctuate=true&smart_format=true" +
                      $"&language={lang}";

        if (input.Hint != null)
            url += $"&keywords={Uri.EscapeDataString(input.Hint)}";

        using var ms  = new MemoryStream();
        await input.Audio.CopyToAsync(ms, ct);
        ms.Position   = 0;

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Authorization", $"Token {_apiKey}");
        req.Content = new StreamContent(ms);
        req.Content.Headers.ContentType =
            System.Net.Http.Headers.MediaTypeHeaderValue.Parse(input.MimeType);

        var sw   = System.Diagnostics.Stopwatch.StartNew();
        var resp = await _http.SendAsync(req, ct);
        sw.Stop();

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new AudioTranscriptionException(ProviderId,
                $"Deepgram returned {(int)resp.StatusCode}: {err}", (int)resp.StatusCode);
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc  = JsonDocument.Parse(json);
        var root       = doc.RootElement;

        // Deepgram response: results.channels[0].alternatives[0].transcript
        var transcript = root
            .GetProperty("results")
            .GetProperty("channels")[0]
            .GetProperty("alternatives")[0]
            .GetProperty("transcript")
            .GetString() ?? "";

        var confidence = root
            .GetProperty("results")
            .GetProperty("channels")[0]
            .GetProperty("alternatives")[0]
            .TryGetProperty("confidence", out var c) ? (float?)c.GetSingle() : null;

        var duration = root
            .TryGetProperty("metadata", out var meta)
            && meta.TryGetProperty("duration", out var d)
            ? TimeSpan.FromSeconds(d.GetDouble())
            : sw.Elapsed;

        var detectedLang = root
            .TryGetProperty("results", out var results)
            && results.TryGetProperty("channels", out var channels)
            && channels[0].TryGetProperty("detected_language", out var dl)
            ? dl.GetString()
            : null;

        return new TranscriptionResult(
            Text:             transcript.Trim(),
            DetectedLanguage: detectedLang ?? lang,
            Confidence:       confidence,
            Duration:         duration,
            ProviderId:       ProviderId);
    }

    private static string NormalizeLanguage(string? lang)
        => string.IsNullOrEmpty(lang) || lang == "auto" ? "es" : lang;
}
