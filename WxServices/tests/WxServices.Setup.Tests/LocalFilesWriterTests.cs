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

    // Paths are composed with Path.Combine rather than written as Windows literals: the writer
    // calls Path.GetDirectoryName, and on Linux (where CI runs) a backslash is an ordinary
    // filename character, so "C:\svc\x\file.json" has no directory part at all and the
    // create-directory assertions silently see nothing. The console itself only runs on Windows,
    // but its tests must pass on both.
    private static readonly string ServiceDir = Path.Combine("svcroot", "wxparser");
    private static readonly string InstallRoot = "installroot";

    private static readonly LocalFile[] Planned =
    {
        new(Path.Combine(ServiceDir, "appsettings.local.json"), "{ \"a\": 1 }"),
        new(Path.Combine(InstallRoot, "appsettings.local.json"), "{ \"b\": 2 }"),
    };

    [Fact]
    public void Flush_WritesEveryPlannedFile()
    {
        var disk = new FakeDisk();

        var written = LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText);

        Assert.Equal(2, disk.Files.Count);
        Assert.Equal("{ \"a\": 1 }", disk.Files[Planned[0].Path]);
        Assert.Equal("{ \"b\": 2 }", disk.Files[Planned[1].Path]);
        Assert.Equal(Planned.Select(f => f.Path), written);
    }

    /// <summary>The install root may not exist yet on a fresh box, so each parent is created first.</summary>
    [Fact]
    public void Flush_CreatesEachParentDirectory()
    {
        var disk = new FakeDisk();

        LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText);

        Assert.Contains(ServiceDir, disk.Directories);
        Assert.Contains(InstallRoot, disk.Directories);
    }

    /// <summary>Re-running setup overwrites rather than erroring (AC-4 idempotency, file side).</summary>
    [Fact]
    public void Flush_OverwritesOnRerun()
    {
        var disk = new FakeDisk();
        disk.WriteAllText(Planned[1].Path, "stale");

        LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText);

        Assert.Equal("{ \"b\": 2 }", disk.Files[Planned[1].Path]);
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