using MetarParser.Data;

using Microsoft.Extensions.Configuration;

using Xunit;

namespace MetarParser.Tests;

/// <summary>
/// Unit tests for the WX-28 retry configuration type.  The retry loop itself
/// is exercised against live SQL Server at startup, but the options class is
/// a pure value type and is tested here to pin down the schedule arithmetic
/// and IConfiguration binding behaviour.
/// </summary>
public class DatabaseStartupRetryOptionsTests
{
    // ── Defaults ─────────────────────────────────────────────────────────────

    [Fact]
    public void Default_HasTwelveAttempts()
    {
        Assert.Equal(12, DatabaseStartupRetryOptions.Default.MaxAttempts);
        Assert.Equal(DatabaseStartupRetryOptions.DefaultMaxAttempts,
                     DatabaseStartupRetryOptions.Default.MaxAttempts);
    }

    [Fact]
    public void Default_ScheduleBudgetsApproximatelyFiveMinutes()
    {
        var totalSeconds = 0;
        foreach (var s in DatabaseStartupRetryOptions.Default.DelaySecondsSchedule)
            totalSeconds += s;

        // 5 + 10 + 20 + 30 × 8 = 275 s ≈ 4:35 — within the "about 5 minutes"
        // budget the WX-28 retry loop promises.  Guardrail against someone
        // tuning the default in a way that silently blows the budget.
        Assert.InRange(totalSeconds, 240, 360);
    }

    // ── DelayAfterAttempt ────────────────────────────────────────────────────

