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
    };

    // ── Assign ───────────────────────────────────────────────────────────────

    [Fact]
    public void Assign_SetsLocalityIdAndMirrorsNameAndStations()
    {
        var loc = SpringLocality();
        var r = new Recipient { RecipientId = "alice" };

        LocalityAssignment.Assign(r, loc);

        Assert.Equal(42, r.LocalityId);
        Assert.Equal("Spring, TX", r.LocalityName);
        Assert.Equal("KDWH, KIAH", r.MetarIcao);
        Assert.Equal("KIAH", r.TafIcao);
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
        };

        LocalityAssignment.Assign(r, loc);

        Assert.Equal("Spring, TX", r.LocalityName);
        Assert.Equal("KDWH, KIAH", r.MetarIcao);
        Assert.Equal("KIAH", r.TafIcao);
    }

    [Fact]
    public void Assign_MirrorsVerbatim_NullLocalityStationsBlankTheRecipient()
    {
        // The locality is authoritative: a member always mirrors it, even when the
        // locality has no stations set. (Workflow keeps localities stationed; this
        // pins the contract.)
        var loc = new Locality { Id = 7, Name = "Empty", MetarIcao = null, TafIcao = null };
        var r = new Recipient { RecipientId = "carol", MetarIcao = "KXYZ", TafIcao = "KXYZ" };

        LocalityAssignment.Assign(r, loc);

        Assert.Equal(7, r.LocalityId);
        Assert.Equal("Empty", r.LocalityName);
        Assert.Null(r.MetarIcao);
        Assert.Null(r.TafIcao);
    }

    // ── SyncMembers ──────────────────────────────────────────────────────────

    [Fact]
    public void SyncMembers_ReappliesNameAndStationsToEveryMember()
    {
        var loc = SpringLocality();
        var members = new[]
        {
            new Recipient { RecipientId = "a", LocalityId = 42, LocalityName = "Spring, TX", MetarIcao = "OLD", TafIcao = "OLD" },
            new Recipient { RecipientId = "b", LocalityId = 42, LocalityName = "Spring, TX", MetarIcao = null, TafIcao = null },
        };

        // Locality name and stations change, then sync.
        loc.Name = "Spring";
        loc.MetarIcao = "KDWH, KHOU";
        loc.TafIcao = "KHOU";
        LocalityAssignment.SyncMembers(loc, members);

        Assert.All(members, m => Assert.Equal("Spring", m.LocalityName));
        Assert.All(members, m => Assert.Equal("KDWH, KHOU", m.MetarIcao));
        Assert.All(members, m => Assert.Equal("KHOU", m.TafIcao));
    }
}