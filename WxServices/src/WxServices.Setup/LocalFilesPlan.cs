namespace WxServices.Setup;

/// <summary>A file the setup script will write: absolute <see cref="Path"/> + full <see cref="Content"/>.</summary>
public sealed record LocalFile(string Path, string Content);

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
    /// <paramref name="options"/> + <paramref name="password"/>; the WxManager file = the native
    /// Trusted connection string only.
    /// </summary>
    public static IReadOnlyList<LocalFile> Build(
        SetupOptions options, string password, Func<string, string> readExample)
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
        files.Add(new LocalFile(
            System.IO.Path.Combine(options.InstallRoot, "appsettings.local.json"),
            LocalJsonGenerator.BuildWxManagerLocalJson(wxManagerConn)));

        return files;
    }
}