    [Fact]
    public void DelayAfterAttempt_AfterFirstAttempt_ReturnsFirstScheduleElement()
    {
        var opts = DatabaseStartupRetryOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(5), opts.DelayAfterAttempt(1));
    }

    [Fact]
    public void DelayAfterAttempt_AfterSecondAttempt_ReturnsSecondScheduleElement()
    {
        var opts = DatabaseStartupRetryOptions.Default;
        Assert.Equal(TimeSpan.FromSeconds(10), opts.DelayAfterAttempt(2));
    }

    [Fact]
    public void DelayAfterAttempt_AtMaxAttempts_ReturnsZero()
    {
        // The final attempt is about to throw — no further delay is needed.
        var opts = DatabaseStartupRetryOptions.Default;
        Assert.Equal(TimeSpan.Zero, opts.DelayAfterAttempt(opts.MaxAttempts));
    }

    [Fact]
    public void DelayAfterAttempt_PastMaxAttempts_ReturnsZero()
    {
        var opts = DatabaseStartupRetryOptions.Default;
        Assert.Equal(TimeSpan.Zero, opts.DelayAfterAttempt(opts.MaxAttempts + 5));
    }

    [Fact]
    public void DelayAfterAttempt_WhenMaxAttemptsExceedsScheduleLength_ReusesFinalElement()
    {
        // MaxAttempts = 20, schedule length = 3.  Attempts 4–19 should all
        // reuse the last schedule element (7) so callers can raise MaxAttempts
        // without also lengthening the schedule array.
        var opts = new DatabaseStartupRetryOptions
        {
            MaxAttempts = 20,
            DelaySecondsSchedule = new[] { 1, 3, 7 },
        };

        Assert.Equal(TimeSpan.FromSeconds(1), opts.DelayAfterAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(3), opts.DelayAfterAttempt(2));
        Assert.Equal(TimeSpan.FromSeconds(7), opts.DelayAfterAttempt(3));
        Assert.Equal(TimeSpan.FromSeconds(7), opts.DelayAfterAttempt(10));
        Assert.Equal(TimeSpan.FromSeconds(7), opts.DelayAfterAttempt(19));
        Assert.Equal(TimeSpan.Zero, opts.DelayAfterAttempt(20));
    }

    [Fact]
    public void DelayAfterAttempt_EmptySchedule_FallsBackToDefaultSchedule()
    {
        var opts = new DatabaseStartupRetryOptions
        {
            MaxAttempts = 5,
            DelaySecondsSchedule = Array.Empty<int>(),
        };

        Assert.Equal(TimeSpan.FromSeconds(5), opts.DelayAfterAttempt(1));
        Assert.Equal(TimeSpan.FromSeconds(10), opts.DelayAfterAttempt(2));
    }

    [Fact]
    public void DelayAfterAttempt_NegativeScheduleElement_ClampsToZero()
    {
        var opts = new DatabaseStartupRetryOptions
        {
            MaxAttempts = 3,
            DelaySecondsSchedule = new[] { -5, -10 },
        };

        Assert.Equal(TimeSpan.Zero, opts.DelayAfterAttempt(1));
        Assert.Equal(TimeSpan.Zero, opts.DelayAfterAttempt(2));
    }

    // ── FromConfiguration ────────────────────────────────────────────────────

    [Fact]
    public void FromConfiguration_EmptyConfiguration_ReturnsDefaults()
    {
        var cfg = new ConfigurationBuilder().Build();
        var opts = DatabaseStartupRetryOptions.FromConfiguration(cfg);

        Assert.Equal(DatabaseStartupRetryOptions.DefaultMaxAttempts, opts.MaxAttempts);
        Assert.Equal(DatabaseStartupRetryOptions.DefaultDelaySecondsSchedule, opts.DelaySecondsSchedule);
    }

    [Fact]
    public void FromConfiguration_FullOverride_UsesConfiguredValues()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:StartupRetry:MaxAttempts"] = "6",
                ["Database:StartupRetry:DelaySecondsSchedule:0"] = "2",
                ["Database:StartupRetry:DelaySecondsSchedule:1"] = "4",
                ["Database:StartupRetry:DelaySecondsSchedule:2"] = "8",
            })
            .Build();

        var opts = DatabaseStartupRetryOptions.FromConfiguration(cfg);

        Assert.Equal(6, opts.MaxAttempts);
        Assert.Equal(new[] { 2, 4, 8 }, opts.DelaySecondsSchedule);
    }

    [Fact]
    public void FromConfiguration_OnlyMaxAttemptsSet_KeepsDefaultSchedule()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:StartupRetry:MaxAttempts"] = "20",
            })
            .Build();

        var opts = DatabaseStartupRetryOptions.FromConfiguration(cfg);

        Assert.Equal(20, opts.MaxAttempts);
        Assert.Equal(DatabaseStartupRetryOptions.DefaultDelaySecondsSchedule, opts.DelaySecondsSchedule);
    }

    [Fact]
    public void FromConfiguration_OnlyScheduleSet_KeepsDefaultMaxAttempts()
    {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:StartupRetry:DelaySecondsSchedule:0"] = "1",
                ["Database:StartupRetry:DelaySecondsSchedule:1"] = "2",
            })
            .Build();

        var opts = DatabaseStartupRetryOptions.FromConfiguration(cfg);

        Assert.Equal(DatabaseStartupRetryOptions.DefaultMaxAttempts, opts.MaxAttempts);
        Assert.Equal(new[] { 1, 2 }, opts.DelaySecondsSchedule);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-100")]
    public void FromConfiguration_InvalidMaxAttempts_FallsBackToDefault(string maxAttemptsValue)
    {
        // Zero or negative MaxAttempts makes no operational sense; the loader
        // must refuse to honour it rather than trap the service in an infinite
        // "did not become available after 0 attempts" loop.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:StartupRetry:MaxAttempts"] = maxAttemptsValue,
            })
            .Build();

        var opts = DatabaseStartupRetryOptions.FromConfiguration(cfg);

        Assert.Equal(DatabaseStartupRetryOptions.DefaultMaxAttempts, opts.MaxAttempts);
    }

    [Fact]
    public void FromConfiguration_SectionEmptyArray_FallsBackToDefaultSchedule()
    {
        // An operator who writes "DelaySecondsSchedule": [] in appsettings
        // should get the default schedule, not a zero-length array that would
        // cause DelayAfterAttempt to silently reach for an empty buffer.
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:StartupRetry:DelaySecondsSchedule"] = "",
            })
            .Build();

        var opts = DatabaseStartupRetryOptions.FromConfiguration(cfg);

        Assert.Equal(DatabaseStartupRetryOptions.DefaultDelaySecondsSchedule, opts.DelaySecondsSchedule);
    }
}