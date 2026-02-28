using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentCli;

// ─── SivarGatewayConnector ───────────────────────────────────────────────────
//
// Two modes of operation:
//
// MODE A — AgentCli AS the agent backend (embedded / sidecar):
//   The Gateway calls AgentCli via POST /api/messages.
//   AgentCli hosts an ASP.NET minimal API endpoint that receives AgentRequest,
//   runs the SessionManager turn, and returns AgentResponse.
//   → Use: SivarGatewayConnector.CreateAgentApiServer(sessionManager, port)
//
// MODE B — AgentCli CALLING the Gateway as a channel connector:
//   AgentCli sends outbound messages via the Gateway's OutboundMessage endpoint.
//   Useful when running AgentCli standalone and delegating send to the Gateway.
//   → Use: new SivarGatewayConnector(gatewayBaseUrl, sessionManager)
//
// Both modes use the same models as Sivar.Os.Gateway.Core (AgentRequest/Response).
//
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// MODE B — AgentCli as a channel connector that talks to the Sivar.Os.Gateway.
///
/// Receive: webhook from Gateway (POST /agent/inbound from Gateway's AgentApiClient)
/// Send:    POST {gatewayBaseUrl}/channels/{channel}/outbound
///
/// This lets you run AgentCli standalone and point the Gateway's AgentApi:BaseUrl
/// at AgentCli's own minimal API server (see AgentApiServer below).
/// </summary>
public sealed class SivarGatewayConnector : IChannelConnector
{
    private readonly HttpClient _http;
    private readonly ILogger?   _log;
    private Func<ChannelInboundMessage, CancellationToken, Task>? _handler;

    /// <summary>Channel name is "sivar-gateway" — a meta-connector for Gateway calls.</summary>
    public string ChannelName => "sivar-gateway";

    public SivarGatewayConnector(string gatewayBaseUrl, ILogger? logger = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(gatewayBaseUrl),
            Timeout     = TimeSpan.FromSeconds(30),
        };
        _log = logger;
    }

    public Task StartAsync(
        Func<ChannelInboundMessage, CancellationToken, Task> onMessage,
        CancellationToken ct)
    {
        _handler = onMessage;
        _log?.LogInformation("SivarGateway connector ready.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) { _handler = null; return Task.CompletedTask; }

    /// <summary>
    /// Route a Gateway AgentRequest to the local SessionManager and return the AgentResponse.
    /// This is what AgentApiServer calls — it's the bridge between the Gateway and AgentCli.
    /// </summary>
    public static async Task<GatewayAgentResponse> HandleAgentRequestAsync(
        GatewayAgentRequest request,
        SessionManager      sessions,
        CancellationToken   ct)
    {
        var sessionKey = SessionKey.Direct(request.Context.Channel, request.UserId);
        var reply      = await sessions.RunAsync(sessionKey, request.Message, ct);
        return new GatewayAgentResponse(
            Replies: [new GatewayAgentReply(reply)],
            ConversationState: "active");
    }

    public async Task SendAsync(ChannelOutboundMessage message, CancellationToken ct)
    {
        // Forward to Gateway's outbound endpoint
        var payload = JsonSerializer.Serialize(new
        {
            channel = message.To.Channel,
            userId  = message.To.UserId,
            chatId  = message.To.ChatId,
            text    = message.Text,
        });
        var content  = new StringContent(payload, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/channels/outbound", content, ct);
        response.EnsureSuccessStatusCode();
    }
}

// ─── AgentApiServer ──────────────────────────────────────────────────────────

/// <summary>
/// Minimal ASP.NET server that exposes the Sivar.Os.Gateway AgentRequest/Response
/// HTTP contract, allowing the Gateway to use AgentCli as its IAgentService backend.
///
/// The Gateway's Program.cs points AgentApi:BaseUrl at this server:
///   builder.Services.AddHttpClient(IAgentService, AgentApiClient)(client => {
///       client.BaseAddress = new Uri("http://localhost:5050"); // ← this server
///   });
///
/// AgentCli's SessionManager handles per-user history, compaction, and the AI turn.
///
/// Usage:
///   var server = new AgentApiServer(sessionManager, port: 5050);
///   await server.StartAsync(cancellationToken);
/// </summary>
public sealed class AgentApiServer : IAsyncDisposable
{
    private readonly SessionManager _sessions;
    private readonly int            _port;
    private WebApplication?         _app;

    public AgentApiServer(SessionManager sessions, int port = 5050)
    {
        _sessions = sessions;
        _port     = port;
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://localhost:{_port}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(_sessions);

        _app = builder.Build();

        // POST /api/messages — Gateway's AgentApiClient calls this
        _app.MapPost("/api/messages", async (
            GatewayAgentRequest request,
            SessionManager      sessions,
            CancellationToken   cancel) =>
        {
            var response = await SivarGatewayConnector.HandleAgentRequestAsync(
                request, sessions, cancel);
            return Results.Ok(response);
        });

        // GET /health
        _app.MapGet("/health", () => Results.Ok(new
        {
            status    = "healthy",
            service   = "AgentCli",
            timestamp = DateTimeOffset.UtcNow,
        }));

        await _app.StartAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app != null) await _app.DisposeAsync();
    }
}

// ─── Gateway-compatible models ────────────────────────────────────────────────
// Mirrors Sivar.Os.Gateway.Core exactly — use these when talking to/from the Gateway.

public sealed record GatewayAgentRequest(
    [property: JsonPropertyName("userId")]         string         UserId,
    [property: JsonPropertyName("conversationId")] int            ConversationId,
    [property: JsonPropertyName("message")]        string         Message,
    [property: JsonPropertyName("context")]        GatewayAgentContext Context
);

public sealed record GatewayAgentContext(
    [property: JsonPropertyName("channel")]  string  Channel,
    [property: JsonPropertyName("locale")]   string  Locale   = "en-US",
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata = null
);

public sealed record GatewayAgentResponse(
    [property: JsonPropertyName("replies")]           IReadOnlyList<GatewayAgentReply> Replies,
    [property: JsonPropertyName("conversationState")] string ConversationState,
    [property: JsonPropertyName("stateData")]         Dictionary<string, object>? StateData = null
);

public sealed record GatewayAgentReply(
    [property: JsonPropertyName("text")]     string Text,
    [property: JsonPropertyName("metadata")] Dictionary<string, string>? Metadata = null
);
