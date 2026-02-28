using AgentCli;
using Xunit;

namespace AgentCli.Tests;

/// <summary>
/// Unit tests for token tracking: TokenUsage, InMemoryTokenLedger, ConfigurableTokenPolicy.
/// No external deps — always run.
/// Postgres ledger integration tests gated behind AGENTCLI_TEST_POSTGRES.
/// </summary>
public class TokenTrackingTests
{
    // ─── TokenUsage ───────────────────────────────────────────────────────────

    [Fact]
    public void TokenUsage_Addition_SumsAllFields()
    {
        var a = new TokenUsage(100, 50, 150);
        var b = new TokenUsage(200, 75, 275);
        var sum = a + b;
        Assert.Equal(300, sum.PromptTokens);
        Assert.Equal(125, sum.CompletionTokens);
        Assert.Equal(425, sum.TotalTokens);
    }

    [Fact]
    public void TokenUsage_Zero_IsAllZeros()
    {
        var z = TokenUsage.Zero;
        Assert.Equal(0, z.PromptTokens);
        Assert.Equal(0, z.CompletionTokens);
        Assert.Equal(0, z.TotalTokens);
    }

    [Fact]
    public void TokenUsage_AddZero_ReturnsOriginal()
    {
        var a = new TokenUsage(10, 20, 30);
        Assert.Equal(a, a + TokenUsage.Zero);
    }

    // ─── InMemoryTokenLedger ──────────────────────────────────────────────────

