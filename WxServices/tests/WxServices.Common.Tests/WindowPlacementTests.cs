// Tests for WindowPlacement (WX-291): the pure placement-fitting logic that keeps a restored
// or default window on a real monitor and never larger than it (AC5/AC6), plus JSON round-trip
// and corrupt-file handling. The WPF glue (monitor lookup) is not tested here — it needs a live
// window; these cover the math and persistence that hold the actual guarantees.

using WxServices.Common;

using Xunit;

namespace WxServices.Common.Tests;

public sealed class WindowPlacementTests
{
    // A 1920x1080 work area at the origin — the common single-monitor case.
    private const double WaLeft = 0, WaTop = 0, WaWidth = 1920, WaHeight = 1080;

    // ── ClampToWorkArea: already-inside placements are preserved ──────────────

    [Fact]
    public void Clamp_PlacementFullyInside_IsUnchanged()
    {
        var p = new WindowPlacement { Left = 100, Top = 80, Width = 1000, Height = 700 };

        var fitted = p.ClampToWorkArea(WaLeft, WaTop, WaWidth, WaHeight);

        Assert.Equal(100, fitted.Left);
        Assert.Equal(80, fitted.Top);
        Assert.Equal(1000, fitted.Width);
        Assert.Equal(700, fitted.Height);
    }

    [Fact]
    public void Clamp_PreservesMaximizedFlag()
    {
        var p = new WindowPlacement { Left = 0, Top = 0, Width = 1000, Height = 700, Maximized = true };

        var fitted = p.ClampToWorkArea(WaLeft, WaTop, WaWidth, WaHeight);

        Assert.True(fitted.Maximized);
    }

    // ── AC6: never larger than the monitor it lands on ───────────────────────

    [Fact]
    public void Clamp_OversizedWindow_ShrinksToWorkArea()
    {
        // Saved from a 4K TV (3840x2160), now only a 1920x1080 monitor is present.
        var p = new WindowPlacement { Left = 0, Top = 0, Width = 3840, Height = 2160 };

        var fitted = p.ClampToWorkArea(WaLeft, WaTop, WaWidth, WaHeight);

        Assert.Equal(WaWidth, fitted.Width);
        Assert.Equal(WaHeight, fitted.Height);
    }

    [Fact]
    public void Clamp_DefaultOnSmallPanel_ShrinksToFit()
    {
        // The 1920x1080 default on a lone 1366x768 laptop panel must shrink, not overflow.
        var def = WindowPlacement.CenteredDefault(0, 0, 1366, 768);

        Assert.Equal(1366, def.Width);
        Assert.Equal(768, def.Height);
        // Centered-and-capped means it sits flush at the origin here (no room to center).
        Assert.Equal(0, def.Left);
        Assert.Equal(0, def.Top);
    }

    // ── AC5: always fully on-screen, even when saved off every monitor ───────

    [Fact]
    public void Clamp_OffScreenToTheRight_SlidesFullyInside()
    {
        // Left edge far beyond the right of the work area (e.g. the TV that used to be there is gone).
        var p = new WindowPlacement { Left = 5000, Top = 200, Width = 800, Height = 600 };

        var fitted = p.ClampToWorkArea(WaLeft, WaTop, WaWidth, WaHeight);

        Assert.Equal(WaWidth - 800, fitted.Left); // flush against the right edge
        Assert.Equal(200, fitted.Top);
        Assert.True(fitted.Left + fitted.Width <= WaWidth);
    }

    [Fact]
    public void Clamp_NegativeOrigin_SlidesToWorkAreaOrigin()
    {
        var p = new WindowPlacement { Left = -3000, Top = -3000, Width = 800, Height = 600 };

        var fitted = p.ClampToWorkArea(WaLeft, WaTop, WaWidth, WaHeight);

        Assert.Equal(WaLeft, fitted.Left);
        Assert.Equal(WaTop, fitted.Top);
    }

    [Fact]
    public void Clamp_HonorsNonZeroWorkAreaOrigin()
    {
        // A secondary monitor whose work area starts at (1920, 0); an off-screen rect must land on it.
        var p = new WindowPlacement { Left = 100, Top = 100, Width = 800, Height = 600 };

        var fitted = p.ClampToWorkArea(1920, 0, 1920, 1080);

        Assert.Equal(1920, fitted.Left); // slid right onto the secondary monitor
        Assert.Equal(100, fitted.Top);
        Assert.True(fitted.Left >= 1920 && fitted.Left + fitted.Width <= 1920 + 1920);
    }

