using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.UI.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// The cached <see cref="IDbModel"/> of the active connection, as every bridge handler that
    /// reads the schema needs it. Throws the same way exec-sql does when there is no connection or
    /// the schema has not loaded; <c>GetFirstValueAsync()</c> lazily loads the model on first access.
    /// </summary>
    internal static class LoadedBridgeModel
    {
        public static async Task<IDbModel> ResolveAsync(ConnectionState state, IServiceProvider services)
        {
            if (string.IsNullOrEmpty(state.ConnectionString) || state.Provider is null)
                throw new InvalidOperationException("No active database connection. Connect to a database first.");

            var pathCache = services.GetService<PathCache<GraphQL.Inputs>>();
            var inputs = pathCache is null ? null : await pathCache.GetFirstValueAsync();
            if (inputs is null || !inputs.TryGetValue("model", out var modelObj) || modelObj is not IDbModel model)
                throw new InvalidOperationException("Database schema is not loaded yet.");

            return model;
        }
    }
}
