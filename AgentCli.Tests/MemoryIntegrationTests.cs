using AgentCli;
using Xunit;

namespace AgentCli.Tests;

/// <summary>
/// Integration tests for ISoulProvider, IAgentMemory, IUserMemory.
/// Requires AGENTCLI_TEST_POSTGRES env var — skips cleanly without it.
///
/// Run with:
///   AGENTCLI_TEST_POSTGRES="Host=86.48.30.121;Port=5432;Database=agentcli_test;Username=postgres;Password=Xa1Hf4M3EnAKG8g" dotnet test --filter "Memory"
/// </summary>
public class MemoryIntegrationTests
{
    private static readonly string? ConnStr =
        Environment.GetEnvironmentVariable("AGENTCLI_TEST_POSTGRES");

    // ─── FileSoulProvider unit tests (no Postgres needed) ─────────────────────

    [Fact]
    public async Task FileSoul_ReturnsNull_WhenNoSoulFile()
    {
        var dir      = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var provider = new FileMemoryProvider(dir);
        var soul     = new FileSoulProvider(provider);

        var result = await soul.GetAsync("default");
        Assert.Null(result);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task FileSoul_ReturnsPrompt_WhenSoulFileExists()
    {
        var dir      = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var provider = new FileMemoryProvider(dir);
        await provider.WriteAsync(MemorySystem.KeySoul, "# I am a test agent.");

        var soul   = new FileSoulProvider(provider);
        var result = await soul.GetAsync("default");

        Assert.NotNull(result);
        Assert.Equal("# I am a test agent.", result!.Prompt);
        Assert.Equal("default",              result.AgentType);
        Assert.Empty(result.StartupReads);   // no WORKFLOW_AUTO.md yet

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task FileSoul_ParsesStartupReads_FromWorkflowAuto()
    {
        var dir      = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var provider = new FileMemoryProvider(dir);
        await provider.WriteAsync(MemorySystem.KeySoul, "# Soul");
        await provider.WriteAsync(MemorySystem.KeyWorkflow, """
            # WORKFLOW_AUTO.md
            - MEMORY.md
            - memory/YYYY-MM-DD.md
            """);

        var soul   = new FileSoulProvider(provider);
        var result = await soul.GetAsync("default");

        Assert.NotNull(result);
        Assert.Equal(2,                      result!.StartupReads.Length);
        Assert.Contains("MEMORY.md",         result.StartupReads);
        Assert.Contains("memory/YYYY-MM-DD.md", result.StartupReads);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task MemorySystem_GetStartupReads_DelegatesTo_FileSoulProvider()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var ms  = MemorySystem.CreateFile(dir);

        await ms.WriteSoulAsync("# My Soul");
        await ms.WriteWorkflowAutoAsync("- MEMORY.md\n- memory/YYYY-MM-DD.md\n");

        var reads = await ms.GetStartupReadsAsync();
        Assert.Equal(2, reads.Length);
        Assert.Contains("MEMORY.md", reads);
        Assert.Contains("memory/YYYY-MM-DD.md", reads);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task MemorySystem_GetStartupReads_FallsBack_WhenNoSoulProvider()
    {
        // MemorySystem without soul provider — should still parse WORKFLOW_AUTO.md
        var dir      = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var provider = new FileMemoryProvider(dir);
        var ms       = new MemorySystem(provider, soulProvider: null);

        await ms.WriteWorkflowAutoAsync("- MEMORY.md\n- memory/YYYY-MM-DD.md\n");

        var reads = await ms.GetStartupReadsAsync();
        Assert.Equal(2, reads.Length);
        Assert.Contains("MEMORY.md", reads);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task FileSoul_Version_Changes_AfterSoulFileUpdate()
    {
        var dir      = Path.Combine(Path.GetTempPath(), $"agentcli-test-{Guid.NewGuid():N}");
        var provider = new FileMemoryProvider(dir);
        await provider.WriteAsync(MemorySystem.KeySoul, "# Soul v1");

        var soul = new FileSoulProvider(provider);
        var v1   = await soul.GetVersionAsync("default");

        // Small delay to ensure mtime differs
        await Task.Delay(10);
        await provider.WriteAsync(MemorySystem.KeySoul, "# Soul v2");

        var v2 = await soul.GetVersionAsync("default");
        Assert.NotEqual(v1, v2);

        Directory.Delete(dir, recursive: true);
    }

    // ─── PostgresSoulProvider integration tests ────────────────────────────────

    [SkippableFact]
    public async Task Soul_GetAsync_ReturnsNull_WhenNotSeeded()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");

        // Ensure schema exists
        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);

        await using var soul = new PostgresSoulProvider(ConnStr!);
        var result = await soul.GetAsync("nonexistent-agent");
        Assert.Null(result);
    }

    [SkippableFact]
    public async Task Soul_GetAsync_ReturnsSeededSoul_WithStartupReads()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");

        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);

        // Seed a test soul directly
        await using var conn = new Npgsql.NpgsqlConnection(ConnStr);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO agentcli.souls
                (agent_type, name, prompt, startup_reads, version, updated_by)
            VALUES
                ('test-soul', 'Test Agent', 'I am a test agent.',
                 ARRAY['MEMORY.md','memory/YYYY-MM-DD.md'], 1, 'test')
            ON CONFLICT (agent_type) DO UPDATE
            SET name = EXCLUDED.name, prompt = EXCLUDED.prompt,
                startup_reads = EXCLUDED.startup_reads, version = EXCLUDED.version
            """;
        await cmd.ExecuteNonQueryAsync();

        await using var soul = new PostgresSoulProvider(ConnStr!);
        var result = await soul.GetAsync("test-soul");

        Assert.NotNull(result);
        Assert.Equal("test-soul",           result!.AgentType);
        Assert.Equal("Test Agent",          result.Name);
        Assert.Equal("I am a test agent.",  result.Prompt);
        Assert.Equal(1,                     result.Version);
        Assert.Equal(2,                     result.StartupReads.Length);
        Assert.Contains("MEMORY.md",        result.StartupReads);

        // Cleanup
        cmd.CommandText = "DELETE FROM agentcli.souls WHERE agent_type = 'test-soul'";
        await cmd.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task Soul_GetVersionAsync_Returns0_WhenMissing()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");
        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var soul  = new PostgresSoulProvider(ConnStr!);
        Assert.Equal(0, await soul.GetVersionAsync("no-such-agent"));
    }

    // ─── Agent Memory ─────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task AgentMemory_WriteAndSearch()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");
        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);

        await using var conn = new Npgsql.NpgsqlConnection(ConnStr);
        await conn.OpenAsync();

        // Seed via direct INSERT (admin-only write)
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO agentcli.agent_memory (key, content, tags)
                VALUES
                  ('test:dotnet10', '.NET 10 was released in November 2026.', ARRAY['tech','dotnet','news']),
                  ('test:azure',    'Azure had a global outage on 2026-02-01.',  ARRAY['tech','azure','news'])
                ON CONFLICT (key) DO UPDATE
                SET content = EXCLUDED.content, tags = EXCLUDED.tags, updated_at = NOW()
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var memory = new PostgresAgentMemory(ConnStr!);

        // Search by keyword
        var results = await memory.SearchAsync("dotnet");
        Assert.Contains(results, e => e.Key == "test:dotnet10");

        // Search by content
        var results2 = await memory.SearchAsync("azure outage");
        Assert.Contains(results2, e => e.Key == "test:azure");

        // Direct key lookup
        var entry = await memory.GetAsync("test:dotnet10");
        Assert.NotNull(entry);
        Assert.Contains("dotnet", entry!.Tags);

        // List by tag
        var byTag = await memory.ListAsync("news");
        Assert.True(byTag.Count >= 2);

        // Cleanup
        await using var del = conn.CreateCommand();
        del.CommandText = "DELETE FROM agentcli.agent_memory WHERE key LIKE 'test:%'";
        await del.ExecuteNonQueryAsync();
    }

    [SkippableFact]
    public async Task AgentMemory_Search_ReturnsEmpty_WhenNoMatch()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");
        await using var store  = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var memory = new PostgresAgentMemory(ConnStr!);
        var results = await memory.SearchAsync("xyzzy-no-such-thing-12345");
        Assert.Empty(results);
    }

    // ─── User Memory ──────────────────────────────────────────────────────────

    [SkippableFact]
    public async Task UserMemory_WriteReadDelete()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");
        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var mem   = new PostgresUserMemory(ConnStr!);

        const string userId  = "test-user-memory-u1";
        const string channel = "telegram";

        try
        {
            // Write
            await mem.WriteAsync(userId, channel, "timezone", "America/Phoenix");
            await mem.WriteAsync(userId, channel, "language", "en");
            await mem.WriteAsync(userId, channel, "prefers",  "bullet lists");

            // Read one
            var tz = await mem.GetAsync(userId, channel, "timezone");
            Assert.Equal("America/Phoenix", tz);

            // Read all
            var all = await mem.GetAllAsync(userId, channel);
            Assert.Equal(3, all.Count);
            Assert.Contains(all, e => e.Key == "timezone" && e.Value == "America/Phoenix");
            Assert.Contains(all, e => e.Key == "language" && e.Value == "en");

            // Overwrite
            await mem.WriteAsync(userId, channel, "timezone", "UTC");
            var updated = await mem.GetAsync(userId, channel, "timezone");
            Assert.Equal("UTC", updated);

            // Delete one
            await mem.DeleteAsync(userId, channel, "language");
            var all2 = await mem.GetAllAsync(userId, channel);
            Assert.Equal(2, all2.Count);
            Assert.DoesNotContain(all2, e => e.Key == "language");

            // Null for missing key
            var missing = await mem.GetAsync(userId, channel, "nonexistent");
            Assert.Null(missing);
        }
        finally
        {
            await mem.DeleteAllAsync(userId, channel);
        }
    }

    [SkippableFact]
    public async Task UserMemory_HardIsolation_BetweenUsers()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");
        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var mem   = new PostgresUserMemory(ConnStr!);

        const string userA = "isolation-test-user-a";
        const string userB = "isolation-test-user-b";
        const string ch    = "telegram";

        try
        {
            await mem.WriteAsync(userA, ch, "secret", "user-a-secret");
            await mem.WriteAsync(userB, ch, "secret", "user-b-secret");

            var aSecret = await mem.GetAsync(userA, ch, "secret");
            var bSecret = await mem.GetAsync(userB, ch, "secret");

            Assert.Equal("user-a-secret", aSecret);
            Assert.Equal("user-b-secret", bSecret);

            var aAll = await mem.GetAllAsync(userA, ch);
            Assert.All(aAll, e => Assert.Equal(userA, e.UserId));
        }
        finally
        {
            await mem.DeleteAllAsync(userA, ch);
            await mem.DeleteAllAsync(userB, ch);
        }
    }

    [SkippableFact]
    public async Task UserMemory_DeleteAll_RemovesAllFacts()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");
        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var mem   = new PostgresUserMemory(ConnStr!);

        const string userId  = "test-delete-all-user";
        const string channel = "whatsapp";

        await mem.WriteAsync(userId, channel, "k1", "v1");
        await mem.WriteAsync(userId, channel, "k2", "v2");
        await mem.WriteAsync(userId, channel, "k3", "v3");

        await mem.DeleteAllAsync(userId, channel);

        var all = await mem.GetAllAsync(userId, channel);
        Assert.Empty(all);
    }

    [SkippableFact]
    public async Task UserMemory_SameKey_DifferentChannels_AreIndependent()
    {
        Skip.If(ConnStr is null, "AGENTCLI_TEST_POSTGRES not set");
        await using var store = await PostgresConversationStore.CreateAsync(ConnStr!);
        await using var mem   = new PostgresUserMemory(ConnStr!);

        const string userId   = "test-multichannel-user";
        const string telegram = "telegram";
        const string whatsapp = "whatsapp";

        try
        {
            await mem.WriteAsync(userId, telegram, "display_name", "Joche on Telegram");
            await mem.WriteAsync(userId, whatsapp, "display_name", "Joche on WhatsApp");

            var tg = await mem.GetAsync(userId, telegram, "display_name");
            var wa = await mem.GetAsync(userId, whatsapp, "display_name");

            Assert.Equal("Joche on Telegram",  tg);
            Assert.Equal("Joche on WhatsApp",  wa);
        }
        finally
        {
            await mem.DeleteAllAsync(userId, telegram);
            await mem.DeleteAllAsync(userId, whatsapp);
        }
    }
}