    [Fact]
    public void Clamp_ResultAlwaysWithinWorkArea()
    {
        foreach (var p in new[]
                 {
                     new WindowPlacement { Left = -500, Top = -500, Width = 400, Height = 300 },
                     new WindowPlacement { Left = 9999, Top = 9999, Width = 5000, Height = 5000 },
                     new WindowPlacement { Left = 1900, Top = 1060, Width = 300, Height = 300 },
                 })
        {
            var f = p.ClampToWorkArea(WaLeft, WaTop, WaWidth, WaHeight);
            Assert.True(f.Left >= WaLeft);
            Assert.True(f.Top >= WaTop);
            Assert.True(f.Left + f.Width <= WaLeft + WaWidth);
            Assert.True(f.Top + f.Height <= WaTop + WaHeight);
        }
    }

    // ── CenteredDefault ──────────────────────────────────────────────────────

    [Fact]
    public void CenteredDefault_OnLargeMonitor_IsDefaultSizeAndCentered()
    {
        var def = WindowPlacement.CenteredDefault(0, 0, 2560, 1440);

        Assert.Equal(WindowPlacement.DefaultWidth, def.Width);
        Assert.Equal(WindowPlacement.DefaultHeight, def.Height);
        Assert.Equal((2560 - WindowPlacement.DefaultWidth) / 2, def.Left);
        Assert.Equal((1440 - WindowPlacement.DefaultHeight) / 2, def.Top);
        Assert.False(def.Maximized);
    }

    [Fact]
    public void CenteredDefault_OnExactly1080pMonitor_FitsWorkAreaMinusTaskbar()
    {
        // On a 1920x1080 monitor the work area is the full width but the height less the
        // taskbar (e.g. a 48px bottom taskbar → 1032). The default must fill the width and
        // shrink to the taskbar-excluded height — not overflow to a hard 1080.
        var def = WindowPlacement.CenteredDefault(0, 0, 1920, 1032);

        Assert.Equal(1920, def.Width);
        Assert.Equal(1032, def.Height);
        Assert.Equal(0, def.Left);
        Assert.Equal(0, def.Top);
    }

    // ── Load/Save round-trip and corrupt-file handling ───────────────────────

    [Fact]
    public void Save_ThenLoad_RoundTripsAllFields()
    {
        var appName = UniqueAppName();
        try
        {
            var p = new WindowPlacement { Left = 12, Top = 34, Width = 1600, Height = 900, Maximized = true };
            p.Save(appName);

            var loaded = WindowPlacement.Load(appName);

            Assert.NotNull(loaded);
            Assert.Equal(p, loaded); // record equality across all fields
        }
        finally
        {
            File.Delete(WindowPlacement.FilePath(appName));
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        Assert.Null(WindowPlacement.Load(UniqueAppName()));
    }

    [Fact]
    public void Load_CorruptJson_ReturnsNull()
    {
        var appName = UniqueAppName();
        try
        {
            Directory.CreateDirectory(WindowPlacement.StorageDir);
            File.WriteAllText(WindowPlacement.FilePath(appName), "{ not valid json ");

            Assert.Null(WindowPlacement.Load(appName));
        }
        finally
        {
            File.Delete(WindowPlacement.FilePath(appName));
        }
    }

    [Fact]
    public void Load_NonPositiveSize_ReturnsNull()
    {
        var appName = UniqueAppName();
        try
        {
            Directory.CreateDirectory(WindowPlacement.StorageDir);
            File.WriteAllText(WindowPlacement.FilePath(appName),
                "{\"Left\":0,\"Top\":0,\"Width\":0,\"Height\":0,\"Maximized\":false}");

            Assert.Null(WindowPlacement.Load(appName)); // zero-size is rejected → caller uses default
        }
        finally
        {
            File.Delete(WindowPlacement.FilePath(appName));
        }
    }

    // A per-test app name so parallel tests never collide on the shared %LOCALAPPDATA% file.
    private static string UniqueAppName() => $"wxtest-{Guid.NewGuid():N}";
}