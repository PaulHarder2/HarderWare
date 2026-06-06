using MetarParser.Data;
using MetarParser.Data.Entities;

using Xunit;

namespace MetarParser.Tests;

public class LocalityAssignmentTests
{
    private static Locality SpringLocality() => new()
    {
        Id = 42,
        Name = "Spring, TX",
        MetarIcao = "KDWH, KIAH",
        TafIcao = "KIAH",
        Timezone = "America/Chicago",
        ScheduledSendHours = "6, 12",
    };

    // ── Assign ───────────────────────────────────────────────────────────────

    [Fact]
    public void Assign_SetsLocalityIdAndMirrorsSharedFields()
    {
        var loc = SpringLocality();
        var r = new Recipient { RecipientId = "alice" };

        LocalityAssignment.Assign(r, loc);

        Assert.Equal(42, r.LocalityId);
        Assert.Equal("Spring, TX", r.LocalityName);
        Assert.Equal("KDWH, KIAH", r.MetarIcao);
        Assert.Equal("KIAH", r.TafIcao);
        Assert.Equal("America/Chicago", r.Timezone);
        Assert.Equal("6, 12", r.ScheduledSendHours);
    }

    [Fact]
    public void Assign_OverwritesExistingRecipientFields()
    {
        var loc = SpringLocality();
        var r = new Recipient
        {
            RecipientId = "bob",
            LocalityName = "The Woodlands",
            MetarIcao = "KOKC",
            TafIcao = "KOKC",
            Timezone = "America/New_York",
            ScheduledSendHours = "7",
        };

        LocalityAssignment.Assign(r, loc);

        Assert.Equal("Spring, TX", r.LocalityName);
        Assert.Equal("KDWH, KIAH", r.MetarIcao);
        Assert.Equal("KIAH", r.TafIcao);
        Assert.Equal("America/Chicago", r.Timezone);
        Assert.Equal("6, 12", r.ScheduledSendHours);
    }

    [Fact]
    public void Assign_MirrorsVerbatim_NullLocalityStationsAndHoursBlankTheRecipient()
    {
        // The locality is authoritative: a member always mirrors it, even when the
        // locality has no stations or schedule set. (Workflow keeps localities
        // stationed and scheduled; this pins the contract — a null schedule falls
        // back to the service default, same as a locality-less recipient's null.)
        // Timezone is required on both sides, so it mirrors the locality's default
        // ("UTC") rather than going null.
        var loc = new Locality { Id = 7, Name = "Empty", MetarIcao = null, TafIcao = null, ScheduledSendHours = null };
        var r = new Recipient { RecipientId = "carol", MetarIcao = "KXYZ", TafIcao = "KXYZ", Timezone = "America/Denver", ScheduledSendHours = "5, 17" };

        LocalityAssignment.Assign(r, loc);

        Assert.Equal(7, r.LocalityId);
        Assert.Equal("Empty", r.LocalityName);
        Assert.Null(r.MetarIcao);
        Assert.Null(r.TafIcao);
        Assert.Equal("UTC", r.Timezone);
        Assert.Null(r.ScheduledSendHours);
    }

    // ── SyncMembers ──────────────────────────────────────────────────────────

    [Fact]
    public void SyncMembers_ReappliesSharedFieldsToEveryMember()
    {
        var loc = SpringLocality();
        var members = new[]
        {
            new Recipient { RecipientId = "a", LocalityId = 42, LocalityName = "Spring, TX", MetarIcao = "OLD", TafIcao = "OLD", Timezone = "UTC", ScheduledSendHours = "6, 12" },
            new Recipient { RecipientId = "b", LocalityId = 42, LocalityName = "Spring, TX", MetarIcao = null, TafIcao = null, Timezone = "America/New_York", ScheduledSendHours = null },
        };

        // Locality name, stations, and schedule change, then sync.
        loc.Name = "Spring";
        loc.MetarIcao = "KDWH, KHOU";
        loc.TafIcao = "KHOU";
        loc.ScheduledSendHours = "7";
        LocalityAssignment.SyncMembers(loc, members);

        Assert.All(members, m => Assert.Equal("Spring", m.LocalityName));
        Assert.All(members, m => Assert.Equal("KDWH, KHOU", m.MetarIcao));
        Assert.All(members, m => Assert.Equal("KHOU", m.TafIcao));
        Assert.All(members, m => Assert.Equal("America/Chicago", m.Timezone));
        Assert.All(members, m => Assert.Equal("7", m.ScheduledSendHours));
    }
}