using System.Text.Json;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Creates a default bifrostql.json configuration file in the current directory.
/// </summary>
public sealed class InitCommand : ICommand
{
    public string Name => "init";
    public string Description => "Create a default bifrostql.json config file";

    internal static readonly object DefaultConfig = new
    {
        ConnectionStrings = new
        {
            bifrost = "Server=localhost;Database=mydb;User Id=sa;Password=yourpassword;TrustServerCertificate=True",
        },
        BifrostQL = new
        {
            DisableAuth = true,
            Path = "/graphql",
            Playground = "/graphiql",
            Metadata = Array.Empty<string>(),
        },
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public Task<int> ExecuteAsync(ToolConfig config, OutputFormatter output)
    {
        var targetPath = Path.Combine(Directory.GetCurrentDirectory(), "bifrostql.json");

        if (File.Exists(targetPath))
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = "bifrostql.json already exists", path = targetPath });
                return Task.FromResult(1);
            }

            output.WriteError("bifrostql.json already exists in the current directory.");
            output.WriteInfo($"  Path: {targetPath}");
            return Task.FromResult(1);
        }

        var json = JsonSerializer.Serialize(DefaultConfig, WriteOptions);
        File.WriteAllText(targetPath, json);

        if (output.IsJsonMode)
        {
            output.WriteJson(new { success = true, path = targetPath });
            return Task.FromResult(0);
        }

        output.WriteSuccess("Created bifrostql.json");
        output.WriteInfo($"  Path: {targetPath}");
        output.WriteInfo("  Edit the connection string and run 'bifrost test' to verify.");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Generates the default config JSON string (for testing).
    /// </summary>
    public static string GenerateDefaultConfigJson()
    {
        return JsonSerializer.Serialize(DefaultConfig, WriteOptions);
    }
}
