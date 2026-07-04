using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.VisualQuery;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.UI.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Visual query builder bridge handlers — Photino-only, in-process.
    ///
    /// <list type="bullet">
    ///   <item><description><c>get-builder-schema</c> — tables/columns/FK relationships
    ///     of the active connection, sourced from the SAME cached <see cref="IDbModel"/>
    ///     the GraphQL pipeline uses so relationships (incl. composite FKs) match query
    ///     behavior exactly.</description></item>
    ///   <item><description><c>build-sql</c> — turns a <c>VisualQuerySpec</c> into SQL +
    ///     named parameters via the server-side dialect builder. No execution.</description></item>
    ///   <item><description><c>build-and-exec</c> — builds then runs via
    ///     <see cref="RawSqlExecutor"/>, returning the same columnar shape as
    ///     <c>exec-sql</c>.</description></item>
    /// </list>
    ///
    /// Driver exceptions are scrubbed by <see cref="NativeBridgeHost"/> before reaching
    /// the renderer.
    /// </summary>
    public sealed class VisualQueryBridgeHandlers
    {
        private readonly ConnectionState _state;
        private readonly IServiceProvider _services;

        public VisualQueryBridgeHandlers(ConnectionState state, IServiceProvider services)
        {
            _state = state;
            _services = services;
        }

        public void Register(NativeBridgeHost bridge)
        {
            bridge.Register("get-builder-schema", GetBuilderSchemaAsync);
            bridge.Register("build-sql", BuildSqlAsync);
            bridge.Register("build-and-exec", BuildAndExecAsync);
        }

        private async Task<object?> GetBuilderSchemaAsync(JsonElement _, CancellationToken __)
        {
            var model = await ResolveModelAsync();
            return BuilderSchemaProjection.Project(model);
        }

        private async Task<object?> BuildSqlAsync(JsonElement payload, CancellationToken _)
        {
            var (model, dialect) = await ResolveModelAndDialectAsync();
            var spec = VisualQueryBridge.Parse(payload);
            var built = VisualQueryBuilder.Build(spec, model, dialect);
            return new { sql = built.Sql, parameters = built.Parameters };
        }

        private async Task<object?> BuildAndExecAsync(JsonElement payload, CancellationToken ct)
        {
            var (model, dialect) = await ResolveModelAndDialectAsync();
            var spec = VisualQueryBridge.Parse(payload);
            var built = VisualQueryBuilder.Build(spec, model, dialect);

            var factory = DbConnFactoryResolver.Create(_state.ConnectionString!, _state.Provider!.Value);
            var result = await RawSqlExecutor.ExecuteAsync(
                factory, built.Sql, built.Parameters,
                RawSqlQueryResolver.DefaultTimeoutSeconds, RawSqlQueryResolver.DefaultMaxRows, ct);

            return new
            {
                sql = built.Sql,
                columns = result.Columns.Select(c => new { name = c.Name, type = c.Type }).ToArray(),
                rows = result.Rows,
                rowsAffected = result.RowsAffected,
                truncated = result.Truncated,
            };
        }

        // Resolves the cached model for the active connection. Throws the same way
        // exec-sql does when there is no connection or the schema has not loaded.
        // GetFirstValueAsync() lazily loads the model on first access.
        private async Task<IDbModel> ResolveModelAsync()
        {
            if (string.IsNullOrEmpty(_state.ConnectionString) || _state.Provider is null)
                throw new InvalidOperationException("No active database connection. Connect to a database first.");

            var pathCache = _services.GetService<PathCache<GraphQL.Inputs>>();
            var inputs = pathCache is null ? null : await pathCache.GetFirstValueAsync();
            if (inputs is null || !inputs.TryGetValue("model", out var modelObj) || modelObj is not IDbModel model)
                throw new InvalidOperationException("Database schema is not loaded yet.");

            return model;
        }

        // Resolves the cached model + active dialect for the build handlers.
        private async Task<(IDbModel Model, ISqlDialect Dialect)> ResolveModelAndDialectAsync()
        {
            var model = await ResolveModelAsync();
            var dialect = DbConnFactoryResolver.Create(_state.ConnectionString!, _state.Provider!.Value).Dialect;
            return (model, dialect);
        }
    }
}
