// Unit tests for HeartbeatChecker.
// Verifies age calculation, missing-file handling, and invalid-content handling.

using WxMonitor.Svc;
using Xunit;

namespace WxMonitor.Tests;

public class HeartbeatCheckerTests : IDisposable
{
    private readonly string _path = Path.GetTempFileName();

    public void Dispose() => File.Delete(_path);

    // ── missing file ──────────────────────────────────────────────────────────

    [Fact]
    public void MissingFile_ReturnsNull()
    {
        var age = HeartbeatChecker.GetAge("nonexistent-heartbeat.txt");
        Assert.Null(age);
    }

    // ── valid timestamp ───────────────────────────────────────────────────────

    [Fact]
    public void ValidIso8601Timestamp_ReturnsApproximateAge()
    {
        var writtenAt = DateTime.UtcNow.AddMinutes(-5);
        File.WriteAllText(_path, writtenAt.ToString("o"));

        var age = HeartbeatChecker.GetAge(_path);

        Assert.NotNull(age);
        // Allow a generous window to absorb any test execution delay.
        Assert.InRange(age!.Value.TotalMinutes, 4.9, 5.1);
    }

    [Fact]
    public void RecentTimestamp_ReturnsSmallAge()
    {
        File.WriteAllText(_path, DateTime.UtcNow.ToString("o"));

        var age = HeartbeatChecker.GetAge(_path);

        Assert.NotNull(age);
        Assert.True(age!.Value.TotalSeconds < 5,
            $"Expected age < 5 seconds but was {age.Value.TotalSeconds:F1}s");
    }

    [Fact]
    public void WhitespacePaddedTimestamp_IsParsedCorrectly()
    {
        // File.WriteAllText may add no trailing whitespace, but the code calls .Trim()
        // so extra whitespace should be tolerated.
        var writtenAt = DateTime.UtcNow.AddMinutes(-2);
        File.WriteAllText(_path, "  " + writtenAt.ToString("o") + "  ");

        var age = HeartbeatChecker.GetAge(_path);

        Assert.NotNull(age);
        Assert.InRange(age!.Value.TotalMinutes, 1.9, 2.1);
    }

    // ── invalid content ───────────────────────────────────────────────────────

    [Fact]
    public void InvalidContent_ReturnsNull()
    {
        File.WriteAllText(_path, "not a timestamp");
        Assert.Null(HeartbeatChecker.GetAge(_path));
    }

    [Fact]
    public void EmptyFile_ReturnsNull()
    {
        File.WriteAllText(_path, "");
        Assert.Null(HeartbeatChecker.GetAge(_path));
    }

    [Fact]
    public void WhitespaceOnlyFile_ReturnsNull()
    {
        File.WriteAllText(_path, "   ");
        Assert.Null(HeartbeatChecker.GetAge(_path));
    }
}