    [Fact]
    public async Task Ledger_RecordAndGetTotal()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(100, 50, 150));
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(200, 80, 280));

        var total = await ledger.GetTotalAsync("u1", "telegram");
        Assert.Equal(300, total.PromptTokens);
        Assert.Equal(130, total.CompletionTokens);
        Assert.Equal(430, total.TotalTokens);
    }

    [Fact]
    public async Task Ledger_TodayOnlyCountsToday()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(100, 50, 150));

        var today = await ledger.GetTodayAsync("u1", "telegram");
        Assert.Equal(150, today.TotalTokens);
    }

    [Fact]
    public async Task Ledger_GetTotal_ZeroForUnknownUser()
    {
        var ledger = new InMemoryTokenLedger();
        var total = await ledger.GetTotalAsync("nobody", "telegram");
        Assert.Equal(TokenUsage.Zero, total);
    }

    [Fact]
    public async Task Ledger_ChannelIsolation()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram",  new TokenUsage(100, 50, 150));
        await ledger.RecordAsync("u1", "whatsapp",  new TokenUsage(200, 80, 280));

        var tg = await ledger.GetTotalAsync("u1", "telegram");
        var wa = await ledger.GetTotalAsync("u1", "whatsapp");

        Assert.Equal(150, tg.TotalTokens);
        Assert.Equal(280, wa.TotalTokens);
    }

    [Fact]
    public async Task Ledger_UserIsolation()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(100, 50, 150));
        await ledger.RecordAsync("u2", "telegram", new TokenUsage(500, 200, 700));

        var u1 = await ledger.GetTotalAsync("u1", "telegram");
        var u2 = await ledger.GetTotalAsync("u2", "telegram");

        Assert.Equal(150, u1.TotalTokens);
        Assert.Equal(700, u2.TotalTokens);
    }

    [Fact]
    public async Task Ledger_GetAllUsers_ReturnsAllGroups()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(100, 50, 150));
        await ledger.RecordAsync("u2", "telegram", new TokenUsage(200, 80, 280));
        await ledger.RecordAsync("u1", "whatsapp", new TokenUsage(50,  20, 70));

        var all = await ledger.GetAllUsersAsync();
        Assert.Equal(3, all.Count);
        Assert.Contains(all, s => s.UserId == "u1" && s.Channel == "telegram");
        Assert.Contains(all, s => s.UserId == "u2" && s.Channel == "telegram");
        Assert.Contains(all, s => s.UserId == "u1" && s.Channel == "whatsapp");
    }

    [Fact]
    public async Task Ledger_GetWindow_FiltersCorrectly()
    {
        var ledger = new InMemoryTokenLedger();
        // Recent entry — should be included
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(100, 50, 150));

        var window = await ledger.GetWindowAsync("u1", "telegram", TimeSpan.FromHours(1));
        Assert.Equal(150, window.TotalTokens);

        var tinyWindow = await ledger.GetWindowAsync("u1", "telegram", TimeSpan.FromMilliseconds(0));
        // 0ms window — may or may not include the entry depending on timing, but shouldn't crash
        Assert.True(tinyWindow.TotalTokens >= 0);
    }

    // ─── ConfigurableTokenPolicy ──────────────────────────────────────────────

    [Fact]
    public async Task Policy_Unlimited_AlwaysAllows()
    {
        var ledger = new InMemoryTokenLedger();
        // Record lots of tokens
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(100_000, 100_000, 200_000));

        var policy = new ConfigurableTokenPolicy();  // defaults = unlimited
        var result = await policy.CheckAsync("u1", "telegram", ledger);
        Assert.Equal(TokenPolicyOutcome.Allow, result.Outcome);
    }

    [Fact]
    public async Task Policy_DailyLimit_DeniesWhenExceeded()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(800, 200, 1000));

        var policy = new ConfigurableTokenPolicy(
            new TokenLimits { DailyTotalTokens = 500 });

        var result = await policy.CheckAsync("u1", "telegram", ledger);
        Assert.Equal(TokenPolicyOutcome.Deny, result.Outcome);
        Assert.NotNull(result.Message);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Policy_DailyLimit_AllowsWhenUnder()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(100, 50, 150));

        var policy = new ConfigurableTokenPolicy(
            new TokenLimits { DailyTotalTokens = 500 });

        var result = await policy.CheckAsync("u1", "telegram", ledger);
        Assert.Equal(TokenPolicyOutcome.Allow, result.Outcome);
        Assert.True(result.IsAllowed);
    }

    [Fact]
    public async Task Policy_WarnThreshold_WarnsWhenExceeded()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(600, 200, 800));

        var policy = new ConfigurableTokenPolicy(
            new TokenLimits { DailyTotalTokens = 2000, DailyWarnAt = 500 });

        var result = await policy.CheckAsync("u1", "telegram", ledger);
        Assert.Equal(TokenPolicyOutcome.Warn, result.Outcome);
        Assert.True(result.IsAllowed);   // warn = still allowed
        Assert.NotNull(result.Message);
    }

    [Fact]
    public async Task Policy_DenyTakesPriorityOverWarn()
    {
        var ledger = new InMemoryTokenLedger();
        // Exceeds both warn and daily limit
        await ledger.RecordAsync("u1", "telegram", new TokenUsage(1500, 500, 2000));

        var policy = new ConfigurableTokenPolicy(
            new TokenLimits { DailyTotalTokens = 1000, DailyWarnAt = 500 });

        var result = await policy.CheckAsync("u1", "telegram", ledger);
        Assert.Equal(TokenPolicyOutcome.Deny, result.Outcome);
        Assert.False(result.IsAllowed);
    }

    [Fact]
    public async Task Policy_PerUserOverride_TakesPriority()
    {
        var ledger = new InMemoryTokenLedger();
        await ledger.RecordAsync("premium", "telegram", new TokenUsage(5000, 2000, 7000));

        var policy = new ConfigurableTokenPolicy(
            defaults: new TokenLimits { DailyTotalTokens = 1000 },
            perUser: new Dictionary<string, TokenLimits>
            {
                ["premium"] = new TokenLimits { DailyTotalTokens = 50_000 }
            });

        // Premium user not denied despite exceeding default limit
        var result = await policy.CheckAsync("premium", "telegram", ledger);
        Assert.Equal(TokenPolicyOutcome.Allow, result.Outcome);
    }

    [Fact]
    public async Task Policy_NoUsageYet_Allows()
    {
        var ledger = new InMemoryTokenLedger();
        var policy = new ConfigurableTokenPolicy(
            new TokenLimits { DailyTotalTokens = 1000 });

        var result = await policy.CheckAsync("new-user", "telegram", ledger);
        Assert.Equal(TokenPolicyOutcome.Allow, result.Outcome);
    }

    [Fact]
    public void TokenPolicyResult_AllowFactory()
    {
        var r = TokenPolicyResult.Allow();
        Assert.True(r.IsAllowed);
        Assert.Equal(TokenPolicyOutcome.Allow, r.Outcome);
        Assert.Null(r.Message);
    }

    [Fact]
    public void TokenPolicyResult_DenyFactory()
    {
        var r = TokenPolicyResult.Deny("over limit");
        Assert.False(r.IsAllowed);
        Assert.Equal("over limit", r.Message);
    }

    [Fact]
    public void TokenPolicyResult_WarnFactory()
    {
        var r = TokenPolicyResult.Warn("getting close");
        Assert.True(r.IsAllowed);
        Assert.Equal(TokenPolicyOutcome.Warn, r.Outcome);
    }

    // ─── Postgres integration ─────────────────────────────────────────────────

    [SkippableFact]
    public async Task PostgresLedger_RecordAndQuery()
    {
        var connStr = Environment.GetEnvironmentVariable("AGENTCLI_TEST_POSTGRES");
        Skip.If(connStr is null, "AGENTCLI_TEST_POSTGRES not set");

        await using var store = await PostgresConversationStore.CreateAsync(connStr!);
        await using var ledger = new PostgresTokenLedger(connStr!);

        const string userId  = "token-test-user";
        const string channel = "telegram";

        try
        {
            await ledger.RecordAsync(userId, channel, new TokenUsage(100, 50, 150), "gpt-4o", "openai");
            await ledger.RecordAsync(userId, channel, new TokenUsage(200, 80, 280), "gpt-4o", "openai");

            var total = await ledger.GetTotalAsync(userId, channel);
            Assert.Equal(300, total.PromptTokens);
            Assert.Equal(430, total.TotalTokens);

            var today = await ledger.GetTodayAsync(userId, channel);
            Assert.Equal(430, today.TotalTokens);

            var window = await ledger.GetWindowAsync(userId, channel, TimeSpan.FromHours(1));
            Assert.Equal(430, window.TotalTokens);

            var all = await ledger.GetAllUsersAsync();
            Assert.Contains(all, s => s.UserId == userId && s.Channel == channel);
        }
        finally
        {
            // Cleanup
            await using var conn = new Npgsql.NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM agentcli.token_usage WHERE user_id = $1";
            cmd.Parameters.AddWithValue(userId);
            await cmd.ExecuteNonQueryAsync();
        }
    }
}
