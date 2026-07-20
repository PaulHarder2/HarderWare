namespace WxServices.Setup;

/// <summary>
/// Parsed command-line options for the setup console (WX-314, AC-3). No hardcoded targets — every
/// destination is an input, which is what makes the script testable in isolation and reusable for
/// the future multi-environment / OpRegion story.
/// </summary>
public sealed record SetupOptions(
    string Mode,
    string InstallRoot,
    string ServicesDir,
    string Database,
    string SqlLogin,
    string Server)
{
    public const string DefaultMode = "full";
    public const string DefaultDatabase = "WeatherData";
    public const string DefaultSqlLogin = "wxservices";
    public const string DefaultServer = @".\SQLEXPRESS";

    /// <summary>
    /// Parses <paramref name="args"/> over the defaults. The literal defaults (mode / database /
    /// sql-login / server) are the constants above; <c>--install-root</c> and <c>--services-dir</c>
    /// default to <paramref name="defaultInstallRoot"/> / <paramref name="defaultServicesDir"/>,
    /// which the caller resolves from the environment (the <c>WXSERVICES_INSTALL_ROOT</c> env var
    /// and the repo <c>services/</c> location). Throws <see cref="ArgumentException"/> on an unknown
    /// flag, a flag missing its value, or an invalid <c>--mode</c>.
    /// </summary>
    public static SetupOptions Parse(
        IReadOnlyList<string> args, string defaultInstallRoot, string defaultServicesDir)
    {
        var mode = DefaultMode;
        var installRoot = defaultInstallRoot;
        var servicesDir = defaultServicesDir;
        var database = DefaultDatabase;
        var sqlLogin = DefaultSqlLogin;
        var server = DefaultServer;

        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i];
            switch (flag)
            {
                case "--mode": mode = NextValue(args, flag, ref i); break;
                case "--install-root": installRoot = NextValue(args, flag, ref i); break;
                case "--services-dir": servicesDir = NextValue(args, flag, ref i); break;
                case "--database": database = NextValue(args, flag, ref i); break;
                case "--sql-login": sqlLogin = NextValue(args, flag, ref i); break;
                case "--server": server = NextValue(args, flag, ref i); break;
                default: throw new ArgumentException($"Unknown argument: '{flag}'.");
            }
        }

        if (mode is not ("full" or "opregion"))
            throw new ArgumentException($"--mode must be 'full' or 'opregion' (got '{mode}').");

        return new SetupOptions(mode, installRoot, servicesDir, database, sqlLogin, server);
    }

    private static string NextValue(IReadOnlyList<string> args, string flag, ref int i)
    {
        if (i + 1 >= args.Count)
            throw new ArgumentException($"Flag '{flag}' is missing its value.");
        return args[++i];
    }
}