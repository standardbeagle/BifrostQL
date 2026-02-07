using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using GraphQL.Utilities;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// Introspects the database and prints the GraphQL schema in SDL format.
/// </summary>
public sealed class SchemaCommand : ICommand
{
    public string Name => "schema";
    public string Description => "Print the GraphQL schema (SDL) for the database";

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
            var metadataLoader = new MetadataLoader(Array.Empty<string>());
            var loader = new DbModelLoader(connectionString, metadataLoader);
            var model = await loader.LoadAsync();

            var schema = DbSchema.FromModel(model);
            var sdl = new SchemaPrinter(schema).Print();

            if (output.IsJsonMode)
            {
                output.WriteJson(new
                {
                    success = true,
                    tableCount = model.Tables.Count,
                    sdl,
                });
                return 0;
            }

            output.WriteSuccess($"Schema generated ({model.Tables.Count} tables)");
            output.WriteInfo("");
            output.WritePlain(sdl);
            return 0;
        }
        catch (SqlException ex)
        {
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = ex.Message });
                return 1;
            }
            output.WriteError($"Schema generation failed: {ex.Message}");
            return 1;
        }
    }
}
