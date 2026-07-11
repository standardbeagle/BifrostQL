using BifrostQL.Core.Schema;
using GraphQL;

namespace BifrostQL.Core.Resolvers;

/// <summary>
/// Endpoint resolution shared by the intent executors
/// (<see cref="QueryIntentExecutor"/>, <see cref="MutationIntentExecutor"/>):
/// resolves an endpoint path to the same cached <see cref="Inputs"/> the HTTP
/// middleware and binary transport key their schemas by, and extracts required
/// entries from it. Fails fast — never a silent fallback to a different database.
/// </summary>
internal static class IntentEndpointResolver
{
    /// <summary>
    /// Resolves the endpoint's cached Inputs. A named endpoint must be registered
    /// (case-insensitive, matching the middleware's lowercase keying); a null
    /// endpoint is only valid when exactly one endpoint exists — with several
    /// registered, guessing could silently target the wrong database.
    /// </summary>
    public static async Task<Inputs> ResolveAsync(PathCache<Inputs> endpoints, string? endpoint)
    {
        if (endpoint is not null)
        {
            var key = endpoint.ToLowerInvariant();
            if (!endpoints.HasPath(key))
                throw new BifrostExecutionError($"Unknown BifrostQL endpoint '{endpoint}'.");
            return await endpoints.GetValueAsync(key);
        }

        if (endpoints.Count == 0)
            throw new BifrostExecutionError("No BifrostQL endpoints are registered.");
        if (endpoints.Count > 1)
            throw new BifrostExecutionError(
                "Multiple BifrostQL endpoints are registered; the intent's Endpoint is required.");

        return await endpoints.GetFirstValueAsync()
            ?? throw new BifrostExecutionError("No BifrostQL endpoints are registered.");
    }

    public static T GetRequired<T>(Inputs inputs, string key, string? endpoint) where T : class
    {
        if (!inputs.TryGetValue(key, out var value) || value is not T typed)
            throw new BifrostExecutionError(
                $"'{key}' is not configured for BifrostQL endpoint '{endpoint ?? "(default)"}'.");
        return typed;
    }
}
