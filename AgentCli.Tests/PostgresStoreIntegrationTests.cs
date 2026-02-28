using AgentCli;
using Xunit;

namespace AgentCli.Tests;

/// <summary>
/// Integration tests against a real Postgres DB.
/// Requires env var AGENTCLI_TEST_POSTGRES to be set, otherwise all tests skip.
///
/// Run with:
///   AGENTCLI_TEST_POSTGRES="Host=86.48.30.121;Port=5432;Database=agentcli_test;Username=postgres;Password=Xa1Hf4M3EnAKG8g" dotnet test
/// </summary>
public class PostgresStoreIntegrationTests
{
    private static readonly string? ConnStr =
        Environment.GetEnvironmentVariable("AGENTCLI_TEST_POSTGRES");

    private static ConversationSession MakeSession(string key, int msgCount = 4)
    {
        var s = new ConversationSession
        {
            SessionKey = key,
            Channel    = "telegram",
            UserId     = "test-user-99",
            ChatType   = "direct",
        };
        for (int i = 0; i < msgCount; i++)
            s.Messages.Add(new ChatMessage(i % 2 == 0 ? "user" : "assistant", $"Message {i}"));
        return s;
    }

    // ─── All operations ───────────────────────────────────────────────────────

    [SkippableFact]
    public async Task Postgres_AllOperations()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");

        var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var _ = store;

        const string key = "telegram:direct:pg-test-all-ops";
        try
        {
            // Not exists initially
            Assert.False(await store.ExistsAsync(key));
            Assert.Null(await store.LoadAsync(key));

            // Save + reload
            var session = MakeSession(key);
            await store.SaveAsync(session);
            Assert.True(await store.ExistsAsync(key));

            var loaded = await store.LoadAsync(key);
            Assert.NotNull(loaded);
            Assert.Equal(key, loaded!.SessionKey);
            Assert.Equal(4, loaded.Messages.Count);
            Assert.Equal("telegram", loaded.Channel);
            Assert.Equal("test-user-99", loaded.UserId);

            // Append
            await store.AppendMessageAsync(key, new ChatMessage("user", "Appended"));
            loaded = await store.LoadAsync(key);
            Assert.Equal(5, loaded!.Messages.Count);
            Assert.Equal("Appended", loaded.Messages.Last().Content);

            // List
            var list = await store.ListAsync();
            Assert.Contains(list, s => s.SessionKey == key);
            var summary = list.First(s => s.SessionKey == key);
            Assert.Equal(5, summary.MessageCount);

            // Delete
            await store.DeleteAsync(key);
            Assert.False(await store.ExistsAsync(key));
        }
        finally
        {
            await store.DeleteAsync(key);
        }
    }

    // ─── Tool calls round-trip ────────────────────────────────────────────────

    [SkippableFact]
    public async Task Postgres_ToolCallsRoundTrip()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");

        var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var _ = store;

        const string key = "telegram:direct:pg-test-toolcalls";
        try
        {
            var session = new ConversationSession { SessionKey = key };
            session.Messages.Add(new ChatMessage("assistant", null,
                ToolCalls: [new ToolCall("call_1", "function",
                    new ToolCallFunction("get_time", "{}"))],
                ToolCallId: null));
            session.Messages.Add(new ChatMessage("tool", "2026-02-28T08:00:00Z",
                ToolCallId: "call_1"));

            await store.SaveAsync(session);
            var loaded = await store.LoadAsync(key);

            Assert.NotNull(loaded);
            Assert.Equal(2, loaded!.Messages.Count);

            var assistantMsg = loaded.Messages[0];
            Assert.NotNull(assistantMsg.ToolCalls);
            Assert.Single(assistantMsg.ToolCalls!);
            Assert.Equal("get_time", assistantMsg.ToolCalls![0].Function.Name);

            var toolMsg = loaded.Messages[1];
            Assert.Equal("tool",                       toolMsg.Role);
            Assert.Equal("2026-02-28T08:00:00Z",       toolMsg.Content);
            Assert.Equal("call_1",                     toolMsg.ToolCallId);
        }
        finally
        {
            await store.DeleteAsync(key);
        }
    }

    // ─── AppendMessageAsync creates session on-the-fly ────────────────────────

    [SkippableFact]
    public async Task Postgres_AppendCreatesSession()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");

        var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var _ = store;

        const string key = "telegram:direct:pg-test-append-new";
        try
        {
            Assert.False(await store.ExistsAsync(key));
            await store.AppendMessageAsync(key, new ChatMessage("user", "Hello from append"));
            Assert.True(await store.ExistsAsync(key));

            var loaded = await store.LoadAsync(key);
            Assert.Single(loaded!.Messages);
            Assert.Equal("Hello from append", loaded.Messages[0].Content);
        }
        finally
        {
            await store.DeleteAsync(key);
        }
    }

    // ─── Survives reconnect (data is actually in Postgres) ───────────────────

    [SkippableFact]
    public async Task Postgres_SurvivesReconnect()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");

        const string key = "telegram:direct:pg-test-reconnect";

        // Write with store 1
        {
            var store1 = await PostgresConversationStore.CreateAsync(ConnStr!);
            await using var _ = store1;
            var session = MakeSession(key, 3);
            await store1.SaveAsync(session);
        }

        // Read with store 2 (new connection)
        {
            var store2 = await PostgresConversationStore.CreateAsync(ConnStr!);
            await using var _ = store2;
            try
            {
                var loaded = await store2.LoadAsync(key);
                Assert.NotNull(loaded);
                Assert.Equal(3, loaded!.Messages.Count);
            }
            finally
            {
                await store2.DeleteAsync(key);
            }
        }
    }

    // ─── ProactiveScheduler — schedule + verify row ───────────────────────────

    [SkippableFact]
    public async Task ProactiveScheduler_ScheduleCreatesRow()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");

        // Ensure schema exists
        var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var _ = store;

        var scheduler = new ProactiveScheduler(ConnStr!, []);
        await using var __ = scheduler;

        var userId = "pg-test-proactive-user";
        await scheduler.ScheduleAsync("telegram", userId, "direct",
            new { text = "Test reminder" },
            DateTimeOffset.UtcNow.AddMinutes(60));

        // Verify the row exists via a fresh store (shares schema)
        using var conn = new Npgsql.NpgsqlConnection(ConnStr);
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM agentcli.proactive_events
            WHERE user_id = $1 AND channel = 'telegram' AND status = 'pending'
            """;
        cmd.Parameters.AddWithValue(userId);
        var count = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        Assert.True(count >= 1);

        // Cleanup
        cmd.CommandText = "DELETE FROM agentcli.proactive_events WHERE user_id = $1";
        await cmd.ExecuteNonQueryAsync();
    }
}
