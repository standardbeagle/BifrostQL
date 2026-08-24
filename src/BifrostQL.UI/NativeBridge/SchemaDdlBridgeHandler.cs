using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using BifrostQL.Server;
using BifrostQL.UI.Web;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// <c>get-table-ddl</c>: the shell's "Copy DDL" source. Payload <c>{ table }</c> —
    /// the qualified <c>schema.name</c> (or bare name) as the builder schema reports
    /// it — resolved against the loaded model, rendered by
    /// <see cref="TableDdlGenerator"/> in the active connection's dialect. Reads only,
    /// no window: registered on BOTH the Photino bridge and the HTTP test mirror.
    /// </summary>
    public sealed class SchemaDdlBridgeHandler
    {
        private readonly ConnectionState _state;
        private readonly IServiceProvider _services;

        public SchemaDdlBridgeHandler(ConnectionState state, IServiceProvider services)
        {
            _state = state;
            _services = services;
        }

        public void Register(IBridgeRegistry bridge) => bridge.Register("get-table-ddl", GetTableDdlAsync);

        private async Task<object?> GetTableDdlAsync(JsonElement payload, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(_state.ConnectionString) || _state.Provider is null)
                throw new InvalidOperationException("No active database connection. Connect to a database first.");
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("table", out var tableProp)
                || tableProp.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tableProp.GetString()))
                throw new ArgumentException("get-table-ddl requires a 'table' string payload.");
            var requested = tableProp.GetString()!;

            var pathCache = _services.GetService<PathCache<GraphQL.Inputs>>();
            var inputs = pathCache is null ? null : await pathCache.GetFirstValueAsync();
            if (inputs is null || !inputs.TryGetValue("model", out var modelObj) || modelObj is not IDbModel model)
                throw new InvalidOperationException("Database schema is not loaded yet.");

            var table = model.Tables.FirstOrDefault(t =>
                string.Equals($"{t.TableSchema}.{t.DbName}", requested, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.DbName, requested, StringComparison.OrdinalIgnoreCase)
                || t.MatchName(requested));
            if (table is null)
                throw new ArgumentException($"Table '{requested}' was not found in the loaded schema.");

            var dialect = DbConnFactoryResolver.Create(_state.ConnectionString!, _state.Provider!.Value).Dialect;
            return new
            {
                table = string.IsNullOrEmpty(table.TableSchema) ? table.DbName : $"{table.TableSchema}.{table.DbName}",
                ddl = TableDdlGenerator.Generate(table, dialect),
            };
        }
    }
}
