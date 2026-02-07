using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Tests database connectivity and reports table/column counts.
/// </summary>
public sealed class TestCommand : ICommand
{
    public string Name => "test";
    public string Description => "Test database connection and report schema summary";

    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        var connectionString = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = "Connection string is required. Use --connection-string." });
                return 1;
            }
            output.WriteError("Connection string is required. Use --connection-string.");
            return 1;
        }

        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var serverVersion = connection.ServerVersion;
            var database = connection.Database;

            var tableCount = await CountAsync(connection,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'");
            var columnCount = await CountAsync(connection,
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS c INNER JOIN INFORMATION_SCHEMA.TABLES t ON c.TABLE_NAME = t.TABLE_NAME AND c.TABLE_SCHEMA = t.TABLE_SCHEMA WHERE t.TABLE_TYPE = 'BASE TABLE'");

            if (output.IsJsonMode)
            {
                output.WriteJson(new
                {
                    success = true,
                    database,
                    serverVersion,
                    tableCount,
                    columnCount,
                });
                return 0;
            }

            output.WriteSuccess("Connection successful");
            output.WriteInfo($"  Database:       {database}");
            output.WriteInfo($"  Server version: {serverVersion}");
            output.WriteInfo($"  Tables:         {tableCount}");
            output.WriteInfo($"  Columns:        {columnCount}");
            return 0;
        }
        catch (SqlException ex)
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = ex.Message });
                return 1;
            }
            output.WriteError($"Connection failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> CountAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        var result = await cmd.ExecuteScalarAsync();
        return result is int count ? count : Convert.ToInt32(result);
    }
}
