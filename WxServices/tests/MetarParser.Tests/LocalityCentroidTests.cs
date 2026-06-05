using MetarParser.Data;
using MetarParser.Data.Entities;

using Xunit;

namespace MetarParser.Tests;

public class LocalityCentroidTests
{
    private static Recipient At(double lat, double lon) =>
        new() { RecipientId = "r", Latitude = lat, Longitude = lon };

    [Fact]
    public void Recompute_SetsMeanOfMemberCoordinates()
    {
        var loc = new Locality { Id = 1, Name = "Spring, TX" };

        LocalityCentroid.Recompute(loc, new[] { At(30.0, -95.0), At(30.4, -95.2) });

        Assert.Equal(30.2, loc.CentroidLat!.Value, 6);
        Assert.Equal(-95.1, loc.CentroidLon!.Value, 6);
    }

    [Fact]
    public void Recompute_ExcludesMembersWithoutCoordinates()
    {
        var loc = new Locality { Id = 1, Name = "Spring, TX" };
        var members = new[]
        {
            At(30.0, -95.0),
            new Recipient { RecipientId = "ungeocoded", Latitude = null, Longitude = null },
        };

        LocalityCentroid.Recompute(loc, members);

        Assert.Equal(30.0, loc.CentroidLat!.Value, 6);
        Assert.Equal(-95.0, loc.CentroidLon!.Value, 6);
    }

    [Fact]
    public void Recompute_NoMembersWithCoordinates_ClearsCentroid()
    {
        var loc = new Locality { Id = 1, Name = "Empty", CentroidLat = 30.0, CentroidLon = -95.0 };

        LocalityCentroid.Recompute(loc, new[] { new Recipient { RecipientId = "ungeocoded" } });

        Assert.Null(loc.CentroidLat);
        Assert.Null(loc.CentroidLon);
    }

    [Fact]
    public void Recompute_EmptyMembers_ClearsCentroid()
    {
        var loc = new Locality { Id = 1, Name = "Empty", CentroidLat = 30.0, CentroidLon = -95.0 };

        LocalityCentroid.Recompute(loc, Array.Empty<Recipient>());

        Assert.Null(loc.CentroidLat);
        Assert.Null(loc.CentroidLon);
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    [Fact]
    public void Recompute_NullLocality_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => LocalityCentroid.Recompute(null!, Array.Empty<Recipient>()));

    [Fact]
    public void Recompute_NullMembers_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => LocalityCentroid.Recompute(new Locality { Id = 1, Name = "x" }, null!));

    [Fact]
    public void Recompute_NullMemberElement_Throws() =>
        Assert.Throws<ArgumentNullException>(
            () => LocalityCentroid.Recompute(new Locality { Id = 1, Name = "x" }, new Recipient[] { null! }));
}