using System.Diagnostics;
using BifrostQL.Core.Model;
using BifrostQL.Model;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Watches for database schema changes and optionally reloads the server.
/// </summary>
public sealed class WatchCommand : ICommand
{
    public string Name => "watch";
    public string Description => "Watch for database schema changes and reload";

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

        var interval = 5000; // 5 seconds default
        if (config.CommandArgs.Length > 0 && int.TryParse(config.CommandArgs[0], out var customInterval))
        {
            interval = customInterval * 1000;
        }

        if (!output.IsJsonMode)
        {
            output.WriteHeader("BifrostQL Schema Watcher");
            output.WriteInfo($"  Checking for schema changes every {interval / 1000}s...");
            output.WriteInfo($"  Press Ctrl+C to stop.");
            output.WriteInfo("");
        }

        var cancellationToken = new CancellationTokenSource();
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            cancellationToken.Cancel();
        };

        string? lastSchemaHash = null;
        var changeCount = 0;

        try
        {
            while (!cancellationToken.Token.IsCancellationRequested)
            {
                try
                {
                    var currentHash = await GetSchemaHashAsync(connectionString);
                    
                    if (lastSchemaHash == null)
                    {
                        lastSchemaHash = currentHash;
                        if (!output.IsJsonMode)
                        {
                            output.WriteInfo($"[{DateTime.Now:HH:mm:ss}] Initial schema loaded.");
                        }
                    }
                    else if (lastSchemaHash != currentHash)
                    {
                        changeCount++;
                        lastSchemaHash = currentHash;
                        
                        if (output.IsJsonMode)
                        {
                            output.WriteJson(new
                            {
                                timestamp = DateTime.UtcNow,
                                eventType = "schema_changed",
                                changeCount,
                            });
                        }
                        else
                        {
                            output.WriteWarning($"[{DateTime.Now:HH:mm:ss}] Schema change detected! ({changeCount} changes so far)");
                            output.WriteInfo("  The database schema has changed.");
                            output.WriteInfo("  Restart the BifrostQL server to pick up changes.");
                            output.WriteInfo("");
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (output.IsJsonMode)
                    {
                        output.WriteJson(new
                        {
                            timestamp = DateTime.UtcNow,
                            eventType = "error",
                            error = ex.Message,
                        });
                    }
                    else
                    {
                        output.WriteError($"[{DateTime.Now:HH:mm:ss}] Error checking schema: {ex.Message}");
                    }
                }

                try
                {
                    await Task.Delay(interval, cancellationToken.Token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        if (!output.IsJsonMode)
        {
            output.WriteInfo("");
            output.WriteInfo($"Watcher stopped. Detected {changeCount} schema change(s).");
        }

        return 0;
    }

    private static async Task<string> GetSchemaHashAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        // Get a hash of the current schema state
        var hashParts = new List<string>();

        // Table count and names
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, DATA_TYPE 
                FROM INFORMATION_SCHEMA.COLUMNS 
                WHERE TABLE_TYPE = 'BASE TABLE'
                ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";
            
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var column = reader.GetString(2);
                var type = reader.GetString(3);
                hashParts.Add($"{schema}.{table}.{column}:{type}");
            }
        }

        // Foreign keys
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT 
                    OBJECT_SCHEMA_NAME(parent_object_id) + '.' + OBJECT_NAME(parent_object_id),
                    COL_NAME(parent_object_id, parent_column_id),
                    OBJECT_SCHEMA_NAME(referenced_object_id) + '.' + OBJECT_NAME(referenced_object_id),
                    COL_NAME(referenced_object_id, referenced_column_id)
                FROM sys.foreign_key_columns
                ORDER BY constraint_object_id";
            
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                hashParts.Add($"FK:{reader.GetString(0)}.{reader.GetString(1)}->{reader.GetString(2)}.{reader.GetString(3)}");
            }
        }

        // Compute hash
        var combined = string.Join("|", hashParts);
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(hash);
    }
}
