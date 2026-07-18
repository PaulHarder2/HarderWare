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
        Action<string, string> writeAllText)
    {
        var written = new List<string>(files.Count);

        foreach (var file in files)
        {
            var directory = Path.GetDirectoryName(file.Path);
            if (!string.IsNullOrEmpty(directory))
                createDirectory(directory);

            writeAllText(file.Path, file.Content);
            written.Add(file.Path);
        }

        return written;
    }

    /// <summary>
    /// Builds the template reader used by <see cref="LocalFilesPlan.Build"/>, guarding the most
    /// likely operator error — a wrong <c>--services-dir</c>, which otherwise surfaces as a bare
    /// <c>FileNotFoundException</c> that does not say what was expected or how to fix it.
    /// </summary>
    public static Func<string, string> MakeExampleReader(
        Func<string, bool> exists, Func<string, string> readAllText) =>
        path => exists(path)
            ? readAllText(path)
            : throw new SetupException(
                $"Template not found: {path}{Environment.NewLine}" +
                "Each service directory must contain the committed 'appsettings.local.json.example'. " +
                "Check that --services-dir points at the repository's services/ directory.");

    /// <summary>The real filesystem reader — <see cref="MakeExampleReader"/> over <see cref="File"/>.</summary>
    public static Func<string, string> FileSystemExampleReader { get; } =
        MakeExampleReader(File.Exists, File.ReadAllText);
}