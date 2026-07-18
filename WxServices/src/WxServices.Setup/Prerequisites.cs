using System.Net.Sockets;

using Microsoft.Data.SqlClient;

namespace WxServices.Setup;

/// <summary>Outcome of one prerequisite check.</summary>
public enum PrereqStatus
{
    Pass,
    Warn,
    Fail,
}

/// <summary>One prerequisite check's result (WX-314, AC-1).</summary>
public sealed record PrereqCheck(string Name, PrereqStatus Status, string Detail);

/// <summary>
/// The prerequisite gate (WX-314, AC-1): probe the box, report a bulleted status, and decide
/// whether to proceed. The decision (<see cref="MayProceed"/>) and the report (<see cref="Format"/>)
/// are pure and unit-tested; <see cref="CheckAsync"/> is I/O-bound and verified by the functional run.
/// The elevation-requiring host config (Mixed Mode / TCP / SQL restart) is only DETECTED here, never
/// performed — that stays a manual prerequisite (WX-67).
/// </summary>
public static class Prerequisites
{
    /// <summary>May the setup proceed? Any <see cref="PrereqStatus.Fail"/> blocks; a Warn does not.</summary>
    public static bool MayProceed(IReadOnlyList<PrereqCheck> checks) =>
        checks.All(c => c.Status != PrereqStatus.Fail);

    /// <summary>A bulleted status report — one line per check.</summary>
    public static string Format(IReadOnlyList<PrereqCheck> checks)
    {
        var lines = checks.Select(c =>
        {
            var mark = c.Status switch
            {
                PrereqStatus.Pass => "[ OK ]",
                PrereqStatus.Warn => "[WARN]",
                _ => "[FAIL]",
            };
            var detail = string.IsNullOrEmpty(c.Detail) ? "" : $" — {c.Detail}";
            return $"  {mark} {c.Name}{detail}";
        });
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Runs every prerequisite check against the box for <paramref name="options"/>. I/O-bound.</summary>
    public static async Task<IReadOnlyList<PrereqCheck>> CheckAsync(SetupOptions options)
    {
        var checks = new List<PrereqCheck>();

        try
        {
            await using var conn = new SqlConnection(ConnectionStrings.BuildWxManager(options.Server, "master"));
            await conn.OpenAsync();
            checks.Add(new PrereqCheck("SQL Server reachable (Trusted)", PrereqStatus.Pass, options.Server));

            var integratedOnly = await ScalarIntAsync(conn, "SELECT CONVERT(int, SERVERPROPERTY('IsIntegratedSecurityOnly'))");
            checks.Add(integratedOnly == 0
                ? new PrereqCheck("Mixed-Mode authentication", PrereqStatus.Pass, "enabled")
                : new PrereqCheck("Mixed-Mode authentication", PrereqStatus.Fail,
                    "Windows-only auth; enable Mixed Mode and restart SQL Server (the SQL login the containers use needs it)"));

            var isSysadmin = await ScalarIntAsync(conn, "SELECT IS_SRVROLEMEMBER('sysadmin')");
            checks.Add(isSysadmin == 1
                ? new PrereqCheck("Invoking user is SQL sysadmin", PrereqStatus.Pass, "")
                : new PrereqCheck("Invoking user is SQL sysadmin", PrereqStatus.Fail,
                    "the current Windows user must be a SQL sysadmin to create the login and migrate the schema"));
        }
        catch (Exception ex)
        {
            checks.Add(new PrereqCheck("SQL Server reachable (Trusted)", PrereqStatus.Fail,
                $"could not connect to {options.Server}: {ex.GetBaseException().Message.TrimEnd('.')}"));
        }

        checks.Add(await TcpListeningAsync("127.0.0.1", 1433)
            ? new PrereqCheck("SQL Server TCP/1433", PrereqStatus.Pass, "listening")
            : new PrereqCheck("SQL Server TCP/1433", PrereqStatus.Fail,
                "no TCP listener on 1433; enable TCP/IP in SQL Server Configuration Manager and restart (containers reach the host via host.docker.internal,1433)"));

        if (options.Mode == "full")
        {
            checks.Add(Directory.Exists(options.ServicesDir)
                ? new PrereqCheck("Source services dir present", PrereqStatus.Pass, options.ServicesDir)
                : new PrereqCheck("Source services dir present", PrereqStatus.Fail, $"not found: {options.ServicesDir}"));

            checks.Add(IsOnPath("dotnet")
                ? new PrereqCheck(".NET SDK on PATH", PrereqStatus.Pass, "")
                : new PrereqCheck(".NET SDK on PATH", PrereqStatus.Fail, "install the .NET SDK (full mode builds from source)"));
        }

        checks.Add(IsOnPath("docker")
            ? new PrereqCheck("Docker Desktop", PrereqStatus.Pass, "")
            : new PrereqCheck("Docker Desktop", PrereqStatus.Warn, "not on PATH; needed to RUN the services, not to run this script"));

        return checks;
    }

    private static async Task<int> ScalarIntAsync(SqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? -1 : Convert.ToInt32(result);
    }

    private static async Task<bool> TcpListeningAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            var finished = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(3)));
            return finished == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var d = dir.Trim();
                if (File.Exists(Path.Combine(d, exe + ".exe")) || File.Exists(Path.Combine(d, exe)))
                    return true;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return false;
    }
}