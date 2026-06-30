using System.Net;
using System.Text;
using System.Text.Json;

using WxReport.Svc.TranslationQa;

using Xunit;

namespace WxReport.Tests;

/// <summary>WX-227 — GeminiJudge over a stubbed HTTP handler (no live API): it sends JSON-mode + the
/// key header + the request text, extracts the verdict from the Gemini envelope, and turns API/shape
/// failures into clear JudgeParseExceptions.</summary>
public class GeminiJudgeTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? ApiKeyHeader { get; private set; }
        public string? RequestBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            ApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var v) ? v.FirstOrDefault() : null;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static string Envelope(string verdictJson) =>
        JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text = verdictJson } } } } } });

    private static GeminiConfig Cfg() => new() { ApiKey = "test-key", Model = "gemini-2.0-flash" };

    [Fact]
    public async Task JudgeAsync_ParsesVerdict_AndSendsKeyHeaderAndJsonMode()
    {
        const string verdict = """
        { "language": "de",
          "selfReportedConfidence": { "level": "high", "note": "native" },
          "vocabularyVerdicts": [ { "token": "rain_light", "accurate": true, "natural": true, "comment": "gut", "suggestion": null } ] }
        """;
        var handler = new StubHandler(HttpStatusCode.OK, Envelope(verdict));
        using var http = new HttpClient(handler);

        var result = await new GeminiJudge(http, Cfg()).JudgeAsync("REQUEST-MARKDOWN-BODY", CancellationToken.None);

        Assert.Equal("de", result.Language);
        Assert.Single(result.VocabularyVerdicts);
        Assert.Equal("test-key", handler.ApiKeyHeader);            // key in header, not URL
        Assert.Contains("application/json", handler.RequestBody);  // responseMimeType (JSON mode)
        Assert.Contains("REQUEST-MARKDOWN-BODY", handler.RequestBody);
    }

    [Fact]
    public async Task JudgeAsync_NonSuccess_ThrowsWithStatus()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.TooManyRequests, "rate limited"));
        var ex = await Assert.ThrowsAsync<JudgeParseException>(() => new GeminiJudge(http, Cfg()).JudgeAsync("x", CancellationToken.None));
        Assert.Contains("429", ex.Message);
    }

    [Fact]
    public async Task JudgeAsync_NoCandidates_Throws()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, """{ "candidates": [] }"""));
        await Assert.ThrowsAsync<JudgeParseException>(() => new GeminiJudge(http, Cfg()).JudgeAsync("x", CancellationToken.None));
    }

    [Fact]
    public async Task JudgeAsync_MissingKey_Throws_WithoutCallingApi()
    {
        var handler = new StubHandler(HttpStatusCode.OK, Envelope("{}"));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<JudgeParseException>(() => new GeminiJudge(http, new GeminiConfig { ApiKey = null }).JudgeAsync("x", CancellationToken.None));
        Assert.Equal(0, handler.Calls); // failed fast before any HTTP call
    }

    [Fact]
    public async Task JudgeAsync_CanceledToken_Propagates()
    {
        // A cancel (and, by the same path, the HttpClient timeout) must surface as OperationCanceledException
        // — GeminiJudge only wraps transport errors, so cancellation propagates for the caller to handle.
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, Envelope("""{ "language": "de" }""")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new GeminiJudge(http, Cfg()).JudgeAsync("x", cts.Token));
    }

    [Fact]
    public async Task JudgeAsync_NetworkError_WrappedAsJudgeParseException()
    {
        // A transport failure (DNS/reset/TLS/refused) must become a clean JudgeParseException, not escape.
        using var http = new HttpClient(new ThrowingHandler(new HttpRequestException("connection refused")));
        await Assert.ThrowsAsync<JudgeParseException>(() => new GeminiJudge(http, Cfg()).JudgeAsync("x", CancellationToken.None));
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => throw ex;
    }
}