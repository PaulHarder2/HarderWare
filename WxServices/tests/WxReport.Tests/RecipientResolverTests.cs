using System.Net.Http;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxReport.Svc;

using Xunit;

namespace WxReport.Tests;

/// <summary>
/// Pins the WX-127 locality guard in <see cref="RecipientResolver.EnsureResolvedAsync"/>:
/// locality members are never station-auto-resolved (their stations mirror the
/// locality verbatim per WX-125), while coordinate geocoding still applies to
/// members (coordinates are per-recipient facts that feed the centroid). The
/// guard paths must return before any station lookup — these tests pass a
/// default HttpClient and empty DbContextOptions precisely because the paths
/// under test must not touch either.
/// </summary>
public class RecipientResolverTests
{
    private static RecipientResolver CreateResolver() => new(
        new DbContextOptionsBuilder<WeatherDataContext>().Options,
        new HttpClient());

    [Fact]
    public async Task EnsureResolved_LocalityMemberWithStationsAndCoords_ReturnsTrueWithoutResolving()
    {
        var resolver = CreateResolver();
        var recipient = new RecipientConfig
        {
            Id = "alice",
            Email = "alice@example.com",
            LocalityId = 42,
            MetarIcao = "KDWH, KIAH",
            TafIcao = null,  // locality has no TAF station — acceptable for members
            Latitude = 30.07,
            Longitude = -95.55,
        };

        var ok = await resolver.EnsureResolvedAsync(recipient);

        Assert.True(ok);
        Assert.Equal("KDWH, KIAH", recipient.MetarIcao);
        Assert.Null(recipient.TafIcao);  // not coerced to "NONE"; not auto-resolved
    }

    [Fact]
    public async Task EnsureResolved_LocalityMemberWithoutStations_SkipsWithoutResolving()
    {
        // A member with no METAR means the locality has none yet. The resolver
        // must NOT find one independently (members of one locality could drift
        // onto different stations) — it reports and skips.
        var resolver = CreateResolver();
        var recipient = new RecipientConfig
        {
            Id = "bob",
            Email = "bob@example.com",
            LocalityId = 42,
            MetarIcao = null,
            TafIcao = null,
            Latitude = 30.07,
            Longitude = -95.55,
        };

        var ok = await resolver.EnsureResolvedAsync(recipient);

        Assert.False(ok);
        Assert.Null(recipient.MetarIcao);  // untouched — no competing write
        Assert.Null(recipient.TafIcao);
    }

    [Fact]
    public async Task EnsureResolved_LocalityMemberWithBothStations_FastPathIgnoresMissingCoords()
    {
        // Pre-existing fast path preserved: both stations cached → true without
        // geocoding, member or not. (Coordinates for members normally come from
        // WxManager's Address Lookup; the resolver geocodes only when stations
        // are incomplete.)
        var resolver = CreateResolver();
        var recipient = new RecipientConfig
        {
            Id = "carol",
            Email = "carol@example.com",
            LocalityId = 42,
            MetarIcao = "KDWH",
            TafIcao = "KIAH",
            Latitude = null,
            Longitude = null,
        };

        Assert.True(await resolver.EnsureResolvedAsync(recipient));
    }

    [Fact]
    public async Task EnsureResolved_NonMemberWithCachedStations_StillReturnsTrue()
    {
        // The pre-existing fast path for locality-less recipients is unchanged.
        var resolver = CreateResolver();
        var recipient = new RecipientConfig
        {
            Id = "dave",
            Email = "dave@example.com",
            LocalityId = null,
            MetarIcao = "KOKC",
            TafIcao = "KOKC",
        };

        Assert.True(await resolver.EnsureResolvedAsync(recipient));
    }
}