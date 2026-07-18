namespace WxServices.Setup;

/// <summary>
/// One provisioning statement: the <see cref="Sql"/> actually executed, and the <see cref="Display"/>
/// form shown to the operator for confirmation (identical unless it carries a secret).
/// </summary>
public sealed record ProvisioningStatement(string Sql, string Display);

/// <summary>
/// Builds the SQL that provisions the services' login (WX-314, AC-4). Pure — it produces text only;
/// executing it is the side-effecting step, verified by the functional run in
/// <c>docs/test-procedures/WX-314.md</c>.
///
/// <para>Two batches, because they run either side of schema creation: the login is server-level and
/// is created first, but <c>CREATE USER</c> needs the database to exist — and the database is created
/// by <c>EnsureSchemaAsync</c>'s migration. So the order is: login → EnsureSchema → user + roles.</para>
///
/// <para>Every statement is guarded so a re-run is a no-op rather than an error (AC-4 idempotency).</para>
/// </summary>
public static class SqlProvisioning
{
    private const string MaskedPassword = "********";

    /// <summary>
    /// The roles granted to the services' login. Deliberately <em>not</em> <c>db_owner</c>:
    /// <c>db_ddladmin</c> is required because every service runs EF migrations at startup, but
    /// permission- and ownership-management rights are withheld.
    /// </summary>
    public static readonly IReadOnlyList<string> LeastPrivilegeRoles =
        new[] { "db_datareader", "db_datawriter", "db_ddladmin" };

    /// <summary>Bracket-quotes an identifier, doubling any <c>]</c> so it cannot close the bracket early.</summary>
    public static string QuoteIdentifier(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Identifier cannot be blank.", nameof(name));
        return $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    /// <summary>Quotes a Unicode string literal, doubling any <c>'</c> so it cannot terminate the string.</summary>
    public static string QuoteLiteral(string value) =>
        $"N'{(value ?? string.Empty).Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// Server-level: create the login, or reset its password if it already exists. An existing login
    /// is ALTERed rather than skipped so the password always matches the one written into the
    /// generated <c>appsettings.local.json</c> files — otherwise a re-run would leave the files
    /// claiming a password the server no longer accepts.
    /// </summary>
    public static IReadOnlyList<ProvisioningStatement> BuildLoginStatements(string login, string password)
    {
        var quotedLogin = QuoteIdentifier(login);
        var loginLiteral = QuoteLiteral(login);

        string Sql(string passwordLiteral) => $"""
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = {loginLiteral})
                CREATE LOGIN {quotedLogin} WITH PASSWORD = {passwordLiteral};
            ELSE
                ALTER LOGIN {quotedLogin} WITH PASSWORD = {passwordLiteral};
            """;

        return new[]
        {
            new ProvisioningStatement(Sql(QuoteLiteral(password)), Sql($"N'{MaskedPassword}'")),
        };
    }

    /// <summary>
    /// Database-level (run after the schema exists): map the login to a database user and add it to
    /// each role. <c>IS_ROLEMEMBER</c> guards keep a re-run from erroring on an existing membership.
    /// </summary>
    public static IReadOnlyList<ProvisioningStatement> BuildUserStatements(
        string login, IReadOnlyList<string> roles)
    {
        var quotedLogin = QuoteIdentifier(login);
        var loginLiteral = QuoteLiteral(login);
        var statements = new List<ProvisioningStatement>
        {
            Plain($"""
                IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = {loginLiteral})
                    CREATE USER {quotedLogin} FOR LOGIN {quotedLogin};
                """),
        };

        foreach (var role in roles)
        {
            statements.Add(Plain($"""
                IF IS_ROLEMEMBER('{role}', {loginLiteral}) = 0
                    ALTER ROLE {QuoteIdentifier(role)} ADD MEMBER {quotedLogin};
                """));
        }

        return statements;

        // No secrets in these, so what we show is exactly what we run.
        static ProvisioningStatement Plain(string sql) => new(sql, sql);
    }
}