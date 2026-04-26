// Unit tests for LogScanner.
// Verifies timestamp filtering, severity filtering, multi-line aggregation,
// first-run baseline behaviour, and the latestTimestamp out-parameter.

using WxMonitor.Svc;

using Xunit;

namespace WxMonitor.Tests;

public class LogScannerTests : IDisposable
{
    // Temp file created fresh for each test, deleted on dispose.
    private readonly string _path = Path.GetTempFileName();

    public void Dispose() => File.Delete(_path);

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a single log line in the log4net PatternLayout style used by WxServices:
    /// <c>yyyy-MM-dd HH:mm:ss.fff LEVEL [File::Method:Line] message</c>.
    /// Level is left-padded to 5 characters to match the %-5level format specifier.
    /// </summary>
    private static string LogLine(DateTime ts, string level, string message)
        => $"{ts:yyyy-MM-dd HH:mm:ss.fff} {level,-5} [Test.cs::Method:1] {message}";

    private static readonly DateTime T0 = new(2026, 3, 30, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T1 = T0.AddMinutes(1);
    private static readonly DateTime T2 = T0.AddMinutes(2);

    // ── missing / empty file ──────────────────────────────────────────────────

    [Fact]
    public void MissingFile_ReturnsEmptyList()
    {
        var entries = LogScanner.Scan("nonexistent.log", T0, "ERROR", out _);
        Assert.Empty(entries);
    }

    [Fact]
    public void EmptyFile_ReturnsEmptyList_LatestTimestampUnchanged()
    {
        File.WriteAllText(_path, "");
        var entries = LogScanner.Scan(_path, T0, "ERROR", out var latest);
        Assert.Empty(entries);
        Assert.Equal(T0, latest);
    }

    // ── first run (since == null) ─────────────────────────────────────────────

    [Fact]
    public void FirstRun_NullSince_ReturnsEmptyList_SetsLatestTimestamp()
    {
        // On first run, no alerts are raised — just the baseline is established.
        File.WriteAllLines(_path,
        [
            LogLine(T0, "INFO",  "service started"),
            LogLine(T1, "ERROR", "something broke"),
        ]);

        var entries = LogScanner.Scan(_path, since: null, "ERROR", out var latest);

        Assert.Empty(entries);
        Assert.Equal(T1, latest);
    }

    [Fact]
    public void FirstRun_EmptyFile_LatestTimestampRemainsNull()
    {
        File.WriteAllText(_path, "");
        LogScanner.Scan(_path, since: null, "ERROR", out var latest);
        Assert.Null(latest);
    }

    // ── timestamp filtering ───────────────────────────────────────────────────

    [Fact]
    public void ReturnsOnlyEntriesStrictlyAfterSince()
    {
        File.WriteAllLines(_path,
        [
            LogLine(T0, "ERROR", "old error"),
            LogLine(T1, "ERROR", "boundary error"),
            LogLine(T2, "ERROR", "new error"),
        ]);

        // since = T1: only T2 should be returned (T1 itself is not strictly after T1)
        var entries = LogScanner.Scan(_path, since: T1, "ERROR", out _);

        Assert.Single(entries);
        Assert.Equal(T2, entries[0].Timestamp);
    }

    [Fact]
    public void EntryAtExactlySince_IsExcluded()
    {
        File.WriteAllLines(_path, [LogLine(T0, "ERROR", "exactly at since")]);
        var entries = LogScanner.Scan(_path, since: T0, "ERROR", out _);
        Assert.Empty(entries);
    }

    // ── severity filtering ────────────────────────────────────────────────────

    [Fact]
    public void EntriesBelowMinSeverity_AreExcluded()
    {
        File.WriteAllLines(_path,
        [
            LogLine(T1, "INFO",  "informational"),
            LogLine(T1, "WARN",  "a warning"),
            LogLine(T1, "ERROR", "an error"),
        ]);

        var entries = LogScanner.Scan(_path, since: T0, "ERROR", out _);

        Assert.Single(entries);
        Assert.Equal("ERROR", entries[0].Severity);
    }

    [Fact]
    public void EntryAtExactlyMinSeverity_IsIncluded()
    {
        File.WriteAllLines(_path, [LogLine(T1, "WARN", "a warning")]);
        var entries = LogScanner.Scan(_path, since: T0, "WARN", out _);
        Assert.Single(entries);
    }

    [Fact]
    public void AllSeveritiesAboveThreshold_AreReturned()
    {
        File.WriteAllLines(_path,
        [
            LogLine(T1, "WARN",  "warn"),
            LogLine(T1, "ERROR", "error"),
            LogLine(T1, "FATAL", "fatal"),
        ]);

        var entries = LogScanner.Scan(_path, since: T0, "WARN", out _);
        Assert.Equal(3, entries.Count);
    }

    // ── latestTimestamp out-parameter ─────────────────────────────────────────

    [Fact]
    public void LatestTimestamp_ReflectsMostRecentEntry_RegardlessOfSeverity()
    {
        // Even INFO entries (below ERROR threshold) should advance latestTimestamp.
        File.WriteAllLines(_path,
        [
            LogLine(T1, "ERROR", "error"),
            LogLine(T2, "INFO",  "info — below threshold but most recent"),
        ]);

        LogScanner.Scan(_path, since: T0, "ERROR", out var latest);
        Assert.Equal(T2, latest);
    }

    // ── multi-line entry (stack trace) aggregation ────────────────────────────

    [Fact]
    public void StackTrace_AggregatedIntoSingleEntry()
    {
        File.WriteAllLines(_path,
        [
            LogLine(T1, "ERROR", "unhandled exception"),
            " System.Exception: Something went wrong",
            "   at SomeClass.SomeMethod() in file.cs:line 10",
            "   at OtherClass.OtherMethod() in other.cs:line 20",
        ]);

        var entries = LogScanner.Scan(_path, since: T0, "ERROR", out _);

        Assert.Single(entries);
        Assert.Contains("System.Exception", entries[0].Text);
        Assert.Contains("SomeClass.SomeMethod", entries[0].Text);
    }

    [Fact]
    public void MultipleEntries_EachWithContinuationLines_ProduceCorrectCount()
    {
        File.WriteAllLines(_path,
        [
            LogLine(T1, "ERROR", "first error"),
            " continuation of first",
            LogLine(T2, "ERROR", "second error"),
            " continuation of second",
        ]);

        var entries = LogScanner.Scan(_path, since: T0, "ERROR", out _);

        Assert.Equal(2, entries.Count);
        Assert.Contains("continuation of first", entries[0].Text);
        Assert.Contains("continuation of second", entries[1].Text);
    }
}