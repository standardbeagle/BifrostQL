using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Server;
using GraphQL.Types;

namespace BifrostQL.Tool.Commands;

/// <summary>
/// The model and schema <c>bifrost serve</c> builds for a connection and config file, built by
/// serve's own <see cref="ProfileCacheBootstrapper"/>: the file's metadata rules, the enum
/// lookup preload and the default profile's schema. Commands that print or inspect the model
/// (schema, export, config-generate) build through here, so they show what serve serves.
/// serve binds only the connection string and the BifrostQL section, so there is no provider
/// override, metadata source, profile or operation to pass.
/// </summary>
internal static class ServeModel
{
    /// <summary>The endpoint path serve mounts when the config file names none.</summary>
    public const string DefaultEndpointPath = "/graphql";

    public static async Task<(IDbModel Model, ISchema Schema)> BuildAsync(ResolvedConnection resolved)
    {
        var inputs = await ProfileCacheBootstrapper.BuildInputsAsync(
            (resolved.ConfigSection?.Path ?? DefaultEndpointPath).ToLowerInvariant(),
            resolved.ConnectionString,
            providerName: null,
            resolved.ConfigSection?.Metadata ?? Array.Empty<string>(),
            Array.Empty<IMetadataSource>(),
            new BifrostProfileRegistry());
        return ((IDbModel)inputs["model"]!, (ISchema)inputs["dbSchema"]!);
    }
}
