// WX-159: the OTel export-failure diagnostics — the throttle that turns an every-10s
// export failure into an onset + heartbeat (no flood), and the source/format helpers
// the listener uses to classify and render the SDK's internal EventSource diagnostics.

using WxServices.Common;

using Xunit;

namespace WxMonitor.Tests;

public class LogThrottleTests
{
    private static readonly DateTime T0 = new(2026, 6, 10, 6, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstCall_AlwaysLogs()
        => Assert.True(new LogThrottle(TimeSpan.FromMinutes(5)).ShouldLog(T0));

    [Fact]
    public void RepeatWithinWindow_Suppressed()
    {
        var t = new LogThrottle(TimeSpan.FromMinutes(5));
        Assert.True(t.ShouldLog(T0));
        Assert.False(t.ShouldLog(T0.AddSeconds(10)));    // the next export attempt, 10s later
        Assert.False(t.ShouldLog(T0.AddMinutes(4.9)));   // still inside the window
    }

    [Fact]
    public void AfterWindow_LogsAgain_ThenSuppressesAgain()
    {
        var t = new LogThrottle(TimeSpan.FromMinutes(5));
        Assert.True(t.ShouldLog(T0));
        Assert.True(t.ShouldLog(T0.AddMinutes(5)));          // heartbeat at the window edge
        Assert.False(t.ShouldLog(T0.AddMinutes(5).AddSeconds(10)));
    }

    [Fact]
    public void EightHourOutage_AtTenSecondCadence_LogsOncePerWindow_NotEveryAttempt()
    {
        var window = TimeSpan.FromMinutes(5);
        var t = new LogThrottle(window);
        var logged = 0;
        // Replay 8 hours of 10s export attempts against a down collector.
        for (var s = 0; s <= 8 * 3600; s += 10)
            if (t.ShouldLog(T0.AddSeconds(s)))
                logged++;
        // ~one per 5-min window over 8h ≈ 97, not the 2,881 raw attempts.
        Assert.InRange(logged, 96, 98);
    }
}

public class OtelExportDiagnosticsHelperTests
{
    [Theory]
    [InlineData("OpenTelemetry-Exporter-OpenTelemetryProtocol", true)]
    [InlineData("OpenTelemetry-Sdk", true)]
    [InlineData("System.Runtime", false)]
    [InlineData("Microsoft-Extensions-Logging", false)]
    public void IsOtelSource_MatchesOnlyOtelSdkSources(string name, bool expected)
        => Assert.Equal(expected, OtelExportDiagnostics.IsOtelSource(name));

    [Theory]
    [InlineData("OpenTelemetry-Exporter-OpenTelemetryProtocol", true)]
    [InlineData("OpenTelemetry-Sdk", false)]
    public void IsExporterSource_MatchesExporterSources(string name, bool expected)
        => Assert.Equal(expected, OtelExportDiagnostics.IsExporterSource(name));

    [Fact]
    public void FormatMessage_FillsPayloadIntoFormatString()
        => Assert.Equal("Failed to export 5 metrics.",
            OtelExportDiagnostics.FormatMessage("Failed to export {0} metrics.", new object?[] { 5 }, "ExportFailed"));

    [Fact]
    public void FormatMessage_NoPayload_ReturnsMessageVerbatim()
        => Assert.Equal("Connection refused.",
            OtelExportDiagnostics.FormatMessage("Connection refused.", null, "ExportFailed"));

    [Fact]
    public void FormatMessage_EmptyMessage_FallsBackToEventName()
        => Assert.Equal("ExportFailed",
            OtelExportDiagnostics.FormatMessage(null, new object?[] { 5 }, "ExportFailed"));

    [Fact]
    public void FormatMessage_MalformedFormatString_FallsBackToRawMessage()
        => Assert.Equal("Bad {0} {1} placeholder.",
            OtelExportDiagnostics.FormatMessage("Bad {0} {1} placeholder.", new object?[] { 5 }, "ExportFailed"));

    [Fact]
    public void FormatMessage_NothingUsable_ReturnsPlaceholder()
        => Assert.Equal("(unnamed event)",
            OtelExportDiagnostics.FormatMessage(null, null, null));
}