using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.UI.Web;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// <c>get-table-ddl</c>: the shell's "Copy DDL" source. Payload <c>{ table }</c> —
    /// the schema-qualified <c>schema.name</c> the editor sends — resolved against the
    /// loaded model by <see cref="ResolveTable"/>, rendered by
    /// <see cref="TableDdlGenerator"/> in the active connection's dialect. Reads only,
    /// no window: registered on BOTH the Photino bridge and the HTTP test mirror.
    /// </summary>
    public sealed class SchemaDdlBridgeHandler
    {
        /// <summary>The one answer for an unknown AND an ambiguous name. It carries no name.</summary>
        internal const string TableNotFoundMessage =
            "Table was not found in the loaded schema. Name it as schema.name when two schemas share the name.";

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
            var active = _state.RequireActive();
            if (payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("table", out var tableProp)
                || tableProp.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tableProp.GetString()))
                throw new ArgumentException("get-table-ddl requires a 'table' string payload.");

            var model = await BridgeModel.ResolveAsync(_services);
            var table = ResolveTable(model, tableProp.GetString()!);

            var dialect = DbConnFactoryResolver.Create(active.ConnectionString, active.Provider).Dialect;
            return new
            {
                table = string.IsNullOrEmpty(table.TableSchema) ? table.DbName : $"{table.TableSchema}.{table.DbName}",
                ddl = TableDdlGenerator.Generate(table, dialect),
            };
        }

        /// <summary>
        /// Resolves the requested DB name: <c>schema.name</c> (split on the FIRST dot) through
        /// the schema-qualified <see cref="IDbModel.TryGetTableFromDbName(string, string, out IDbTable?)"/>,
        /// else a bare name through <see cref="IDbModel.TryGetTableFromDbName(string, out IDbTable?)"/>,
        /// which refuses a name two schemas share. Unknown and ambiguous both throw the one
        /// <see cref="TableNotFoundMessage"/>: copying the DDL of whichever same-named table
        /// enumerated first would hand the user the wrong table's definition.
        /// </summary>
        internal static IDbTable ResolveTable(IDbModel model, string requested)
        {
            var dot = requested.IndexOf('.');
            if (dot > 0 && dot < requested.Length - 1
                && model.TryGetTableFromDbName(requested[..dot], requested[(dot + 1)..], out var qualified))
                return qualified;
            if (model.TryGetTableFromDbName(requested, out var bare))
                return bare;
            throw BifrostErrorSink.LookupMiss(
                TableNotFoundMessage,
                $"get-table-ddl: '{requested}' is unknown or ambiguous across schemas.",
                nameof(SchemaDdlBridgeHandler));
        }
    }
}
