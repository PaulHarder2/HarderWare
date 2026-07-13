// Anti-drift tests for the WX-290 canonical service token: the component that WRITES a service's
// log/heartbeat and the monitor that READS them must resolve IDENTICAL paths, so the WX-106 heartbeat
// blind spot — a writer/monitor filename divergence — cannot recur. Also pins that the derived
// filenames are byte-identical to the pre-WX-290 names (no rename) and that unknown names still
// normalize sensibly. Path separators differ by OS (CI is Linux), so filename assertions use
// Path.GetFileName rather than a hardcoded separator.

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
    public void WriterAndMonitorResolveIdenticalHeartbeatAndLogPaths(string writerToken, string configName)
    {
        // WX-106 was exactly these two diverging (writer "wxparser-heartbeat.txt" vs monitor
        // "wxparser-svc-heartbeat.txt"). Assert they cannot: writer side uses the constant, monitor side
        // resolves from its config Name, and both land on the same file.
        var monitorToken = WxServiceToken.FromConfigName(configName);
        Assert.Equal(Paths.HeartbeatFile(writerToken), Paths.HeartbeatFile(monitorToken));
        Assert.Equal(Paths.ServiceLogFile(writerToken), Paths.ServiceLogFile(monitorToken));
    }

    [Fact]
    public void DerivedFilenames_MatchPreWx290Names_NoRename()
    {
        // The canonical token keeps every filename byte-identical to the pre-WX-290 names, so no file is
        // renamed (the scheduling-gate-avoidance premise) and no verify script or log target moves.
        Assert.Equal("wxparser-svc.log", Path.GetFileName(Paths.ServiceLogFile(WxServiceToken.WxParser)));
        Assert.Equal("wxparser-heartbeat.txt", Path.GetFileName(Paths.HeartbeatFile(WxServiceToken.WxParser)));
        Assert.Equal("wxreport-svc.log", Path.GetFileName(Paths.ServiceLogFile(WxServiceToken.WxReport)));
        Assert.Equal("wxreport-heartbeat.txt", Path.GetFileName(Paths.HeartbeatFile(WxServiceToken.WxReport)));
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