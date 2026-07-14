// Tests for WxPaths.StartupBanner (WX-63): the one-line startup banner every service logs at
// launch. The service name self-derives from the entry assembly (no per-service literal), and the
// git commit is deliberately absent — it is unavailable in a container build, and per-deploy
// provenance lives in deploy-history.log. These lock in the format and, critically, that the
// commit stays out.

using WxServices.Common;

using Xunit;

namespace WxServices.Common.Tests;

public sealed class StartupBannerTests
{
    [Fact]
    public void StartupBanner_EndsWithStarting() =>
        Assert.EndsWith(" starting.", WxPaths.StartupBanner());

    [Fact]
    public void StartupBanner_IncludesProductVersion() =>
        // Same entry assembly drives both, so the version string appears verbatim.
        Assert.Contains(WxPaths.ProductVersion, WxPaths.StartupBanner());

    [Fact]
    public void StartupBanner_OmitsGitCommit() =>
        // The whole point of WX-63's banner change: no "(commit ...)" fragment.
        Assert.DoesNotContain("commit", WxPaths.StartupBanner().ToLowerInvariant());

    [Fact]
    public void StartupBanner_LeadsWithAServiceName()
    {
        // A non-empty name self-derives from the entry assembly, so the banner never starts
        // with a space or leads with the version (there is a name token before it).
        var banner = WxPaths.StartupBanner();
        Assert.False(banner.StartsWith(' '));
        Assert.False(banner.StartsWith(WxPaths.ProductVersion));
    }
}