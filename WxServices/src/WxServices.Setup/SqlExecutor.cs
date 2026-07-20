using Microsoft.Data.SqlClient;

namespace WxServices.Setup;

/// <summary>
/// Executes provisioning statements over a Windows-Trusted connection (WX-314). Thin by design:
/// every decision about <em>what</em> to run lives in <see cref="SqlProvisioning"/>, which is
/// unit-tested; this only runs it.
/// </summary>
public static class SqlExecutor
{
    /// <summary>
    /// Runs each statement in order on one connection. A failure is wrapped in a
    /// <see cref="SetupException"/> naming the statement that failed, so the operator sees which
    /// step to fix rather than a bare SQL error number.
    /// </summary>
    public static async Task ExecuteAsync(
        string connectionString,
        IReadOnlyList<ProvisioningStatement> statements,
        CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(connectionString);

        // Opening is inside the guard too: the server can go away between the prerequisite gate and
        // this call (the operator is typing answers in between), and an unwrapped SqlException here
        // would escape Program.cs's SetupException handler as a raw stack trace.
        try
        {
            await connection.OpenAsync(ct);
        }
        catch (SqlException ex)
        {
            throw new SetupException(
                $"Could not connect to SQL Server to run the provisioning statements.{Environment.NewLine}" +
                $"Server said: {ex.Message}", ex);
        }

        foreach (var statement in statements)
        {
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = statement.Sql;
                await command.ExecuteNonQueryAsync(ct);
            }
            catch (SqlException ex)
            {
                // Report the Display form — it is the masked one when the statement carries a secret.
                throw new SetupException(
                    $"SQL provisioning failed.{Environment.NewLine}" +
                    $"Statement:{Environment.NewLine}{statement.Display}{Environment.NewLine}" +
                    $"Server said: {ex.Message}", ex);
            }
        }
    }
}