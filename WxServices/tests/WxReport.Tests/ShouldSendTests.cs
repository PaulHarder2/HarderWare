using MetarParser.Data;
using MetarParser.Data.Entities;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

// Decision-table tests for ReportWorker.ShouldSend — the unified WX-80 trigger,
// now keyed on the LOCALITY (WX-130): the decision is per locality, against the
// shared LocalityState and the locality's timezone + send hours. Exercises the
// priority order (first -> gap -> scheduled -> arrival pre-filter) and the two
// behaviours the review and acceptance criteria care about most: the post-gap
// arrival send (finding #1) and the once-per-slot scheduled cadence.

public class ShouldSendTests
{
    private static ReportConfig Cfg(int minGap = 60, string defHours = "7")
        => new() { MinGapMinutes = minGap, DefaultScheduledSendHours = defHours };

    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static IReadOnlyList<int> Hours(string? raw) => ScheduledSendHoursFormat.Parse(raw);

    private static string Hash(string metar)
        => new InputIdentity(metar, "none", "none").Serialize();

    [Fact]
    public void FirstEver_Sends_First()
    {
        var state = new LocalityState { LocalityId = 1 };
        var (send, reason, severity, allowSkip) = ReportWorker.ShouldSend(
            Utc, Hours(""), state, new InputIdentity("m", "none", "none"), Cfg(), new DateTime(2026, 5, 31, 9, 0, 0, DateTimeKind.Utc));

        Assert.True(send);
        Assert.Equal("first", reason);
        Assert.Equal(ChangeSeverity.None, severity);
        Assert.False(allowSkip);
    }

    [Fact]
    public void PostGap_AdvancedInput_SendsOnceGapClears()
    {
        // An input advanced 10 min after the last send, inside a 60-min gap.
        var sentAt = new DateTime(2026, 5, 31, 8, 0, 0, DateTimeKind.Utc);
        var state = new LocalityState
        {
            LocalityId = 1,
            LastUnscheduledSentUtc = sentAt,
            LastClaudeInputHash = Hash("m_old"),
        };
        var advanced = new InputIdentity("m_new", "none", "none");

        // Inside the gap: rate-limited, no send. Crucially the hash is NOT updated.
        var inGap = ReportWorker.ShouldSend(Utc, Hours(""), state, advanced, Cfg(minGap: 60), sentAt.AddMinutes(10));
        Assert.False(inGap.send);
        Assert.Equal("gap", inGap.reason);

        // After the gap clears: the advance still reads as changed, so it sends.
        var afterGap = ReportWorker.ShouldSend(Utc, Hours(""), state, advanced, Cfg(minGap: 60), sentAt.AddMinutes(61));
        Assert.True(afterGap.send);
        Assert.Equal("change", afterGap.reason);
        Assert.Equal(ChangeSeverity.Update, afterGap.severity);
        Assert.True(afterGap.allowSkip);
    }

    [Fact]
    public void NoInputChange_PreFilterSkips()
    {
        var sentAt = new DateTime(2026, 5, 31, 8, 0, 0, DateTimeKind.Utc);
        var identity = new InputIdentity("m", "none", "none");
        var state = new LocalityState
        {
            LocalityId = 1,
            LastUnscheduledSentUtc = sentAt,
            LastClaudeInputHash = identity.Serialize(),
        };

        var (send, reason, _, _) = ReportWorker.ShouldSend(
            Utc, Hours(""), state, identity, Cfg(minGap: 60), sentAt.AddMinutes(120));

        Assert.False(send);
        Assert.Equal("prefilter-skip", reason);
    }

    [Fact]
    public void ScheduledHourPassed_NotYetServed_Sends()
    {
        var state = new LocalityState
        {
            LocalityId = 1,
            LastUnscheduledSentUtc = new DateTime(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc), // yesterday: gap clear, not first
        };

        var (send, reason, _, allowSkip) = ReportWorker.ShouldSend(
            Utc, Hours("7"), state, new InputIdentity("m", "none", "none"), Cfg(), new DateTime(2026, 5, 31, 7, 30, 0, DateTimeKind.Utc));

        Assert.True(send);
        Assert.Equal("scheduled", reason);
        Assert.False(allowSkip); // scheduled sends are never skippable
    }

    [Fact]
    public void ScheduledSlotAlreadyServed_DoesNotResendInSameSlot()
    {
        // 07:00 slot already served at 07:05; same input. Later in the day the
        // scheduled trigger must not re-fire — it falls through to the pre-filter.
        var identity = new InputIdentity("m", "none", "none");
        var state = new LocalityState
        {
            LocalityId = 1,
            LastScheduledSentUtc = new DateTime(2026, 5, 31, 7, 5, 0, DateTimeKind.Utc),
            LastClaudeInputHash = identity.Serialize(),
        };

        var (send, reason, _, _) = ReportWorker.ShouldSend(
            Utc, Hours("7"), state, identity, Cfg(), new DateTime(2026, 5, 31, 9, 0, 0, DateTimeKind.Utc));

        Assert.False(send);
        Assert.NotEqual("scheduled", reason);
        Assert.Equal("prefilter-skip", reason);
    }

    [Fact]
    public void ScheduledOnlySend_ThenArrivalWithinGap_StillRateLimited()
    {
        // Regression: with only LastScheduledSentUtc set (LastUnscheduledSentUtc
        // null), the min-gap must still be enforced. A nullable `>` ternary would
        // pick the null side and skip the gap, letting an arrival fire minutes
        // after the scheduled send.
        var sentAt = new DateTime(2026, 5, 31, 7, 0, 0, DateTimeKind.Utc);
        var state = new LocalityState
        {
            LocalityId = 1,
            LastScheduledSentUtc = sentAt,   // scheduled send happened; never any unscheduled
            LastClaudeInputHash = Hash("m_old"),
        };

        // A fresh METAR 5 min later, well inside the 60-min gap.
        var (send, reason, _, _) = ReportWorker.ShouldSend(
            Utc, Hours(""), state, new InputIdentity("m_new", "none", "none"), Cfg(minGap: 60), sentAt.AddMinutes(5));

        Assert.False(send);
        Assert.Equal("gap", reason);
    }
}