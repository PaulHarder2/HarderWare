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

        /// <summary>Paths written atomically — so the per-file strategy choice is assertable (WX-326).</summary>
        public List<string> AtomicWrites { get; } = new();

        public void CreateDirectory(string path) => Directories.Add(path);
        public void WriteAllText(string path, string content) => Files[path] = content;
        public void WriteAtomic(string path, string content)
        {
            AtomicWrites.Add(path);
            Files[path] = content;
        }
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

        var written = LocalFilesWriter.Flush(
            Planned, disk.CreateDirectory, disk.WriteAllText, disk.WriteAtomic);

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

        LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText, disk.WriteAtomic);

        Assert.Contains(ServiceDir, disk.Directories);
        Assert.Contains(InstallRoot, disk.Directories);
    }

    /// <summary>Re-running setup overwrites rather than erroring (AC-4 idempotency, file side).</summary>
    [Fact]
    public void Flush_OverwritesOnRerun()
    {
        var disk = new FakeDisk();
        disk.WriteAllText(Planned[1].Path, "stale");

        LocalFilesWriter.Flush(Planned, disk.CreateDirectory, disk.WriteAllText, disk.WriteAtomic);

        Assert.Equal("{ \"b\": 2 }", disk.Files[Planned[1].Path]);
    }

    // ---- the missing-template guard ---------------------------------------

    [Fact]
    public void ExampleReader_ReturnsTemplateContent()
    {
        var read = LocalFilesWriter.MakeExampleReader(readAllText: path => $"contents of {path}");

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
            readAllText: _ => throw new FileNotFoundException("not there"));

        var ex = Assert.Throws<SetupException>(
            () => read(@"C:\svc\wxvis\appsettings.local.json.example"));

        Assert.Contains(@"C:\svc\wxvis\appsettings.local.json.example", ex.Message, StringComparison.Ordinal);
        Assert.Contains("--services-dir", ex.Message, StringComparison.Ordinal);
    }
}
/// <summary>
/// WX-326: the two file-touching behaviours that an injected fake cannot prove — the optional reader's
/// absent-vs-unreadable distinction, and the atomicity of the write. These use a real temp directory
/// deliberately. The lesson that put them here: <c>Build_UnreadableExistingFile_StillNamesThePath</c>
/// substitutes the whole reader, so it demonstrates the *caller's* wrapping and says nothing about
/// whether the production reader ever throws in the first place.
/// </summary>
public class RealFilesystemReaderAndWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"wx326-{Guid.NewGuid():N}");

    public RealFilesystemReaderAndWriterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void FileSystemOptionalReader_AbsentFile_ReturnsNull()
    {
        var missing = Path.Combine(_dir, "does-not-exist.json");

        Assert.Null(LocalFilesWriter.FileSystemOptionalReader(missing));
    }

    [Fact]
    public void FileSystemOptionalReader_MissingDirectory_ReturnsNull()
    {
        // The fresh-box case: InstallRoot itself does not exist yet.
        var missing = Path.Combine(_dir, "no-such-dir", "appsettings.local.json");

        Assert.Null(LocalFilesWriter.FileSystemOptionalReader(missing));
    }

    [Fact]
    public void FileSystemOptionalReader_PresentFile_ReturnsContent_AndStripsBom()
    {
        // The BOM strip is load-bearing, not incidental: PowerShell's Set-Content -Encoding UTF8
        // writes one, and JsonNode.Parse rejects a leading BOM with a message naming nothing the
        // operator could act on. Written through UTF8Encoding(true) so the BOM is really there.
        var path = Path.Combine(_dir, "appsettings.local.json");
        File.WriteAllText(path, """{ "Fetch": { "HomeIcao": "KAUS" } }""", new System.Text.UTF8Encoding(true));

        Assert.Equal((byte)0xEF, File.ReadAllBytes(path)[0]);   // the BOM is on disk...

        var read = LocalFilesWriter.FileSystemOptionalReader(path);

        Assert.StartsWith("{", read);                            // ...and gone by the time we parse
        Assert.Equal("KAUS", System.Text.Json.Nodes.JsonNode.Parse(read!)!["Fetch"]!["HomeIcao"]!.GetValue<string>());
    }

    [Fact]
    public void WriteAtomic_ReplacesExistingContent_AndLeavesNoTempFile()
    {
        var path = Path.Combine(_dir, "appsettings.local.json");
        File.WriteAllText(path, "OLD");

        LocalFilesWriter.WriteAtomic(path, "NEW");

        Assert.Equal("NEW", File.ReadAllText(path));
        Assert.Equal(new[] { "appsettings.local.json" },
            Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());   // no stray *.tmp
    }

    [Fact]
    public void WriteAtomic_CreatesFileThatDidNotExist()
    {
        var path = Path.Combine(_dir, "brand-new.json");

        LocalFilesWriter.WriteAtomic(path, "NEW");

        Assert.Equal("NEW", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAtomic_FailedWrite_LeavesOriginalIntact_AndRemovesTheTempFile()
    {
        // NAMED CAREFULLY. This does NOT prove atomicity, and an earlier version of it claimed to:
        // when WriteAtomic was temporarily reverted to a plain File.WriteAllText, this test still
        // PASSED, because writing to a locked file throws before truncating either way. What it does
        // prove is the cleanup contract — the original survives a failure and no temp file is left
        // behind. Real atomicity (a crash between truncate and write) is not reachable from a unit
        // test; it rests on the temp-file-then-replace construction, spelled out in WriteAtomic's own
        // doc comment rather than asserted here.
        // (WX-326: a test that cannot fail for the thing its name claims is decoration.)
        //
        // THE FAILURE IS INDUCED PLATFORM-NEUTRALLY, and the first version was not. It held the target
        // open with FileShare.None, which blocks the replace on Windows but NOT on Linux: FileShare
        // maps to an advisory lock on the target's inode, while the replace operates on the directory
        // entry, so on ubuntu-latest CI it would have succeeded and turned three assertions red. These
        // tests are in WxServices.CI.slnf and CI runs on Linux, so that was a red build waiting to
        // happen — the documented Windows-local-vs-Linux-CI trap, caught by code review. Putting a
        // DIRECTORY where the file should go fails identically on both.
        var path = Path.Combine(_dir, "appsettings.local.json");
        File.WriteAllText(path, "ORIGINAL");

        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(blocked);   // a directory cannot be replaced by a file, anywhere

        var ex = Record.Exception(() => LocalFilesWriter.WriteAtomic(blocked, "NEW"));

        Assert.NotNull(ex);
        Assert.True(
            ex is IOException or UnauthorizedAccessException,
            $"expected a file-access failure, got {ex.GetType().Name}");
        Assert.Equal("ORIGINAL", File.ReadAllText(path));            // untouched bystander
        Assert.True(Directory.Exists(blocked));                       // target unchanged
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp.*"));            // temp cleaned up
    }
}