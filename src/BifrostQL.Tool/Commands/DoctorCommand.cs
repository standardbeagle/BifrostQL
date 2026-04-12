using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Diagnoses common BifrostQL configuration and connection issues.
/// </summary>
public sealed class DoctorCommand : ICommand
{
    public string Name => "doctor";
    public string Description => "Diagnose configuration and connection issues";

    public async Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        var issues = new List<DoctorIssue>();
        var checks = new List<DoctorCheck>();

        // Check 1: Connection string provided
        var connectionString = config.ConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // Try to load from config file
            var configPath = config.ConfigPath ?? Path.Combine(Directory.GetCurrentDirectory(), "bifrostql.json");
            if (File.Exists(configPath))
            {
                checks.Add(new DoctorCheck("Config File", DoctorStatus.Info, $"Found config file: {configPath}"));
                var json = await File.ReadAllTextAsync(configPath);
                connectionString = ConfigFileReader.ReadConnectionString(json);
                if (!string.IsNullOrWhiteSpace(connectionString))
                {
                    checks.Add(new DoctorCheck("Connection String", DoctorStatus.Pass, "Loaded from config file"));
                }
            }
            else
            {
                checks.Add(new DoctorCheck("Connection String", DoctorStatus.Fail, "No connection string provided"));
                issues.Add(new DoctorIssue(
                    DoctorSeverity.Error,
                    "Missing Connection String",
                    "No database connection string was provided.",
                    new[] { "Use --connection-string to provide a connection string", "Or create a bifrostql.json config file with 'bifrost init'" }
                ));
            }
        }
        else
        {
            checks.Add(new DoctorCheck("Connection String", DoctorStatus.Pass, "Provided via command line"));
        }

        // Check 2: Parse connection string
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                var builder = new SqlConnectionStringBuilder(connectionString);
                checks.Add(new DoctorCheck("Connection String Format", DoctorStatus.Pass, "Valid SQL Server connection string format"));

                // Check for common issues
                if (string.IsNullOrWhiteSpace(builder.InitialCatalog))
                {
                    checks.Add(new DoctorCheck("Database Name", DoctorStatus.Warning, "No database specified in connection string"));
                    issues.Add(new DoctorIssue(
                        DoctorSeverity.Warning,
                        "No Database Specified",
                        "The connection string does not specify a database name (InitialCatalog).",
                        new[] { "Add 'Database=YourDatabaseName' to your connection string" }
                    ));
                }

                if (string.IsNullOrWhiteSpace(builder.DataSource))
                {
                    checks.Add(new DoctorCheck("Server Name", DoctorStatus.Fail, "No server specified in connection string"));
                    issues.Add(new DoctorIssue(
                        DoctorSeverity.Error,
                        "No Server Specified",
                        "The connection string does not specify a server name.",
                        new[] { "Add 'Server=YourServerName' to your connection string" }
                    ));
                }

                // Check authentication
                if (!builder.IntegratedSecurity && string.IsNullOrWhiteSpace(builder.UserID))
                {
                    checks.Add(new DoctorCheck("Authentication", DoctorStatus.Warning, "No authentication method specified"));
                    issues.Add(new DoctorIssue(
                        DoctorSeverity.Warning,
                        "Authentication Not Configured",
                        "The connection string doesn't specify authentication credentials.",
                        new[] { "Use 'Integrated Security=True' for Windows auth", "Or provide User ID and Password for SQL auth" }
                    ));
                }
            }
            catch (ArgumentException ex)
            {
                checks.Add(new DoctorCheck("Connection String Format", DoctorStatus.Fail, $"Invalid format: {ex.Message}"));
                issues.Add(new DoctorIssue(
                    DoctorSeverity.Error,
                    "Invalid Connection String",
                    "The connection string format is invalid.",
                    new[] { "Check for typos in connection string keywords", "Ensure values are properly quoted if they contain special characters" }
                ));
            }
        }

        // Check 3: Test database connection
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            checks.Add(new DoctorCheck("Database Connection", DoctorStatus.Info, "Testing connection..."));
            
            try
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                checks.Add(new DoctorCheck("Database Connection", DoctorStatus.Pass, $"Connected to {connection.Database} on {connection.DataSource}"));
                
                // Check permissions
                try
                {
                    await using var cmd = connection.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'";
                    var tableCount = await cmd.ExecuteScalarAsync();
                    checks.Add(new DoctorCheck("Schema Access", DoctorStatus.Pass, $"Can read schema ({tableCount} tables found)"));
                }
                catch (Exception ex)
                {
                    checks.Add(new DoctorCheck("Schema Access", DoctorStatus.Warning, $"Limited permissions: {ex.Message}"));
                    issues.Add(new DoctorIssue(
                        DoctorSeverity.Warning,
                        "Limited Schema Access",
                        "Connected but cannot fully read database schema.",
                        new[] { "Ensure the user has permissions to read INFORMATION_SCHEMA", "Some features may not work correctly" }
                    ));
                }
            }
            catch (SqlException ex) when (ex.Number == 18456)
            {
                checks.Add(new DoctorCheck("Database Connection", DoctorStatus.Fail, "Login failed"));
                issues.Add(new DoctorIssue(
                    DoctorSeverity.Error,
                    "Database Login Failed",
                    "The database credentials are incorrect.",
                    new[] { "Verify the username and password", "Check that SQL Server authentication is enabled", "Ensure the login exists in the database" }
                ));
            }
            catch (SqlException ex) when (ex.Number == 4060)
            {
                checks.Add(new DoctorCheck("Database Connection", DoctorStatus.Fail, "Database not found"));
                issues.Add(new DoctorIssue(
                    DoctorSeverity.Error,
                    "Database Not Found",
                    "The specified database does not exist.",
                    new[] { "Verify the database name in the connection string", "Create the database if it doesn't exist" }
                ));
            }
            catch (SqlException ex) when (ex.Number == 53 || ex.Number == 258)
            {
                checks.Add(new DoctorCheck("Database Connection", DoctorStatus.Fail, "Cannot reach server"));
                issues.Add(new DoctorIssue(
                    DoctorSeverity.Error,
                    "Cannot Connect to Server",
                    "Unable to reach the database server.",
                    new[] { "Verify the server name/IP address is correct", "Check that the SQL Server service is running", "Ensure firewall rules allow the connection", "Verify the port number if non-standard" }
                ));
            }
            catch (Exception ex)
            {
                checks.Add(new DoctorCheck("Database Connection", DoctorStatus.Fail, ex.Message));
                issues.Add(new DoctorIssue(
                    DoctorSeverity.Error,
                    "Connection Error",
                    $"An error occurred while connecting: {ex.Message}",
                    new[] { "Check the detailed error message", "Verify network connectivity" }
                ));
            }
        }

        // Check 4: Config file validation
        var bifrostConfigPath = config.ConfigPath ?? Path.Combine(Directory.GetCurrentDirectory(), "bifrostql.json");
        if (File.Exists(bifrostConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(bifrostConfigPath);
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                checks.Add(new DoctorCheck("Config File", DoctorStatus.Pass, "Valid JSON format"));

                var rules = ConfigFileReader.ReadMetadataRules(json);
                if (rules.Count > 0)
                {
                    checks.Add(new DoctorCheck("Metadata Rules", DoctorStatus.Info, $"{rules.Count} rules found"));
                }
            }
            catch (System.Text.Json.JsonException ex)
            {
                checks.Add(new DoctorCheck("Config File", DoctorStatus.Fail, $"Invalid JSON: {ex.Message}"));
                issues.Add(new DoctorIssue(
                    DoctorSeverity.Error,
                    "Invalid Config File",
                    "The bifrostql.json file contains invalid JSON.",
                    new[] { "Check for syntax errors in the JSON file", "Validate the JSON using a JSON linter" }
                ));
            }
        }

        // Output results
        if (output.IsJsonMode)
        {
            output.WriteJson(new
            {
                success = !issues.Any(i => i.Severity == DoctorSeverity.Error),
                checkCount = checks.Count,
                issueCount = issues.Count,
                errorCount = issues.Count(i => i.Severity == DoctorSeverity.Error),
                warningCount = issues.Count(i => i.Severity == DoctorSeverity.Warning),
                checks = checks.Select(c => new { name = c.Name, status = c.Status.ToString(), message = c.Message }),
                issues = issues.Select(i => new { severity = i.Severity.ToString(), title = i.Title, message = i.Message, suggestions = i.Suggestions }),
            });
        }
        else
        {
            output.WriteHeader("BifrostQL Doctor");
            output.WriteInfo("");

            foreach (var check in checks)
            {
                var symbol = check.Status switch
                {
                    DoctorStatus.Pass => "✓",
                    DoctorStatus.Fail => "✗",
                    DoctorStatus.Warning => "⚠",
                    DoctorStatus.Info => "ℹ",
                    _ => "?"
                };
                
                var color = check.Status switch
                {
                    DoctorStatus.Pass => AnsiColor.Green,
                    DoctorStatus.Fail => AnsiColor.Red,
                    DoctorStatus.Warning => AnsiColor.Yellow,
                    _ => AnsiColor.Cyan
                };
                
                output.WriteInfo($"  {output.Colorize(symbol, color)} {check.Name}: {check.Message}");
            }

            if (issues.Count > 0)
            {
                output.WriteInfo("");
                output.WriteHeader("Issues Found");
                output.WriteInfo("");

                foreach (var issue in issues)
                {
                    var color = issue.Severity switch
                    {
                        DoctorSeverity.Error => AnsiColor.Red,
                        DoctorSeverity.Warning => AnsiColor.Yellow,
                        _ => AnsiColor.Cyan
                    };
                    
                    output.WriteInfo(output.Colorize($"  {issue.Title}", color));
                    output.WriteInfo($"    {issue.Message}");
                    
                    if (issue.Suggestions?.Count > 0)
                    {
                        output.WriteInfo("");
                        output.WriteInfo("    Suggestions:");
                        foreach (var suggestion in issue.Suggestions)
                        {
                            output.WriteInfo($"      • {suggestion}");
                        }
                    }
                    output.WriteInfo("");
                }
            }
            else
            {
                output.WriteInfo("");
                output.WriteSuccess("All checks passed! No issues found.");
            }
        }

        return issues.Any(i => i.Severity == DoctorSeverity.Error) ? 1 : 0;
    }
}

internal enum DoctorStatus { Pass, Fail, Warning, Info }
internal enum DoctorSeverity { Error, Warning, Info }

internal sealed record DoctorCheck(string Name, DoctorStatus Status, string Message);
internal sealed record DoctorIssue(DoctorSeverity Severity, string Title, string Message, IReadOnlyList<string>? Suggestions = null);
