// Tests for AddressGeocoder.LookupAsync's three-path dispatcher:
//   1. ///word.word.word  → What3Words
//   2. lat, lon           → direct parse, no HTTP call
//   3. anything else      → Nominatim
// Nominatim itself is not exercised here — the existing path is unchanged.

using System.Net;

using MetarParser.Data;

using Xunit;

namespace MetarParser.Tests;

public class AddressGeocoderRoutingTests
{
    // ── lat,lon direct-parse path ────────────────────────────────────────────

    [Fact]
    public async Task LatLon_BasicSignedDecimals_ParsesWithoutHttpCall()
    {
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("30.07, -95.55", http, w3wApiKey: null);

        Assert.NotNull(result);
        Assert.Equal(30.07, result!.Value.Latitude, precision: 5);
        Assert.Equal(-95.55, result.Value.Longitude, precision: 5);
        Assert.Equal("", result.Value.LocalityName);
    }

    [Fact]
    public async Task LatLon_WithExtraWhitespace_StillParses()
    {
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("   45 ,  -120   ", http, w3wApiKey: null);

        Assert.NotNull(result);
        Assert.Equal(45.0, result!.Value.Latitude);
        Assert.Equal(-120.0, result.Value.Longitude);
    }

    [Fact]
    public async Task LatLon_PositiveSignPrefix_Accepted()
    {
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("+30.5, +95.5", http, w3wApiKey: null);

        Assert.NotNull(result);
        Assert.Equal(30.5, result!.Value.Latitude);
        Assert.Equal(95.5, result.Value.Longitude);
    }

    [Fact]
    public async Task LatLon_LatOutOfRange_ReturnsNull()
    {
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("91, 0", http, w3wApiKey: null);
        Assert.Null(result);
    }

    [Fact]
    public async Task LatLon_LonOutOfRange_ReturnsNull()
    {
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("0, 181", http, w3wApiKey: null);
        Assert.Null(result);
    }

    // ── What3Words path ──────────────────────────────────────────────────────

    [Fact]
    public async Task W3W_WithEmptyApiKey_ReturnsNullAndDoesNotCallHttp()
    {
        // The W3W path returns null before any HTTP call when the key is missing.
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("///offer.loops.carb", http, w3wApiKey: "");
        Assert.Null(result);
    }

    [Fact]
    public async Task W3W_WithNullApiKey_ReturnsNullAndDoesNotCallHttp()
    {
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("///offer.loops.carb", http, w3wApiKey: null);
        Assert.Null(result);
    }

    // ── empty / whitespace input ─────────────────────────────────────────────

    [Fact]
    public async Task EmptyAddress_ReturnsNullAndDoesNotCallHttp()
    {
        var http = HttpClientThatFailsIfCalled();
        var result = await AddressGeocoder.LookupAsync("   ", http, w3wApiKey: null);
        Assert.Null(result);
    }

    // ── Nominatim fallback ───────────────────────────────────────────────────

    [Fact]
    public async Task PlainText_RoutesToNominatim()
    {
        // We don't care what Nominatim would return — only that the dispatcher
        // routes plain text to the Nominatim path (which DOES call HTTP).
        // A handler that returns 200 with an empty array makes the geocoder
        // return null but proves the request was sent.
        var nominatimWasCalled = false;
        var handler = new StubHandler((req) =>
        {
            nominatimWasCalled = true;
            Assert.Contains("nominatim.openstreetmap.org", req.RequestUri!.Host);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]"),
            };
        });
        using var http = new HttpClient(handler);

        var result = await AddressGeocoder.LookupAsync("123 Main St, Springfield", http, w3wApiKey: null);

        Assert.True(nominatimWasCalled, "Plain-text address should route to Nominatim.");
        Assert.Null(result);  // empty results array
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static HttpClient HttpClientThatFailsIfCalled()
        => new(new StubHandler(_ => throw new InvalidOperationException("HTTP call should not be made on this path.")));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_respond(request));
    }
}
