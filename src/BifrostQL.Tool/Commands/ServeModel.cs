using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Server;
using GraphQL.Types;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// The model and schema <c>bifrost serve</c> builds for a connection and config file, built by
/// serve's own <see cref="ProfileCacheBootstrapper"/>: the file's metadata rules, the enum
/// lookup preload and the default profile's schema. Commands that print or inspect the model
/// (schema, export) build through here, so they show what serve serves. config-generate is the
/// bootstrap tool, run before the file's rules match the database, so it builds without them.
/// serve binds only the connection string and the BifrostQL section, so there is no provider
/// override, metadata source, profile or operation to pass.
/// </summary>
internal static class ServeModel
{
    /// <summary>The endpoint path serve mounts when the config file names none.</summary>
    public const string DefaultEndpointPath = "/graphql";

    /// <summary>
    /// The model serve serves, with the config file's metadata rules. A rule the live schema
    /// cannot satisfy (a table or column that does not exist yet) fails <c>ModelConfigValidator</c>
    /// as serve's startup would: the refusal, with its message and a pointer to
    /// <c>bifrost config-validate</c>, is written to <paramref name="output"/> and null is returned.
    /// </summary>
    public static async Task<(IDbModel Model, ISchema Schema)?> BuildOrRefuseAsync(
        ResolvedConnection resolved, OutputFormatter output)
    {
        try
        {
            return await BuildAsync(resolved, resolved.ConfigSection?.Metadata ?? Array.Empty<string>());
        }
        catch (InvalidOperationException ex)
        {
            const string hint = "Run 'bifrost config-validate' to check the config file's rules against the database.";
            if (output.IsJsonMode)
            {
                output.WriteJson(new { success = false, error = ex.Message, hint });
                return null;
            }
            output.WriteError(ex.Message);
            output.WriteInfo($"  {hint}");
            return null;
        }
    }

    /// <summary>
    /// The model of the database alone, without the config file's metadata rules: what
    /// config-generate detects patterns in. The rules it proposes may name tables the file's
    /// current rules hide, or the file may name tables that do not exist yet.
    /// </summary>
    public static async Task<IDbModel> BuildWithoutConfigRulesAsync(ResolvedConnection resolved)
        => (await BuildAsync(resolved, Array.Empty<string>())).Model;

    private static async Task<(IDbModel Model, ISchema Schema)> BuildAsync(
        ResolvedConnection resolved, IReadOnlyList<string> metadataRules)
    {
        var inputs = await ProfileCacheBootstrapper.BuildInputsAsync(
            (resolved.ConfigSection?.Path ?? DefaultEndpointPath).ToLowerInvariant(),
            resolved.ConnectionString,
            providerName: null,
            metadataRules,
            Array.Empty<IMetadataSource>(),
            new BifrostProfileRegistry());
        return ((IDbModel)inputs["model"]!, (ISchema)inputs["dbSchema"]!);
    }
}
