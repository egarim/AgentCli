namespace AgentCli;

// ─── Core records ─────────────────────────────────────────────────────────────

/// <summary>
/// Audio input to be transcribed. Channel connectors build this from raw message data.
/// </summary>
public sealed record AudioInput(
    Stream   Audio,             // raw audio bytes — caller disposes
    string   MimeType,          // "audio/ogg", "audio/mpeg", "audio/mp4", "audio/wav", etc.
    string?  Language  = null,  // BCP-47 e.g. "en", "es" — null = auto-detect
    string?  Hint      = null,  // optional prompt hint to guide the model
    string?  FileName  = null   // optional, used in multipart upload filename
);

/// <summary>
/// Result from a transcription provider.
/// </summary>
public sealed record TranscriptionResult(
    string   Text,              // the transcript — empty string if nothing detected
    string?  DetectedLanguage,  // BCP-47 if provider returns it, else null
    float?   Confidence,        // 0.0–1.0 if provider returns it, else null
    TimeSpan Duration,          // audio duration if known, else TimeSpan.Zero
    string   ProviderId         // which provider produced this result
);

// ─── Interface ────────────────────────────────────────────────────────────────

/// <summary>
/// Converts audio to text. One implementation per backend.
/// Thread-safe — a single instance is shared across all sessions.
/// </summary>
public interface IAudioTranscriber
{
    /// <summary>Provider identifier e.g. "groq-whisper", "openai-whisper", "local-whisper".</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name for display/logging.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Transcribe audio to text.
    /// Throws <see cref="AudioTranscriptionException"/> on provider error.
    /// Returns empty TranscriptionResult.Text if audio contains no speech.
    /// </summary>
    Task<TranscriptionResult> TranscribeAsync(AudioInput input, CancellationToken ct = default);

    /// <summary>
    /// MIME types this provider can handle directly (without conversion).
    /// e.g. ["audio/ogg", "audio/mpeg", "audio/mp4", "audio/wav", "audio/webm"]
    /// </summary>
    IReadOnlyList<string> SupportedMimeTypes { get; }

    /// <summary>Max audio duration this provider accepts. Null = no limit enforced.</summary>
    TimeSpan? MaxDuration { get; }
}

/// <summary>Thrown when a transcription provider returns an error.</summary>
public sealed class AudioTranscriptionException : Exception
{
    public string ProviderId { get; }
    public int?   StatusCode { get; }

    public AudioTranscriptionException(string providerId, string message,
        int? statusCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ProviderId = providerId;
        StatusCode = statusCode;
    }
}

// ─── Audio options ────────────────────────────────────────────────────────────

/// <summary>Runtime configuration for the audio subsystem.</summary>
public sealed class AudioOptions
{
    /// <summary>Primary provider ID. Default: "groq-whisper".</summary>
    public string  Provider       { get; set; } = "groq-whisper";

    /// <summary>Fallback provider ID if primary fails or hits rate limit. Null = no fallback.</summary>
    public string? FallbackProvider { get; set; } = "local-whisper";

    /// <summary>Default language hint. "auto" = let provider detect. Default: "auto".</summary>
    public string  Language       { get; set; } = "auto";

    /// <summary>Echo the transcript back to the user before the AI reply. Default: true.</summary>
    public bool    EchoTranscript { get; set; } = true;

    /// <summary>Reject audio longer than this. Default: 5 minutes.</summary>
    public TimeSpan MaxDuration   { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Format string for the echo message. {0} = transcript text.</summary>
    public string  EchoFormat     { get; set; } = "🎤 _{0}_";

    /// <summary>Text injected before transcript when passed to agent. {0} = transcript.</summary>
    public string  AgentFormat    { get; set; } = "[Voice message]: {0}";
}
