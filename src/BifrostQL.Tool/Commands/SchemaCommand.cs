using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using GraphQL.Utilities;

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
        // --connection-string wins; otherwise the config file supplies it. A config file that
        // exists is refused here when serve would refuse it.
        var resolved = await CommandConnection.ResolveAsync(config, output);
        if (resolved is null)
            return 1;
        var connectionString = resolved.Value.ConnectionString;

        try
        {
            var metadataLoader = new MetadataLoader(Array.Empty<string>());
            var loader = new DbModelLoader(connectionString, metadataLoader);
            var model = await loader.LoadAsync();

            var schema = DbSchema.FromModel(model);
#pragma warning disable CS0618 // SchemaPrinter is the available printer API in this target package set.
            var sdl = new SchemaPrinter(schema).Print();
#pragma warning restore CS0618

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
        catch (DbException ex)
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
