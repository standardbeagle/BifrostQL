using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Runs the shared protocol-adapter security-conformance suite against the BINARY WEBSOCKET
    /// transport (<c>/bifrost-ws</c>, <see cref="BifrostBinaryMiddleware"/>) — the front door the
    /// BifrostQL UI's transport toggle routes every editor query through.
    ///
    /// <para>It is a full kit citizen, not an exception: the transport carries arbitrary GraphQL
    /// text, so every conformance request translates onto its real wire (a real WebSocket upgrade
    /// against the TestServer, real protobuf frames), and both the read and the mutation facts
    /// apply unchanged. The one thing it does not share with the other derivations is HOSTING — it
    /// is HTTP middleware rather than a Kestrel <c>ConnectionHandler</c>-hosted
    /// <c>IProtocolAdapter</c> — which is what <see cref="ConfigureAdapterPipeline"/> exists for.</para>
    ///
    /// <para>The claims header below stands in for the deployment's authentication middleware
    /// (the same role <c>RoleHeaderAuthHandler</c> plays for the app-metadata suite): it puts a
    /// <see cref="ClaimsPrincipal"/> on the upgrade request's <see cref="HttpContext"/> and stops
    /// there. Everything the facts prove happens AFTER that — the middleware projecting that
    /// principal through <see cref="IBifrostAuthContextFactory"/> and executing through the shared
    /// engine, where the transformer chain is unskippable.</para>
    /// </summary>
    public sealed class BinaryTransportConformanceTests : ProtocolAdapterConformanceTests
    {
        private const string SocketPath = "/bifrost-ws";
        private const string ClaimsHeader = "X-Test-Claims";

        private uint _requestId;

        // The binary transport is not an IProtocolAdapter — it is mounted as middleware below.
        protected override void RegisterAdapter(BifrostMultiDbOptions options) { }

        protected override void ConfigureAdapterServices(IServiceCollection services)
            => services.AddBifrostEngine();

        protected override void ConfigureAdapterPipeline(IApplicationBuilder app)
        {
            // Stands in for the deployment's authentication middleware: lands a principal on the
            // upgrade request and nothing more.
            app.Use(async (context, next) =>
            {
                var header = context.Request.Headers[ClaimsHeader].ToString();
                if (!string.IsNullOrEmpty(header))
                    context.User = DecodePrincipal(header);
                await next(context);
            });
            app.UseWebSockets();
            app.UseBifrostBinary(SocketPath, graphqlPath: EndpointPath);
        }

        // Query and mutation both travel the same wire and the same engine call; the transport
        // exposes writes, so it must prove the mutation facts too.
        protected override bool AdapterSupportsMutations => true;

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            var table = await TableAsync(request.Table);
            var columns = string.Join(" ", request.Columns.Select(c => GraphQlColumn(table, c)));
            var filter = request.Filter is null ? "" : $"(filter: {RenderFilter(table, request.Filter)})";
            var query = $"{{ {table.GraphQlName}{filter} {{ data {{ {columns} }} }} }}";

            var payload = await SendAsync(BifrostMessageType.Query, query, request.Principal);
            return DecodeRows(payload, table);
        }

        protected override async Task<object?> ExecuteMutationAsync(ConformanceMutationRequest request)
        {
            var table = await TableAsync(request.Table);
            var verb = request.Action switch
            {
                ConformanceMutationAction.Insert => "insert",
                ConformanceMutationAction.Update => "update",
                ConformanceMutationAction.Delete => "delete",
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Action, "Unknown action."),
            };

            var fields = new List<string>();
            // Update/delete address the row by its primary key positionally (composite-key safe);
            // insert carries only the supplied column values.
            if (request.PrimaryKey is { Count: > 0 })
            {
                var keys = table.KeyColumns.ToList();
                if (keys.Count != request.PrimaryKey.Count)
                    throw new InvalidOperationException(
                        $"Table '{table.DbName}' has {keys.Count} key column(s); the request supplied {request.PrimaryKey.Count}.");
                for (var i = 0; i < keys.Count; i++)
                    fields.Add($"{keys[i].GraphQlName}: {RenderValue(request.PrimaryKey[i])}");
            }
            var supplied = new HashSet<string>(request.Data.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in request.Data)
                fields.Add($"{GraphQlColumn(table, entry.Key)}: {RenderValue(entry.Value)}");

            if (request.Action != ConformanceMutationAction.Delete)
                foreach (var required in RequiredColumnsMissingFrom(table, supplied))
                    fields.Add($"{required.GraphQlName}: {RenderValue(WireValueFor(table, required, request.Principal))}");

            var mutation = $"mutation {{ {table.GraphQlName}({verb}: {{ {string.Join(", ", fields)} }}) }}";
            var payload = await SendAsync(BifrostMessageType.Mutation, mutation, request.Principal);

            using var document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("data", out var data)
                && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty(table.GraphQlName, out var value)
                    ? value.ToString()
                    : null;
        }

        /// <summary>
        /// The generated GraphQL insert/update input types mark every non-nullable column REQUIRED,
        /// so a request the kit expresses with only the columns it cares about would be rejected by
        /// schema validation before any security transformer ran — a vacuous pass dressed as a
        /// fail-closed one. The derivation therefore fills the columns the WIRE demands, exactly as
        /// a real client must, and nothing else.
        /// </summary>
        private static IEnumerable<ColumnDto> RequiredColumnsMissingFrom(IDbTable table, HashSet<string> supplied) =>
            table.Columns.Where(c =>
                !c.IsNullable && !c.IsPrimaryKey && !c.IsIdentity && !c.IsComputed && !supplied.Contains(c.ColumnName));

        /// <summary>
        /// The value a real client would put in a required column the conformance request omitted.
        /// For the table's TENANT column that is the caller's own tenant claim — the honest client
        /// value, and the one that leaves the cross-tenant facts able to fail: a working transformer
        /// scopes the write away, a broken one hijacks the target row. With no caller identity at
        /// all there is no claim to copy, so a placeholder goes on the wire and the write must be
        /// refused for the ABSENCE of identity, not for a malformed frame.
        /// </summary>
        private static object? WireValueFor(IDbTable table, ColumnDto column, ClaimsPrincipal? principal)
        {
            if (IsTenantColumn(table, column))
                return principal?.FindFirst(Auth.LocalAuthClaims.Tenant)?.Value ?? "no-tenant-claim";
            return column.IsNullable ? null : "conformance-required";
        }

        private static bool IsTenantColumn(IDbTable table, ColumnDto column) =>
            table.Metadata.TryGetValue(MetadataKeys.Security.TenantFilter, out var raw)
            && raw is string tenantColumn
            && string.Equals(tenantColumn.Trim(), column.ColumnName, StringComparison.OrdinalIgnoreCase);

        // ---- the wire ------------------------------------------------------

        /// <summary>
        /// Opens a real WebSocket to <c>/bifrost-ws</c>, sends one protobuf request frame and
        /// returns the response payload. A server-side rejection (an Error frame, or a Result
        /// frame carrying GraphQL errors) is thrown, never swallowed — the kit's fail-closed facts
        /// are only meaningful if the suite cannot mistake a rejection for an empty result set.
        /// </summary>
        private async Task<byte[]> SendAsync(BifrostMessageType type, string query, ClaimsPrincipal? principal)
        {
            var client = Host.GetTestServer().CreateWebSocketClient();
            client.ConfigureRequest = context =>
            {
                if (principal is not null)
                    context.Headers[ClaimsHeader] = EncodePrincipal(principal);
            };

            var uri = new UriBuilder(Host.GetTestServer().BaseAddress) { Scheme = "ws", Path = SocketPath }.Uri;
            using var socket = await client.ConnectAsync(uri, CancellationToken.None);

            var request = new BifrostMessage
            {
                RequestId = ++_requestId,
                Type = type,
                Query = query,
            };
            var bytes = request.ToBytes();
            await socket.SendAsync(bytes, WebSocketMessageType.Binary, endOfMessage: true, CancellationToken.None);

            var buffer = new byte[64 * 1024];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            var response = BifrostMessage.FromBytes(buffer, 0, received.Count);

            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);

            if (response.Type == BifrostMessageType.Error || response.Errors.Count > 0)
                throw new InvalidOperationException(string.Join(" | ", response.Errors));

            return response.Payload;
        }

        // ---- GraphQL encoding / decoding ------------------------------------

        private async Task<IDbTable> TableAsync(string dbName)
        {
            var model = await Host.Services.GetRequiredService<IQueryIntentExecutor>().GetModelAsync(EndpointPath);
            return model.GetTableFromDbName(dbName);
        }

        private static string GraphQlColumn(IDbTable table, string dbColumn) =>
            table.ColumnLookup.TryGetValue(dbColumn, out var column) ? column.GraphQlName : dbColumn;

        /// <summary>
        /// Renders the kit's GraphQL-shaped filter dictionary (DB column names) as GraphQL literal
        /// text against the table's GraphQL names.
        /// </summary>
        private static string RenderFilter(IDbTable table, IReadOnlyDictionary<string, object?> filter)
        {
            var parts = filter.Select(entry =>
            {
                var name = GraphQlColumn(table, entry.Key);
                if (entry.Value is IReadOnlyDictionary<string, object?> operators)
                    return $"{name}: {{ {string.Join(", ", operators.Select(o => $"{o.Key}: {RenderValue(o.Value)}"))} }}";
                return $"{name}: {{ _eq: {RenderValue(entry.Value)} }}";
            });
            return $"{{ {string.Join(", ", parts)} }}";
        }

        private static string RenderValue(object? value) => value switch
        {
            null => "null",
            string text => JsonSerializer.Serialize(text),
            bool flag => flag ? "true" : "false",
            _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
        };

        /// <summary>
        /// Decodes the <c>{"data":{"&lt;table&gt;":{"data":[…]}}}</c> GraphQL body back into rows
        /// keyed by DB column name, which is the vocabulary the kit's facts assert in.
        /// </summary>
        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> DecodeRows(byte[] payload, IDbTable table)
        {
            if (payload.Length == 0)
                return Array.Empty<IReadOnlyDictionary<string, object?>>();

            using var document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(table.GraphQlName, out var paged)
                || paged.ValueKind != JsonValueKind.Object
                || !paged.TryGetProperty("data", out var rows)
                || rows.ValueKind != JsonValueKind.Array)
                return Array.Empty<IReadOnlyDictionary<string, object?>>();

            var dbNameByGraphQl = table.Columns.ToDictionary(
                c => c.GraphQlName, c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

            return rows.EnumerateArray().Select(row =>
            {
                var decoded = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in row.EnumerateObject())
                {
                    var name = dbNameByGraphQl.TryGetValue(property.Name, out var dbName) ? dbName : property.Name;
                    decoded[name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.Null => null,
                        JsonValueKind.Number => property.Value.TryGetInt64(out var i) ? i : property.Value.GetDouble(),
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => property.Value.GetString(),
                    };
                }
                return (IReadOnlyDictionary<string, object?>)decoded;
            }).ToArray();
        }

        // ---- principal transport over the upgrade request -------------------

        private static string EncodePrincipal(ClaimsPrincipal principal) =>
            Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
                principal.Claims.Select(c => new[] { c.Type, c.Value }).ToArray()));

        private static ClaimsPrincipal DecodePrincipal(string header)
        {
            var claims = JsonSerializer.Deserialize<string[][]>(Convert.FromBase64String(header))!
                .Select(pair => new Claim(pair[0], pair[1]));
            return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"));
        }
    }
}
