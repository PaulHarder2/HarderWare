using System;
using System.Collections.Generic;
using System.Linq;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-5, test-first: flushing the planned files to disk, and the guard for a missing
/// <c>.example</c> template (the item deferred from the earlier self-review). The filesystem is
/// injected, so writing is proven without touching a real directory.
/// </summary>
public class LocalFilesWriterTests
{
    /// <summary>An in-memory stand-in for the filesystem: records directories created and files written.</summary>
    private sealed class FakeDisk
    {
        public List<string> Directories { get; } = new();
        public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void CreateDirectory(string path) => Directories.Add(path);
        public void WriteAllText(string path, string content) => Files[path] = content;
    }

    private static readonly LocalFile[] Planned =
    {
        new(@"C:\svc\wxparser\appsettings.local.json", "{ \"a\": 1 }"),
        new(@"C:\root\appsettings.local.json", "{ \"b\": 2 }"),
    };

    [Fact]
    public void Flush_WritesEveryPlannedFile()
    {
        var disk = new FakeDisk();

        var written = LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText);

        Assert.Equal(2, disk.Files.Count);
        Assert.Equal("{ \"a\": 1 }", disk.Files[@"C:\svc\wxparser\appsettings.local.json"]);
        Assert.Equal("{ \"b\": 2 }", disk.Files[@"C:\root\appsettings.local.json"]);
        Assert.Equal(Planned.Select(f => f.Path), written);
    }

    /// <summary>The install root may not exist yet on a fresh box, so each parent is created first.</summary>
    [Fact]
    public void Flush_CreatesEachParentDirectory()
    {
        var disk = new FakeDisk();

        LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText);

        Assert.Contains(@"C:\svc\wxparser", disk.Directories);
        Assert.Contains(@"C:\root", disk.Directories);
    }

    /// <summary>Re-running setup overwrites rather than erroring (AC-4 idempotency, file side).</summary>
    [Fact]
    public void Flush_OverwritesOnRerun()
    {
        var disk = new FakeDisk();
        disk.WriteAllText(@"C:\root\appsettings.local.json", "stale");

        LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText);

        Assert.Equal("{ \"b\": 2 }", disk.Files[@"C:\root\appsettings.local.json"]);
    }

    // ---- the missing-template guard ---------------------------------------

    [Fact]
    public void ExampleReader_ReturnsTemplateContent()
    {
        var read = LocalFilesWriter.MakeExampleReader(
            exists: _ => true, readAllText: path => $"contents of {path}");

        Assert.Equal(@"contents of C:\svc\wxvis\appsettings.local.json.example",
            read(@"C:\svc\wxvis\appsettings.local.json.example"));
    }

    /// <summary>
    /// A missing template is the most likely operator error (wrong --services-dir), so it must fail
    /// with an actionable message naming the path — not a bare FileNotFoundException.
    /// </summary>
    [Fact]
    public void ExampleReader_MissingTemplate_ThrowsActionableSetupException()
    {
        var read = LocalFilesWriter.MakeExampleReader(
            exists: _ => false, readAllText: _ => throw new InvalidOperationException("must not be read"));

        var ex = Assert.Throws<SetupException>(
            () => read(@"C:\svc\wxvis\appsettings.local.json.example"));

        Assert.Contains(@"C:\svc\wxvis\appsettings.local.json.example", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--services-dir", ex.Message, StringComparison.Ordinal);
    }
}