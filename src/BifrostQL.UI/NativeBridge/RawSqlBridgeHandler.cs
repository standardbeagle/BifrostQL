using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.UI.Web;

namespace BifrostQL.UI.NativeBridge
{
    /// <summary>
    /// Raw SQL grid execution — Photino-only channel. Runs arbitrary SQL against
    /// the currently active connection entirely in-process; results never traverse
    /// the localhost HTTP/GraphQL surface. This is a desktop database explorer, so
    /// full DML/DDL is intentionally permitted (no RawSqlValidator gate). The
    /// <see cref="ConnectionState"/> is kept in sync by <c>/api/vault/connect</c>
    /// and the quickstart self-bind path.
    ///
    /// Payload: { sql: string, params?: {name:value}, maxRows?: int, timeout?: int }
    /// Result:  { columns: [{name,type}], rows: [[...]], rowsAffected, truncated }
    ///
    /// Errors (including driver exceptions that may embed the connection string)
    /// are scrubbed by <see cref="NativeBridgeHost"/> before reaching the renderer.
    /// </summary>
    public sealed class RawSqlBridgeHandler
    {
        private readonly ConnectionState _state;

        public RawSqlBridgeHandler(ConnectionState state) => _state = state;

        public void Register(NativeBridgeHost bridge) => bridge.Register("exec-sql", ExecSqlAsync);

        private async Task<object?> ExecSqlAsync(JsonElement payload, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(_state.ConnectionString) || _state.Provider is null)
                throw new InvalidOperationException("No active database connection. Connect to a database first.");

            if (payload.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("exec-sql payload must be an object");

            var sql = payload.TryGetProperty("sql", out var sqlEl) && sqlEl.ValueKind == JsonValueKind.String
                ? sqlEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(sql))
                throw new ArgumentException("sql is required");

            var maxRows = payload.TryGetProperty("maxRows", out var mrEl)
                && mrEl.ValueKind == JsonValueKind.Number && mrEl.TryGetInt32(out var mr) && mr > 0
                ? mr
                : RawSqlQueryResolver.DefaultMaxRows;

            var timeout = payload.TryGetProperty("timeout", out var toEl)
                && toEl.ValueKind == JsonValueKind.Number && toEl.TryGetInt32(out var to) && to > 0
                ? to
                : RawSqlQueryResolver.DefaultTimeoutSeconds;

            Dictionary<string, object?>? sqlParams = null;
            if (payload.TryGetProperty("params", out var paramsEl) && paramsEl.ValueKind == JsonValueKind.Object)
            {
                sqlParams = new Dictionary<string, object?>();
                foreach (var prop in paramsEl.EnumerateObject())
                    sqlParams[prop.Name] = VisualQueryBridge.JsonValueToClr(prop.Value);
            }

            var factory = DbConnFactoryResolver.Create(_state.ConnectionString, _state.Provider.Value);
            var result = await RawSqlExecutor.ExecuteAsync(factory, sql!, sqlParams, timeout, maxRows, ct);

            return new
            {
                columns = result.Columns.Select(c => new { name = c.Name, type = c.Type }).ToArray(),
                rows = result.Rows,
                rowsAffected = result.RowsAffected,
                truncated = result.Truncated
            };
        }
    }
}
