using System.Text;

namespace WxReport.Tests.Scenarios;

// Passthrough DelegatingHandler that tees each response body to a file. Used by
// the opt-in recorder (KdwhScenarioReplayRecorder) to capture the live Anthropic
// response for deterministic replay. Never used in CI.
internal sealed class RecordingHandler : DelegatingHandler
{
    private readonly string _outputPath;

    public RecordingHandler(string outputPath, HttpMessageHandler inner) : base(inner)
        => _outputPath = outputPath;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);

        // Buffer the body (reading consumes the stream), write it, then re-attach
        // a fresh content so the caller can still read the response.
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
        await File.WriteAllTextAsync(_outputPath, body, cancellationToken);

        // Dispose the original network-backed content before swapping in the
        // buffered copy, so the underlying response stream isn't leaked.
        response.Content.Dispose();
        response.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return response;
    }
}