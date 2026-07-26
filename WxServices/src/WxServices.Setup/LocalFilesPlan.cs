namespace WxServices.Setup;

/// <summary>
/// A file the setup script will write: absolute <see cref="Path"/> + full <see cref="Content"/>.
/// <para>
/// <see cref="AtomicReplace"/> selects the write strategy, and the choice is **not** "atomic is always
/// better" (WX-326):
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>false — truncate in place.</b> Required for the four container files, because each is a
/// <b>single-file Docker bind mount</b> (<c>./wxreport/appsettings.local.json:/opt/…:ro</c>). A bind
/// mount binds the <i>inode</i>; replacing the directory entry leaves every running container reading
/// the old, now-unlinked file — forever, since they run <c>restart: unless-stopped</c>. Truncating in
/// place keeps the inode, so the running container sees the new content.
/// </description></item>
/// <item><description>
/// <b>true — atomic replace.</b> For the WxManager file, which nothing bind-mounts and which, since
/// this ticket, is the sole home of the operator's keys, so a torn write loses them permanently.
/// </description></item>
/// </list>
/// The two requirements are in direct opposition, which is why this is a per-file property rather than
/// one policy. (Applying atomicity to all five was caught by code review before it shipped.)
/// </summary>
public sealed record LocalFile(string Path, string Content, bool AtomicReplace = false);

/// <summary>
/// Plans the five per-environment <c>appsettings.local.json</c> files (WX-314, AC-5) — four
/// container files + WxManager — as <see cref="LocalFile"/> pairs, composing the pure generators.
/// Reading the container <c>.example</c> templates is injected so the plan is unit-testable without
/// disk; a separate thin writer flushes the plan (that's the only I/O).
/// </summary>
public static class LocalFilesPlan
{
    /// <summary>The four containerized services (each has a bind-mounted <c>appsettings.local.json</c>).</summary>
    public static readonly IReadOnlyList<string> ContainerServices =
        new[] { "wxparser", "wxreport", "wxmonitor", "wxvis" };

    /// <summary>
    /// Builds the five files. Each container file = its <c>.example</c> (read via
    /// <paramref name="readExample"/>) with the connection string rebuilt from
    /// <paramref name="options"/> + <paramref name="password"/>; the WxManager file = its own
    /// existing content (read via <paramref name="readExistingWxManager"/>, null when the file does
    /// not exist) with only the native Trusted connection string set — a re-run must not destroy
    /// operator-owned keys (WX-326).
    /// </summary>
    /// <exception cref="SetupException">
    /// The existing WxManager <c>appsettings.local.json</c> cannot be parsed. Setup stops rather
    /// than overwriting a file whose contents it could not read.
    /// </exception>
    public static IReadOnlyList<LocalFile> Build(
        SetupOptions options,
        string password,
        Func<string, string> readExample,
        Func<string, string?> readExistingWxManager)
    {
        var files = new List<LocalFile>();

        var containerConn = ConnectionStrings.BuildContainer(options.Database, options.SqlLogin, password);
        foreach (var svc in ContainerServices)
        {
            var example = readExample(System.IO.Path.Combine(options.ServicesDir, svc, "appsettings.local.json.example"));
            var content = LocalJsonGenerator.BuildContainerLocalJson(example, containerConn);
            files.Add(new LocalFile(
                System.IO.Path.Combine(options.ServicesDir, svc, "appsettings.local.json"), content));
        }

        var wxManagerConn = ConnectionStrings.BuildWxManager(options.Server, options.Database);
        var wxManagerPath = System.IO.Path.Combine(options.InstallRoot, "appsettings.local.json");
        files.Add(new LocalFile(
            wxManagerPath,
            BuildWxManagerMerged(wxManagerPath, readExistingWxManager, wxManagerConn),
            AtomicReplace: true));

        return files;
    }

    /// <summary>
    /// Reads the operator's existing WxManager local.json and merges the connection string into it,
    /// turning any failure to read or parse it into a <see cref="SetupException"/> that names the path —
    /// the generator is pure and does not know where its input came from, and "invalid JSON" with no
    /// path is not actionable.
    /// </summary>
    /// <remarks>
    /// The read is inside the <c>try</c> deliberately: a locked or permission-denied file throws
    /// <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> from the reader, and if that
    /// escaped it would surface as a generic failure carrying no path — the gap this wrapper closes.
    /// Every way this file can fail names the file.
    /// <para>
    /// <b>Accepted residual risk, deliberately not engineered away (WX-326).</b> This content is
    /// produced before the database work and written after it, so an edit made to the file <i>during</i>
    /// a setup run is reverted by the write. A <c>RefreshWxManager</c> step that re-read and re-merged
    /// immediately before the flush was built to close that window and then <b>removed</b>: across three
    /// consecutive review rounds it produced two data-loss defects (treating a save-in-progress as a
    /// fresh box and wiping every key; overwriting an operator's mid-run edit with the pre-edit
    /// snapshot) and three reporting defects, while the window it closed only opens if the operator
    /// edits this file during their own interactive setup run. The machinery was a larger hazard than
    /// the hazard. Documented in <c>docs/test-procedures/WX-326.md</c> instead: do not edit
    /// <c>appsettings.local.json</c> while setup is running.
    /// </para>
    /// </remarks>
    private static string BuildWxManagerMerged(
        string path, Func<string, string?> readExisting, string connectionString)
    {
        try
        {
            var existing = readExisting(path);
            return LocalJsonGenerator.BuildWxManagerLocalJson(existing, connectionString);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or ArgumentException
                                      or IOException or UnauthorizedAccessException)
        {
            throw new SetupException(
                $"Existing settings file could not be used: {path}{Environment.NewLine}" +
                $"{ex.Message}{Environment.NewLine}" +
                "Setup will not overwrite a file it cannot read, because that would destroy every " +
                "key in it. Fix it (or move the file aside to start fresh) and re-run." +
                LenientJsonHint(ex),
                ex);
        }
    }

    /// <summary>
    /// The comments/trailing-commas note — appended <b>only</b> for a JSON syntax error, the one
    /// cause it actually explains. Four different failures reach the catch above (syntax error, root
    /// not an object, <c>ConnectionStrings</c> not an object, duplicate key), and an unconditional
    /// note sends an operator whose file has neither construct hunting for something that is not
    /// there — the same "not actionable" fault as omitting the path, inverted. (WX-326, found by
    /// review.)
    /// </summary>
    private static string LenientJsonHint(Exception ex) =>
        ex is System.Text.Json.JsonException
            ? Environment.NewLine +
              "Note: // comments and trailing commas are accepted by the running services but " +
              "NOT here, deliberately — setup would have to drop them to rewrite the file, and " +
              "silently deleting what you wrote is the very fault this check exists to prevent. " +
              "Remove them and the value they document survives in the file."
            : string.Empty;
}