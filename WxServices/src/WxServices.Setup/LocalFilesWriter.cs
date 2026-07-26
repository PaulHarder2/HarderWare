namespace WxServices.Setup;

/// <summary>
/// A setup failure the operator can act on (a missing template, an unreachable server). Program.cs
/// reports these as a plain message and a non-zero exit rather than a stack trace.
/// </summary>
public sealed class SetupException : Exception
{
    public SetupException(string message) : base(message) { }

    public SetupException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Flushes the planned <c>appsettings.local.json</c> files to disk (WX-314, AC-5) — the only
/// filesystem mutation in setup. The directory-create and file-write operations are injected so the
/// write behaviour is unit-testable without touching a real tree.
/// </summary>
public static class LocalFilesWriter
{
    /// <summary>
    /// Writes every planned file, creating its parent directory first (the install root may not
    /// exist yet on a fresh box), and returns the paths written in order. Existing files are
    /// overwritten so a re-run reconciles rather than fails.
    /// </summary>
    public static IReadOnlyList<string> Flush(
        IReadOnlyList<LocalFile> files,
        Action<string> createDirectory,
        Action<string, string> writeInPlace,
        Action<string, string> writeAtomic)
    {
        var written = new List<string>(files.Count);

        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file.Path);
            if (!string.IsNullOrEmpty(directory))
                createDirectory(directory);

            // Per-file, not one policy — see LocalFile.AtomicReplace. In-place truncation preserves the
            // inode a single-file Docker bind mount depends on; atomic replace protects a file that is
            // the only copy of what it holds. Applying either everywhere breaks the other case.
            var write = file.AtomicReplace ? writeAtomic : writeInPlace;
            write(file.Path, file.Content);
            written.Add(file.Path);
        }

