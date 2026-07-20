// WxServices.Setup — first-time setup console (WX-314).
//
// Order matters and is not arbitrary: the login is server-level and must exist before anything
// authenticates as it; CREATE USER needs the database, which does not exist until EnsureSchemaAsync
// runs its migrations; and the Config seed needs the schema. Files are written only once the
// database work has succeeded, so a failed run does not leave configuration on disk pointing at a
// login or database that was never created.
//
//   prerequisites -> prompt -> confirm -> PLAN the 5 files (pure; validates the templates)
//                 -> CREATE LOGIN -> EnsureSchema -> CREATE USER + roles
//                 -> flush the 5 appsettings.local.json -> seed Config
//
// Nothing here elevates: host-level configuration that needs elevation (Mixed-Mode auth, TCP) is
// detected by the prerequisite gate and reported, never performed.
using System.Text;

using MetarParser.Data;

using Microsoft.EntityFrameworkCore;

using WxServices.Setup;

var installRootDefault = Environment.GetEnvironmentVariable("WXSERVICES_INSTALL_ROOT") ?? @"C:\HarderWare";
var servicesDirDefault = Environment.GetEnvironmentVariable("WXSERVICES_SERVICES_DIR")
    ?? ResolveServicesDir();

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

    // ---- 3a. Build the file plan BEFORE any mutation ----------------------
    // Building the plan reads every committed .example template, so a missing or unreadable
    // template fails here — while the instance is still untouched. Doing this after the DB work
    // (as an earlier cut did) could leave a provisioned login and a fully migrated database with
    // no config files at all: exactly the half-configured state the write-after-DB order exists to
    // avoid. The plan is pure, so building early costs nothing and buys the guarantee.
    var plan = LocalFilesPlan.Build(opts, password, LocalFilesWriter.FileSystemExampleReader);

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

    // Steps 7-8 are wrapped like steps 4-6: they are the last mutating steps, so a disk-full,
    // permission, or DbUpdateException failure here lands at the worst possible moment — after the
    // login, schema, and user are already provisioned — and must still report as a plain
    // actionable message rather than a stack trace.

    // ---- 7. Flush the five per-environment files (AC-5) --------------------
    // Planned in step 3a; only the disk write happens here, after the database work succeeded.
    IReadOnlyList<string> written;
    try
    {
        written = LocalFilesWriter.Flush(plan, path => Directory.CreateDirectory(path), File.WriteAllText);
    }
    catch (Exception ex) when (ex is not SetupException)
    {
        throw new SetupException(
            $"Writing the local configuration files failed: {ex.Message}{Environment.NewLine}" +
            "The SQL login and schema were already provisioned — fix the cause and re-run; setup is idempotent.",
            ex);
    }

    Console.WriteLine();
    Console.WriteLine("Wrote:");
    foreach (var path in written)
        Console.WriteLine($"  {path}");

    // ---- 8. Foundational Config rows (AC-6) -------------------------------
    await using (var db = new WeatherDataContext(dbOptions))
    {
        var rows = ConfigSeed.BuildFoundationalSeedRows(inputs);
        SeedOutcome outcome;
        try
        {
            // SetupException is already actionable (the bootstrap-key and duplicate-key guards) —
            // re-wrapping it would bury its message inside a second one.
            outcome = await ConfigSeeder.UpsertAsync(db, rows, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not SetupException)
        {
            throw new SetupException(
                $"Seeding the Config table failed: {ex.Message}{Environment.NewLine}" +
                "The login, schema, and configuration files are already in place — fix the cause and re-run.",
                ex);
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Config seed: {outcome.Inserted} inserted, {outcome.Updated} updated, {outcome.Unchanged} unchanged.");
    }

    Console.WriteLine();
    Console.WriteLine("Setup complete.");
    return 0;
}

// services/ lives at the repository root, but this console is normally launched from the
// WxServices project directory ("dotnet run --project src\WxServices.Setup" only resolves from
// there), where cwd\services does not exist. Check the working directory first, then its parent,
// so the obvious invocation works from either location; --services-dir still overrides both.
string ResolveServicesDir()
{
    var cwd = Directory.GetCurrentDirectory();
    var here = Path.Combine(cwd, "services");
    if (Directory.Exists(here))
        return here;

    var parent = Directory.GetParent(cwd)?.FullName;
    if (parent is not null)
    {
        var up = Path.Combine(parent, "services");
        if (Directory.Exists(up))
            return up;
    }

    return here;   // report the working-directory candidate in the gate's "not found" message
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