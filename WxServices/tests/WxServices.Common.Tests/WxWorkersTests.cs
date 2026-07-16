// Pins the WxWorkers registry (WX-68 Unit 2): the single source from which every worker derives its own
// heartbeat filename AND the monitor derives its watch-set, so a writer and the reader can never diverge
// on a name (the WX-106 blind-spot class, at the worker grain). If a worker is added or renamed, these
// fail until the expected map below is updated in lockstep — the guardrail that also keeps the compose
// healthchecks, which hard-code the same filenames, honest.

using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using WxServices.Common;

using Xunit;

namespace WxServices.Common.Tests;

public sealed class WxWorkersTests
{
    private static readonly WxPaths Paths = new(@"C:\HarderWare");

    [Fact]
    public void All_ContainsEveryRegisteredWorker_Once()
    {
        Assert.Equal(8, WxWorkers.All.Count);
        // No duplicate tokens — a dup would mean two workers share a heartbeat file (silent overwrite).
        Assert.Equal(WxWorkers.All.Count, WxWorkers.All.Select(w => w.Token).Distinct().Count());
    }

    [Theory]
    [InlineData("wxmonitor", "monitor", "wxmonitor-monitor-heartbeat.txt")]
    [InlineData("wxparser", "fetch", "wxparser-fetch-heartbeat.txt")]
    [InlineData("wxparser", "gfs", "wxparser-gfs-heartbeat.txt")]
    [InlineData("wxreport", "report", "wxreport-report-heartbeat.txt")]
    [InlineData("wxreport", "qa", "wxreport-qa-heartbeat.txt")]
    [InlineData("wxvis", "analysis", "wxvis-analysis-heartbeat.txt")]
    [InlineData("wxvis", "forecast", "wxvis-forecast-heartbeat.txt")]
    [InlineData("wxvis", "meteogram", "wxvis-meteogram-heartbeat.txt")]
    public void Registry_HasWorker_WithExpectedTokenAndFilename(string service, string worker, string expectedFile)
    {
        var w = WxWorkers.All.SingleOrDefault(x => x.Service == service && x.Worker == worker);
        Assert.NotNull(w);
        Assert.Equal($"{service}-{worker}", w!.Token);
        Assert.Equal(expectedFile, Path.GetFileName(Paths.HeartbeatFile(w)));
    }

    [Fact]
    public void HeartbeatFileOverload_MatchesTokenPrimitive()
    {
        // The WxWorker overload must resolve to the same file as passing its token to the primitive —
        // that equivalence is what lets writers and the monitor share one filename format.
        foreach (var w in WxWorkers.All)
            Assert.Equal(Paths.HeartbeatFile(w.Token), Paths.HeartbeatFile(w));
    }

    [Fact]
    public void OnlyMonitorBelongsToTheMonitorService()
    {
        // HeartbeatWatcher skips WxMonitor's own workers (a worker can't report its own death). Pin that
        // exactly one worker is in that service, so the skip covers wxmonitor and nothing else.
        var monitorWorkers = WxWorkers.All.Where(w => w.Service == WxServiceToken.WxMonitor).ToList();
        Assert.Single(monitorWorkers);
        Assert.Equal(WxWorkers.Monitor, monitorWorkers[0]);
    }

    [Fact]
    public void EveryWorkerServiceIsAKnownServiceToken()
    {
        var known = new[] { WxServiceToken.WxParser, WxServiceToken.WxReport, WxServiceToken.WxVis, WxServiceToken.WxMonitor };
        Assert.All(WxWorkers.All, w => Assert.Contains(w.Service, known));
    }

    [Fact]
    public void DefaultMaxAgeMinutes_ArePositive()
    {
        // A non-positive threshold would make the freshness check meaningless (always stale / never stale).
        Assert.All(WxWorkers.All, w => Assert.True(w.DefaultMaxAgeMinutes > 0));
    }

    [Fact]
    public void ComposeHealthchecks_ReferenceExactlyTheRegistryFilenames()
    {
        // The registry↔Compose recovery contract: services/docker-compose.yml hard-codes each heartbeat
        // filename in its healthcheck probes, and nothing else ties those strings to WxWorkers. Without
        // this test, renaming/adding a worker leaves container recovery watching a stale/absent name
        // while the rest of the suite stays green. Pin both directions.
        var compose = ReadComposeFile();

        // (1) Every registered worker's heartbeat filename appears in a compose healthcheck.
        foreach (var w in WxWorkers.All)
        {
            var file = $"{w.Token}-heartbeat.txt";
            Assert.True(compose.Contains(file, StringComparison.Ordinal),
                $"docker-compose.yml has no healthcheck referencing '{file}' (worker {w.Token}).");
        }

        // (2) No orphan: every *-heartbeat.txt named in compose is a registered worker (catches a
        // filename left behind after a rename).
        var known = WxWorkers.All.Select(w => $"{w.Token}-heartbeat.txt").ToHashSet(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(compose, @"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*-heartbeat\.txt"))
            Assert.True(known.Contains(m.Value),
                $"docker-compose.yml references heartbeat file '{m.Value}', which is not in WxWorkers.All.");
    }

    /// <summary>
    /// Reads the repo's <c>services/docker-compose.yml</c>. Located via <see cref="CallerFilePathAttribute"/>
    /// (the source path baked at compile time) rather than <c>AppContext.BaseDirectory</c>, because the
    /// test binary runs from an out-of-tree build cache from which the repo root can't be walked to.
    /// </summary>
    private static string ReadComposeFile([CallerFilePath] string thisFile = "")
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(thisFile)!); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "services", "docker-compose.yml");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException($"Could not locate services/docker-compose.yml from source path '{thisFile}'.");
    }
}