        return written;
    }

    /// <summary>
    /// Writes <paramref name="content"/> to <paramref name="path"/> <b>atomically</b> — to a temp file
    /// in the same directory, flushed to disk, then moved over the target — so an interrupted write
    /// leaves the original intact rather than a truncated file.
    /// <para>
    /// This matters *because of* WX-326. Before it, the WxManager <c>appsettings.local.json</c> was
    /// fully regenerable and a crash mid-write cost nothing; now that file is, by this change's own
    /// argument, the only home of the operator's keys. A plain truncate-then-write turns a disk-full or
    /// a power cut into permanent loss of exactly what the merge exists to protect. The temp file is a
    /// sibling so the move is same-volume, hence atomic.
    /// </para>
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);

        // Named to match .gitignore's existing "*.tmp.*" rule (which needs a dot AFTER "tmp"). Purely
        // defensive today — this is only ever used for the WxManager file under InstallRoot, outside
        // the repo — so it costs nothing and stays correct if that ever changes.
        var temp = Path.Combine(
            string.IsNullOrEmpty(directory) ? "." : directory,
            $"{Path.GetFileName(path)}.tmp.{Environment.ProcessId}.{Guid.NewGuid():N}");

        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);   // durable before the swap, or the atomicity is theatre
            }

            // File.Replace, not File.Move: Move replaces the directory entry, so the surviving file
            // carries the TEMP file's security descriptor and the destination's ACL is silently
            // discarded. An operator who hardened this file — realistic, since it holds a connection
            // string — would have that hardening quietly removed by a setup re-run, with no message.
            // Replace preserves the destination's attributes and ACL.
            //
            // Try-then-catch rather than File.Exists-then-branch — the same shape argued for on both
            // readers above, and the first cut of this method got it wrong while making that argument
            // two functions away. Exists-then-act loses the race in both directions: a target appearing
            // in between made the bare File.Move throw, and File.Exists reports false for a file that is
            // present but not stat-able. Either way setup dies after the database is provisioned,
            // leaving WxManager with no config file. (WX-326, found by code review.)
            try
            {
                File.Replace(temp, path, destinationBackupFileName: null);
            }
            catch (FileNotFoundException)
            {
                File.Move(temp, path, overwrite: true);   // fresh-box path: nothing to preserve
            }
        }
        catch
        {
            try { File.Delete(temp); } catch { /* best effort — never mask the original failure */ }
            throw;
        }
    }

    /// <summary>
    /// Builds the template reader used by <see cref="LocalFilesPlan.Build"/>, guarding the most
    /// likely operator error — a wrong <c>--services-dir</c>, which otherwise surfaces as a bare
    /// <c>FileNotFoundException</c> that does not say what was expected or how to fix it.
    /// </summary>
    /// <remarks>
    /// Reads first and catches "not found", for the same reason as <see cref="MakeOptionalReader"/> —
    /// <c>File.Exists</c> also returns false for a file that exists but cannot be *accessed*, so an
    /// Exists-then-read shape would answer a permission failure with *"Template not found … check that
    /// --services-dir points at the repository's services/ directory"*, sending the operator to fix a
    /// path that is already correct. (WX-326: the first cut argued this at length on the reader below
    /// and left this one on the old shape — the same defect one function away, caught by code review.)
    /// </remarks>
    public static Func<string, string> MakeExampleReader(Func<string, string> readAllText) =>
        path =>
        {
            try
            {
                return readAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                throw new SetupException(
                    $"Template not found: {path}{Environment.NewLine}" +
                    "Each service directory must contain the committed 'appsettings.local.json.example'. " +
                    "Check that --services-dir points at the repository's services/ directory.", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Present but unreadable — locked by antivirus, permission-denied, a directory in its
                // place. Dropping the File.Exists guard fixed a misleading message but left this case
                // escaping as an unhandled stack trace past Program.cs's `catch (SetupException)`, since
                // unlike the optional reader this one has no caller that wraps I/O failures. A different
                // message from "not found", because it is a different problem. (WX-326, code review.)
                throw new SetupException(
                    $"Template could not be read: {path}{Environment.NewLine}" +
                    $"{ex.Message}{Environment.NewLine}" +
                    "The file is there but not readable — check that nothing holds it open and that " +
                    "you have permission to read it.", ex);
            }
        };

    /// <summary>The real filesystem reader — <see cref="MakeExampleReader"/> over <see cref="File"/>.</summary>
    public static Func<string, string> FileSystemExampleReader { get; } =
        MakeExampleReader(File.ReadAllText);

    /// <summary>
    /// Builds the reader for a file that may legitimately not exist yet — the WxManager
    /// <c>appsettings.local.json</c>, which setup merges into rather than rebuilds (WX-326).
    /// Returns null when absent, which the generator reads as the fresh-box path. Contrast
    /// <see cref="MakeExampleReader"/>, where a missing file is an operator error.
    /// </summary>
    /// <remarks>
    /// <b>Reads first and catches "not found", rather than asking <c>File.Exists</c> and then reading.</b>
    /// Two reasons, and the second is why this shape is not merely stylistic:
    /// <list type="number">
    /// <item><description>
    /// <c>File.Exists</c> returns <c>false</c> for a file the caller cannot access, not just one that is
    /// absent. Under an Exists-then-read design a permission problem is therefore indistinguishable from
    /// a fresh box — setup would take the fresh-box path, plan a connection-string-only file, and the
    /// flush would <i>destroy the operator's keys</i>. That is this ticket's own defect arriving through
    /// its fix. (Measured 2026-07-26: a Deny-Read ACE on the current user did <i>not</i> reproduce it —
    /// <c>File.Exists</c> returned true and the read threw as wanted — so the documented behaviour
    /// evidently depends on which right is denied. Read-then-catch removes the question entirely, which
    /// is worth more than knowing exactly which ACL triggers it.)
    /// </description></item>
    /// <item><description>
    /// It closes the check-then-use gap: a file deleted between the <c>Exists</c> call and the read.
    /// </description></item>
    /// </list>
    /// Only "the file is not there" is swallowed. Every other failure — permission, sharing violation,
    /// I/O — propagates so the caller can name the path.
    /// </remarks>
    public static Func<string, string?> MakeOptionalReader(Func<string, string> readAllText) =>
        path =>
        {
            try
            {
                return readAllText(path);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;   // no existing file — the fresh-box path
            }
        };

    /// <summary>
    /// The real filesystem reader — <see cref="MakeOptionalReader"/> over <see cref="File"/>.
    /// <para>
    /// <b>Load-bearing detail:</b> <see cref="File.ReadAllText(string)"/> detects and strips a UTF-8
    /// BOM. That is not incidental here — PowerShell 5.1's <c>Set-Content -Encoding UTF8</c> writes
    /// one, so a settings file edited that way (including by our own test procedure) begins
    /// <c>EF BB BF</c>, and <c>JsonNode.Parse</c> rejects a leading BOM with <c>"'0xEF' is an invalid
    /// start of a value"</c> — a message naming nothing an operator could act on. Production is safe
    /// only because this reader strips it. A substituted reader that returns a raw string does
    /// <b>not</b>, so a test using one is exercising a different path than the shipped code.
    /// (WX-326, found by review.)
    /// </para>
    /// </summary>
    public static Func<string, string?> FileSystemOptionalReader { get; } =
        MakeOptionalReader(File.ReadAllText);
}