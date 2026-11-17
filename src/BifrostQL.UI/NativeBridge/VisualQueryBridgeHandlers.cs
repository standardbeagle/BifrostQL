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

        public void Register(IBridgeRegistry bridge)
        {
            bridge.Register("get-builder-schema", GetBuilderSchemaAsync);
            bridge.Register("build-sql", BuildSqlAsync);
            bridge.Register("build-and-exec", BuildAndExecAsync);
        }

        private async Task<object?> GetBuilderSchemaAsync(JsonElement _, CancellationToken __)
        {
            _state.RequireActive();
            var model = await BridgeModel.ResolveAsync(_services);
            return BuilderSchemaProjection.Project(model);
        }

        private async Task<object?> BuildSqlAsync(JsonElement payload, CancellationToken _)
        {
            var (model, dialect, _) = await ResolveModelAndDialectAsync();
            var spec = VisualQueryBridge.Parse(payload);
            var built = VisualQueryBuilder.Build(spec, model, dialect);
            return new { sql = built.Sql, parameters = built.Parameters };
        }

        private async Task<object?> BuildAndExecAsync(JsonElement payload, CancellationToken ct)
        {
            var (model, dialect, active) = await ResolveModelAndDialectAsync();
            var spec = VisualQueryBridge.Parse(payload);
            var built = VisualQueryBuilder.Build(spec, model, dialect);

            var factory = DbConnFactoryResolver.Create(active.ConnectionString, active.Provider);
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

        // Resolves the cached model + the dialect of ONE connection snapshot, so the
        // factory that later executes is built from the same snapshot as the dialect.
        private async Task<(IDbModel Model, ISqlDialect Dialect, ActiveConnection Active)> ResolveModelAndDialectAsync()
        {
            var active = _state.RequireActive();
            var model = await BridgeModel.ResolveAsync(_services);
            var dialect = DbConnFactoryResolver.Create(active.ConnectionString, active.Provider).Dialect;
            return (model, dialect, active);
        }
    }
}
