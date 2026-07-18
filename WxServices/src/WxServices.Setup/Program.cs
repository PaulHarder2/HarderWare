// WxServices.Setup — first-time setup console (WX-314).
//
// Order matters and is not arbitrary: the login is server-level and must exist before anything
// authenticates as it; CREATE USER needs the database, which does not exist until EnsureSchemaAsync
// runs its migrations; and the Config seed needs the schema. Files are written only once the
// database work has succeeded, so a failed run does not leave configuration on disk pointing at a
// login or database that was never created.
//
//   prerequisites -> prompt -> confirm -> CREATE LOGIN -> EnsureSchema -> CREATE USER + roles
//                 -> write appsettings.local.json x5 -> seed Config
//
// Nothing here elevates: host-level configuration that needs elevation (Mixed-Mode auth, TCP) is
// detected by the prerequisite gate and reported, never performed.
using System.Text;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxServices.Setup;

var installRootDefault = Environment.GetEnvironmentVariable("WXSERVICES_INSTALL_ROOT") ?? @"C:\HarderWare";
var servicesDirDefault = Environment.GetEnvironmentVariable("WXSERVICES_SERVICES_DIR")
    ?? Path.Combine(Directory.GetCurrentDirectory(), "services");

SetupOptions options;
try
{
    options = SetupOptions.Parse(args, installRootDefault, servicesDirDefault);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Argument error: {ex.Message}");
    return 2;
}

Console.WriteLine($"WxServices.Setup — mode={options.Mode}  server={options.Server}  database={options.Database}");
Console.WriteLine();

try
{
    return await RunAsync(options);
}
catch (SetupException ex)
{
    // Actionable failures print as a plain message: the operator needs the instruction, not a stack.
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Setup failed: {ex.Message}");
    return 1;
}

async Task<int> RunAsync(SetupOptions opts)
{
    // ---- 1. Prerequisites (AC-1) — detect, never fix ----------------------
    Console.WriteLine("Prerequisites:");
    var checks = await Prerequisites.CheckAsync(opts);
    Console.WriteLine(Prerequisites.Format(checks));
    Console.WriteLine();

    if (!Prerequisites.MayProceed(checks))
    {
        Console.Error.WriteLine("Prerequisites not met — fix the [FAIL] items above and re-run. Nothing was changed.");
        return 1;
    }

    // ---- 2. Prompt (AC-2) -------------------------------------------------
    var prompter = new ConsolePrompter(Console.ReadLine, Console.Write, ReadSecret);

    Console.WriteLine("Foundational settings (stored in the Config table):");
    var inputs = prompter.PromptFoundational();
    Console.WriteLine();

    Console.WriteLine("SQL login (used by the four service containers over TCP):");
    var password = prompter.PromptPassword(opts.SqlLogin);
    Console.WriteLine();

    // ---- 3. Show the DDL and get an explicit confirmation (AC-4) ----------
    var loginStatements = SqlProvisioning.BuildLoginStatements(opts.SqlLogin, password);
    var userStatements = SqlProvisioning.BuildUserStatements(opts.SqlLogin, SqlProvisioning.LeastPrivilegeRoles);

    Console.WriteLine("The following will be executed against " + opts.Server + ":");
    Console.WriteLine();
    foreach (var statement in loginStatements.Concat(userStatements))
    {
        Console.WriteLine(statement.Display);
        Console.WriteLine();
    }

    if (!Confirm("Proceed?"))
    {
        Console.WriteLine("Cancelled. Nothing was changed.");
        return 1;
    }

    Console.WriteLine();

    // ---- 4. Server-level login -------------------------------------------
    var masterConnection = ConnectionStrings.BuildWxManager(opts.Server, "master");
    Console.WriteLine($"Provisioning login '{opts.SqlLogin}'...");
    await SqlExecutor.ExecuteAsync(masterConnection, loginStatements);

    // ---- 5. Schema (creates the database if absent) -----------------------
    var adminConnection = ConnectionStrings.BuildWxManager(opts.Server, opts.Database);
    var dbOptions = new DbContextOptionsBuilder<WeatherDataContext>()
        .UseSqlServer(adminConnection)
        .Options;

    Console.WriteLine($"Ensuring schema in '{opts.Database}' (creates the database if it does not exist)...");
    try
    {
        await DatabaseSetup.EnsureSchemaAsync(dbOptions);
    }
    catch (Exception ex)
    {
        throw new SetupException($"Schema creation failed: {ex.Message}", ex);
    }

    // ---- 6. Database user + least-privilege roles -------------------------
    Console.WriteLine($"Mapping '{opts.SqlLogin}' into '{opts.Database}' with {string.Join(", ", SqlProvisioning.LeastPrivilegeRoles)}...");
    await SqlExecutor.ExecuteAsync(adminConnection, userStatements);

    // ---- 7. The five per-environment files (AC-5) -------------------------
    var plan = LocalFilesPlan.Build(opts, password, LocalFilesWriter.FileSystemExampleReader);
    var written = LocalFilesWriter.Flush(plan, path => Directory.CreateDirectory(path), File.WriteAllText);

    Console.WriteLine();
    Console.WriteLine("Wrote:");
    foreach (var path in written)
        Console.WriteLine($"  {path}");

    // ---- 8. Foundational Config rows (AC-6) -------------------------------
    await using (var db = new WeatherDataContext(dbOptions))
    {
        var rows = ConfigSeed.BuildFoundationalSeedRows(inputs);
        var outcome = await ConfigSeeder.UpsertAsync(db, rows, DateTime.UtcNow);
        Console.WriteLine();
        Console.WriteLine(
            $"Config seed: {outcome.Inserted} inserted, {outcome.Updated} updated, {outcome.Unchanged} unchanged.");
    }

    Console.WriteLine();
    Console.WriteLine("Setup complete.");
    return 0;
}

bool Confirm(string question)
{
    Console.Write($"{question} (yes/no): ");
    var answer = Console.ReadLine();
    return answer is not null
        && (answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
            || answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase));
}

// Reads without echoing. Falls back to ReadLine when stdin is redirected, where ReadKey is
// unavailable — that path is for scripted runs, which are not interactive to begin with.
string? ReadSecret()
{
    if (Console.IsInputRedirected)
        return Console.ReadLine();

    var buffer = new StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                Console.WriteLine();
                return buffer.ToString();
            case ConsoleKey.Backspace when buffer.Length > 0:
                buffer.Length--;
                break;
            default:
                if (!char.IsControl(key.KeyChar))
                    buffer.Append(key.KeyChar);
                break;
        }
    }
}