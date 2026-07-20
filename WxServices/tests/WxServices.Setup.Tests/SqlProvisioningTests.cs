using System;
using System.Linq;

using WxServices.Setup;

using Xunit;

namespace WxServices.Setup.Tests;

/// <summary>
/// WX-314 AC-4, test-first: the SQL provisioning statements — identifier/literal quoting, the
/// idempotency guards, and the guarantee that the password never reaches the printed script. Pure:
/// these build text only; executing it is the (separately verified) side-effecting step.
/// </summary>
public class SqlProvisioningTests
{
    private const string Login = "wxservicestest";
    private const string Password = "p@ss'w0rd";   // deliberately contains a quote

    // ---- quoting ----------------------------------------------------------

    [Fact]
    public void QuoteIdentifier_Brackets() =>
        Assert.Equal("[wxservices]", SqlProvisioning.QuoteIdentifier("wxservices"));

    /// <summary>A <c>]</c> inside an identifier must be doubled or it closes the bracket early.</summary>
    [Fact]
    public void QuoteIdentifier_DoublesClosingBracket() =>
        // Both ']' characters double: we][ird]  ->  we]][ird]]  ->  [we]][ird]]]
        Assert.Equal("[we]][ird]]]", SqlProvisioning.QuoteIdentifier("we][ird]"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void QuoteIdentifier_RejectsBlank(string? name) =>
        Assert.Throws<ArgumentException>(() => SqlProvisioning.QuoteIdentifier(name!));

    /// <summary>A <c>'</c> inside a literal must be doubled or it terminates the string.</summary>
    [Fact]
    public void QuoteLiteral_DoublesSingleQuote() =>
        Assert.Equal("N'p@ss''w0rd'", SqlProvisioning.QuoteLiteral(Password));

    // ---- login (server level) ---------------------------------------------

    [Fact]
    public void LoginStatements_CreateIsGuardedAndAlterUpdatesPassword()
    {
        var sql = string.Join("\n", SqlProvisioning.BuildLoginStatements(Login, Password).Select(s => s.Sql));

        Assert.Contains("sys.server_principals", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE LOGIN [wxservicestest]", sql, StringComparison.Ordinal);
        // Re-running must reconcile the password with what we write into the config files,
        // so an existing login is ALTERed rather than skipped.
        Assert.Contains("ALTER LOGIN [wxservicestest]", sql, StringComparison.Ordinal);
        Assert.Contains("N'p@ss''w0rd'", sql, StringComparison.Ordinal);
    }

    /// <summary>The operator sees the script before confirming — it must not leak the password.</summary>
    [Fact]
    public void LoginStatements_DisplayMasksThePassword()
    {
        var display = string.Join("\n", SqlProvisioning.BuildLoginStatements(Login, Password).Select(s => s.Display));

        Assert.DoesNotContain("p@ss", display, StringComparison.Ordinal);
        Assert.Contains("CREATE LOGIN [wxservicestest]", display, StringComparison.Ordinal);
        Assert.Contains("********", display, StringComparison.Ordinal);
    }

    // ---- database user + roles --------------------------------------------

    [Fact]
    public void UserStatements_GuardUserCreationAndEachRoleAdd()
    {
        var sql = string.Join("\n", SqlProvisioning
            .BuildUserStatements(Login, SqlProvisioning.LeastPrivilegeRoles)
            .Select(s => s.Sql));

        Assert.Contains("sys.database_principals", sql, StringComparison.Ordinal);
        Assert.Contains("CREATE USER [wxservicestest] FOR LOGIN [wxservicestest]", sql, StringComparison.Ordinal);

        foreach (var role in SqlProvisioning.LeastPrivilegeRoles)
        {
            // IS_ROLEMEMBER keeps a re-run from erroring on an already-present member; the role
            // name is a quoted literal there, not raw interpolation.
            Assert.Contains($"IS_ROLEMEMBER(N'{role}'", sql, StringComparison.Ordinal);
            Assert.Contains($"ALTER ROLE [{role}] ADD MEMBER [wxservicestest]", sql, StringComparison.Ordinal);
        }
    }

    /// <summary>db_ddladmin is required because every service runs EF migrations at startup.</summary>
    [Fact]
    public void LeastPrivilegeRoles_SupportMigrationsWithoutOwnership()
    {
        Assert.Equal(
            new[] { "db_datareader", "db_datawriter", "db_ddladmin" },
            SqlProvisioning.LeastPrivilegeRoles);
        Assert.DoesNotContain("db_owner", SqlProvisioning.LeastPrivilegeRoles);
    }

    [Fact]
    public void UserStatements_DisplayEqualsSql_NoSecretsInvolved()
    {
        foreach (var statement in SqlProvisioning.BuildUserStatements(Login, SqlProvisioning.LeastPrivilegeRoles))
            Assert.Equal(statement.Sql, statement.Display);
    }
}