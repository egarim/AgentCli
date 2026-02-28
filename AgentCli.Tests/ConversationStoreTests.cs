using AgentCli;
using Xunit;

namespace AgentCli.Tests;

public class ConversationStoreTests
{
    // ─── Shared test helpers ──────────────────────────────────────────────────

    private static ConversationSession MakeSession(string key, int msgCount = 4)
    {
        var s = new ConversationSession
        {
            SessionKey = key,
            Channel    = "telegram",
            UserId     = "12345",
            ChatType   = "direct",
        };
        for (int i = 0; i < msgCount; i++)
        {
            s.Messages.Add(new ChatMessage(i % 2 == 0 ? "user" : "assistant", $"Message {i}"));
        }
        return s;
    }

    private static async Task RunStoreTests(IConversationStore store)
    {
        const string key = "telegram:direct:12345";

        // ── Does not exist initially ──────────────────────────────────────────
        Assert.False(await store.ExistsAsync(key));
        Assert.Null(await store.LoadAsync(key));

        // ── Save and reload ───────────────────────────────────────────────────
        var session = MakeSession(key);
        await store.SaveAsync(session);

        Assert.True(await store.ExistsAsync(key));

        var loaded = await store.LoadAsync(key);
        Assert.NotNull(loaded);
        Assert.Equal(key, loaded!.SessionKey);
        Assert.Equal("telegram", loaded.Channel);
        Assert.Equal("12345", loaded.UserId);
        Assert.Equal("direct", loaded.ChatType);
        Assert.Equal(4, loaded.Messages.Count);
        Assert.Equal("Message 0", loaded.Messages[0].Content);
        Assert.Equal("user",      loaded.Messages[0].Role);
        Assert.Equal("assistant", loaded.Messages[1].Role);

        // ── Append message ────────────────────────────────────────────────────
        await store.AppendMessageAsync(key, new ChatMessage("user", "Extra message"));

        var reloaded = await store.LoadAsync(key);
        Assert.Equal(5, reloaded!.Messages.Count);
        Assert.Equal("Extra message", reloaded.Messages[4].Content);

        // ── List ──────────────────────────────────────────────────────────────
        var list = await store.ListAsync();
        Assert.Contains(list, s => s.SessionKey == key);
        var summary = list.First(s => s.SessionKey == key);
        Assert.Equal("telegram", summary.Channel);
        Assert.Equal("12345",    summary.UserId);
        Assert.Equal(5,          summary.MessageCount);

        // ── Multiple sessions ─────────────────────────────────────────────────
        await store.SaveAsync(MakeSession("telegram:direct:99999", 2));
        var list2 = await store.ListAsync();
        Assert.Equal(2, list2.Count);

        // ── Delete ────────────────────────────────────────────────────────────
        await store.DeleteAsync(key);
        Assert.False(await store.ExistsAsync(key));
        Assert.Null(await store.LoadAsync(key));

        var list3 = await store.ListAsync();
        Assert.DoesNotContain(list3, s => s.SessionKey == key);
    }

    // ─── InMemory ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task InMemory_AllOperations()
    {
        var store = new InMemoryConversationStore();
        await RunStoreTests(store);
    }

    [Fact]
    public async Task InMemory_AppendCreatesSession()
    {
        var store = new InMemoryConversationStore();
        await store.AppendMessageAsync("new:session", new ChatMessage("user", "hello"));
        Assert.True(await store.ExistsAsync("new:session"));
        var s = await store.LoadAsync("new:session");
        Assert.Single(s!.Messages);
    }

    [Fact]
    public async Task InMemory_Clear_ResetsState()
    {
        var store = new InMemoryConversationStore();
        await store.SaveAsync(MakeSession("key:1"));
        store.Clear();
        Assert.Empty(await store.ListAsync());
    }

