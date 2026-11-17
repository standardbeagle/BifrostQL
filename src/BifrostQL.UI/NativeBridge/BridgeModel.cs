using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// The one place the bridge handlers read the active connection's cached
    /// <see cref="IDbModel"/> — the SAME model the GraphQL pipeline uses.
    /// <c>GetFirstValueAsync()</c> lazily loads it on first access.
    /// </summary>
    internal static class BridgeModel
    {
        public static async Task<IDbModel> ResolveAsync(IServiceProvider services)
        {
            var pathCache = services.GetService<PathCache<GraphQL.Inputs>>();
            var inputs = pathCache is null ? null : await pathCache.GetFirstValueAsync();
            if (inputs is null || !inputs.TryGetValue("model", out var modelObj) || modelObj is not IDbModel model)
                throw new InvalidOperationException("Database schema is not loaded yet.");
            return model;
        }
    }
}
