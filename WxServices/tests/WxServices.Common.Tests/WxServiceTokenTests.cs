// Anti-drift tests for the WX-290 canonical service token: the component that WRITES a service's LOG
// and the monitor that READS it must resolve IDENTICAL paths, so a writer/monitor filename divergence
// (the WX-106 blind-spot class) cannot recur. Also pins the stable per-service log filenames and that
// unknown names still normalize sensibly. Heartbeats moved to the per-worker WxWorkers registry in
// WX-68 (WxWorkersTests pins those). Path separators differ by OS (CI is Linux), so filename
// assertions use Path.GetFileName rather than a hardcoded separator.

using System.IO;

using WxServices.Common;

using Xunit;

namespace WxServices.Common.Tests;

public sealed class WxServiceTokenTests
{
    private static readonly WxPaths Paths = new(@"C:\HarderWare");

    [Theory]
    [InlineData(WxServiceToken.WxParser, "WxParser.Svc")]
    [InlineData(WxServiceToken.WxReport, "WxReport.Svc")]
    [InlineData(WxServiceToken.WxVis, "WxVis.Svc")]
    [InlineData(WxServiceToken.WxMonitor, "WxMonitor.Svc")]
    public void MonitorResolvesConfigNameToTheWriterToken(string writerToken, string configName)
    {
        // The seam: the monitor maps its WatchedServices config Name to the SAME token the writer uses
        // as a constant — the single source of truth.
        Assert.Equal(writerToken, WxServiceToken.FromConfigName(configName));
    }

    [Theory]
    [InlineData(WxServiceToken.WxParser, "WxParser.Svc")]
    [InlineData(WxServiceToken.WxReport, "WxReport.Svc")]
    [InlineData(WxServiceToken.WxVis, "WxVis.Svc")]
    [InlineData(WxServiceToken.WxMonitor, "WxMonitor.Svc")]
    public void WriterAndMonitorResolveIdenticalLogPaths(string writerToken, string configName)
    {
        // The service LOG is the surface this token now protects: the service's own log init and the
        // monitor's log-scan must resolve the SAME file. (Heartbeats moved to the per-worker WxWorkers
        // registry in WX-68 — their writer/reader agreement is pinned in WxWorkersTests.)
        var monitorToken = WxServiceToken.FromConfigName(configName);
        Assert.Equal(Paths.ServiceLogFile(writerToken), Paths.ServiceLogFile(monitorToken));
    }

    [Fact]
    public void ServiceLogFilenames_DeriveFromCanonicalToken()
    {
        // The canonical token yields the stable per-service log name (one -svc.log per process),
        // unchanged by WX-68 — only heartbeats went per-worker (see WxWorkersTests).
        Assert.Equal("wxparser-svc.log", Path.GetFileName(Paths.ServiceLogFile(WxServiceToken.WxParser)));
        Assert.Equal("wxreport-svc.log", Path.GetFileName(Paths.ServiceLogFile(WxServiceToken.WxReport)));
        Assert.Equal("wxvis-svc.log", Path.GetFileName(Paths.ServiceLogFile(WxServiceToken.WxVis)));
        Assert.Equal("wxmonitor-svc.log", Path.GetFileName(Paths.ServiceLogFile(WxServiceToken.WxMonitor)));
    }

    [Theory]
    [InlineData("WxParser.Svc", "wxparser")]
    [InlineData("wxparser.svc", "wxparser")]        // case-insensitive ".Svc" strip
    [InlineData("WxFuture.Svc", "wxfuture")]         // unregistered service still resolves sensibly
    [InlineData("SomethingElse", "somethingelse")]   // no ".Svc" suffix → normalized as-is
    public void FromConfigName_NormalizesUnknownNames(string configName, string expected)
    {
        Assert.Equal(expected, WxServiceToken.FromConfigName(configName));
    }
}