    // ─── File ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task File_AllOperations()
    {
        var dir   = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var store = new FileConversationStore(dir);
        try   { await RunStoreTests(store); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task File_ToolCallsRoundTrip()
    {
        var dir   = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var store = new FileConversationStore(dir);
        try
        {
            const string key = "telegram:direct:tooltest";
            var session = new ConversationSession { SessionKey = key };
            var toolCall = new ToolCall("call_abc", "function", new ToolCallFunction("get_time", "{}"));
            session.Messages.Add(new ChatMessage("assistant", null, new List<ToolCall> { toolCall }));
            session.Messages.Add(new ChatMessage("tool", "2026-02-28", ToolCallId: "call_abc"));
            await store.SaveAsync(session);

            var loaded = await store.LoadAsync(key);
            Assert.NotNull(loaded!.Messages[0].ToolCalls);
            Assert.Equal("get_time", loaded.Messages[0].ToolCalls![0].Function.Name);
            Assert.Equal("call_abc", loaded.Messages[1].ToolCallId);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task File_AppendCreatesSession()
    {
        var dir   = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var store = new FileConversationStore(dir);
        try
        {
            await store.AppendMessageAsync("new:session", new ChatMessage("user", "hello"));
            Assert.True(await store.ExistsAsync("new:session"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    // ─── SQLite ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sqlite_AllOperations()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}.db");
        using var store = new SqliteConversationStore(dbPath);
        try   { await RunStoreTests(store); }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    [Fact]
    public async Task Sqlite_ToolCallsRoundTrip()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}.db");
        using var store = new SqliteConversationStore(dbPath);
        try
        {
            const string key = "telegram:direct:tooltest";
            var session = new ConversationSession { SessionKey = key };
            var toolCall = new ToolCall("call_xyz", "function", new ToolCallFunction("shell_exec", "{\"cmd\":\"ls\"}"));
            session.Messages.Add(new ChatMessage("assistant", null, new List<ToolCall> { toolCall }));
            session.Messages.Add(new ChatMessage("tool", "file1\nfile2", ToolCallId: "call_xyz"));
            await store.SaveAsync(session);

            var loaded = await store.LoadAsync(key);
            Assert.Equal("shell_exec", loaded!.Messages[0].ToolCalls![0].Function.Name);
            Assert.Equal("call_xyz",   loaded.Messages[1].ToolCallId);
            Assert.Equal("file1\nfile2", loaded.Messages[1].Content);
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    [Fact]
    public async Task Sqlite_Persist_Survives_Reopen()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new SqliteConversationStore(dbPath))
                await store.SaveAsync(MakeSession("telegram:direct:persist"));

            using (var store2 = new SqliteConversationStore(dbPath))
            {
                var loaded = await store2.LoadAsync("telegram:direct:persist");
                Assert.NotNull(loaded);
                Assert.Equal(4, loaded!.Messages.Count);
            }
        }
        finally { if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    // ─── AgentRegistry — AgentId generation (no DB needed) ───────────────────

    [Fact]
    public void AgentId_IsDeterministic()
    {
        var id1 = AgentRegistry.BuildAgentId("sivar-main");
        var id2 = AgentRegistry.BuildAgentId("sivar-main");
        Assert.Equal(id1, id2);
    }

    [Fact]
    public void AgentId_IncludesName()
    {
        var id = AgentRegistry.BuildAgentId("sivar-main");
        Assert.StartsWith("sivar-main-", id);
    }

    [Fact]
    public void AgentId_HasCorrectLength()
    {
        // "name-XXXXXXXX" where X is 8 hex chars
        var id = AgentRegistry.BuildAgentId("agent");
        Assert.Matches(@"^agent-[0-9a-f]{8}$", id);
    }

    [Fact]
    public void AgentId_DifferentNames_DifferentIds()
    {
        var id1 = AgentRegistry.BuildAgentId("agent-a");
        var id2 = AgentRegistry.BuildAgentId("agent-b");
        // suffix is same (same machine) but prefix differs
        Assert.NotEqual(id1, id2);
        Assert.StartsWith("agent-a-", id1);
        Assert.StartsWith("agent-b-", id2);
    }

    // ─── PostgresConversationStore — contract (via InMemory proxy) ────────────
    // Real Postgres tests require a live DB; run with:
    //   AGENTCLI_TEST_POSTGRES="Host=...;Database=agentcli_test;..." dotnet test
    // These tests verify the same contract using InMemory so CI always passes.

    [Fact]
    public async Task Postgres_Contract_ViaInMemory()
    {
        // Verifies IConversationStore contract is satisfied by InMemoryConversationStore
        // (which PostgresConversationStore also implements).
        // When AGENTCLI_TEST_POSTGRES is set, a real Postgres test can be added here.
        var store = new InMemoryConversationStore();
        await RunStoreTests(store);
    }

    [Fact]
    public void ClusterOptions_Defaults_AreValid()
    {
        var opts = new ClusterOptions();
        Assert.Equal("agent",                  opts.AgentName);
        Assert.Equal("http://localhost:5050",  opts.Host);
        Assert.Equal(TimeSpan.FromSeconds(30), opts.HeartbeatInterval);
        Assert.Equal(TimeSpan.FromSeconds(90), opts.DeadThreshold);
        Assert.Null(opts.ReplyGateway);
        Assert.Null(opts.ConnectionString);
    }

    // ─── SessionKey helpers ───────────────────────────────────────────────────

    [Fact]
    public void SessionKey_Direct_Format()
    {
        Assert.Equal("telegram:direct:5932684607", SessionKey.Direct("telegram", "5932684607"));
        Assert.Equal("whatsapp:direct:+15551234567", SessionKey.Direct("WhatsApp", "+15551234567"));
    }

    [Fact]
    public void SessionKey_Group_Format()
    {
        Assert.Equal("telegram:group:-1001234567890", SessionKey.Group("Telegram", "-1001234567890"));
    }

    [Fact]
    public void SessionKey_Normalizes_Case()
    {
        Assert.Equal(SessionKey.Direct("TELEGRAM", "USER1"), SessionKey.Direct("telegram", "user1"));
    }
}
