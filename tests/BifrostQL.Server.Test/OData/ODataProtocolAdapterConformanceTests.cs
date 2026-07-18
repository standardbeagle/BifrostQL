using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Server.OData;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BifrostQL.Server.Test.OData
{
    /// <summary>
    /// Runs the shared protocol-adapter security-conformance suite against the OData v4 HTTP front
    /// door. Every read is issued as a REAL OData request — an entity-set GET with
    /// <c>$select</c>/<c>$filter</c> travelling the actual <see cref="ODataMiddleware"/> against the
    /// base fixture's real transformer-pipeline <see cref="IQueryIntentExecutor"/> and shared
    /// <see cref="IBifrostAuthContextFactory"/> — so tenant isolation, soft-delete, policy read
    /// guards and parameterization are proven ON THE WIRE, not shortcut into core. This is the
    /// machine-check that no OData read path bypasses the query-intent seam or the shared auth
    /// projection (slice-7 acceptance criterion 1).
    ///
    /// <para><b>Read-only.</b> OData is a read-only front door this epic — it exposes NO write verb —
    /// so <see cref="AdapterSupportsMutations"/> stays false (the default) and the kit's mutation
    /// facts are skipped honestly rather than faked. Not-cutting: the read-side security facts
    /// (tenant/soft-delete/policy/param/fail-closed) all run against the real pipeline.</para>
    ///
    /// <para><b>Driven on its own front door.</b> Like the RESP and MCP derivations, the OData
    /// middleware is bound to the base fixture's <c>Host.Services</c> executors, so nothing is
    /// registered on the HTTP endpoint options (<see cref="RegisterAdapter"/> is a no-op). The base
    /// host still builds the transformer-pipeline executor and the SQL-capture observer this
    /// derivation relies on for the SQL assertions.</para>
    ///
    /// <para><b>Sanitized / invisibility rejections (documented exemptions from the canonical wire
    /// text, NOT from the fail-closed assertion).</b> Two kit facts assert a canonical server error
    /// fragment; the read is still REJECTED (<see cref="ExecuteReadAsync"/> throws, zero rows), but
    /// the wire text differs by OData's own security contract, so <see cref="ExpectedRejectionFragment"/>
    /// is overridden:
    /// <list type="bullet">
    /// <item>The kit's <c>documents</c> table declares <c>policy-read-deny: body</c> and NO
    /// <c>policy-actions</c>, so its <see cref="BifrostQL.Core.Auth.TablePolicy"/> has a policy but an
    /// empty allow-list — <see cref="BifrostQL.Core.Auth.PolicyEvaluator.CanAct"/> denies table READ
    /// for a non-admin on BOTH the query path (the priority-1 policy transformer) and OData. OData
    /// applies that SAME authoritative gate in <see cref="ODataModelVisibility"/>, so the whole table
    /// is INVISIBLE (.claude/rules/protocol-adapter-security.md invariant 4) and selecting OR filtering
    /// the denied column is a clean 404 — deliberately indistinguishable from a non-existent set, the
    /// STRONGEST anti-oracle. The denied column (indeed the whole table) cannot be read; the read is
    /// refused with zero rows. The wire text is thus OData's sanitized "not found", not the query
    /// path's field-level "not permitted by authorization policy" message.</item>
    /// <item>A <b>missing tenant identity</b> is modelled (as RESP does) by an authenticated
    /// principal carrying no tenant claim, so the request passes auth and reaches the pipeline, where
    /// the tenant transformer fails closed. That fault is a Bifrost-internal exception, which the
    /// middleware sanitizes to a generic OData InternalError (invariant 3) rather than forwarding
    /// verbatim — so the surfaced wire text is the sanitized "internal error", while the read is
    /// still rejected with no rows.</item>
    /// </list>
    /// Neither exemption relaxes the requirement that the read is refused — only which text the
    /// thrown rejection carries.</para>
    /// </summary>
    public sealed class ODataProtocolAdapterConformanceTests : ProtocolAdapterConformanceTests
    {
        // OData is driven on its own HTTP front door bound to the fixture's real executors, so nothing
        // is registered on the endpoint options. OData exposes no write verb, so AdapterSupportsMutations
        // stays false (default) and the mutation facts are skipped honestly.
        protected override void RegisterAdapter(BifrostMultiDbOptions options) { }

        // See the class remarks: a policy-read-denied table is invisible under invariant 4 (→ a 404
        // "not found", indistinguishable from a non-existent set), and a pipeline tenant fault is
        // sanitized to a generic InternalError (invariant 3, → "internal error"). Both still throw
        // with zero rows — only the surfaced wire text is relaxed, never the fail-closed assertion.
        protected override string ExpectedRejectionFragment(string canonicalServerFragment)
            => canonicalServerFragment.Contains("policy", StringComparison.OrdinalIgnoreCase)
                ? "not found"
                : "internal error";

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            // A null principal models "no tenant identity". The OData wire cannot authenticate
            // "nobody" (that is a 401 before the pipeline), so — like RESP — it authenticates an
            // identity carrying no tenant claim, and the tenant transformer then fails closed.
            var principal = request.Principal ?? NoTenantPrincipal();

            var reads = Host.Services.GetRequiredService<IQueryIntentExecutor>();
            var authFactory = Host.Services.GetRequiredService<IBifrostAuthContextFactory>();
            var options = new ODataOptions { Endpoint = EndpointPath };
            var authenticator = new ODataAuthenticator(authFactory, basicStore: null);
            RequestDelegate next = _ => throw new InvalidOperationException("the OData endpoint terminates the request");
            var middleware = new ODataMiddleware(
                next, options, authenticator, reads, NullLogger<ODataMiddleware>.Instance);

            // Translate DB column names (the kit's contract) to the entity's EDM property names, which
            // are the columns' schema-derived GraphQL names — the names $select/$filter resolve against.
            var model = await reads.GetModelAsync(EndpointPath);
            var table = model.GetTableFromDbName(request.Table);
            var dbToGraph = table.Columns.ToDictionary(
                c => c.DbName, c => c.GraphQlName, StringComparer.OrdinalIgnoreCase);
            var graphToDb = table.Columns.ToDictionary(
                c => c.GraphQlName, c => c.DbName, StringComparer.OrdinalIgnoreCase);
            string ToProperty(string dbName) => dbToGraph.TryGetValue(dbName, out var g) ? g : dbName;

            var query = BuildQueryString(request, ToProperty);

            var ctx = new DefaultHttpContext { User = principal };
            ctx.Request.Path = "/" + table.GraphQlName;
            if (query.Length > 0)
                ctx.Request.QueryString = new QueryString("?" + query);
            ctx.Response.Body = new MemoryStream();

            await middleware.InvokeAsync(ctx);

            ctx.Response.Body.Seek(0, SeekOrigin.Begin);
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();

            if (ctx.Response.StatusCode != 200)
                throw new InvalidOperationException(ExtractErrorMessage(body, ctx.Response.StatusCode));

            return DecodeRows(body, graphToDb);
        }

        private static string BuildQueryString(ConformanceReadRequest request, Func<string, string> toProperty)
        {
            var parts = new List<string>();
            if (request.Columns.Count > 0)
                parts.Add("$select=" + string.Join(",", request.Columns.Select(toProperty)));

            if (request.Filter is { Count: > 0 })
            {
                var clauses = request.Filter.Select(kv => $"{toProperty(kv.Key)} eq {FilterLiteral(kv.Value)}");
                parts.Add("$filter=" + string.Join(" and ", clauses));
            }

            return string.Join("&", parts);
        }

        /// <summary>Renders the kit's single-<c>_eq</c> operator value as an OData literal.</summary>
        private static string FilterLiteral(object? operatorDict)
        {
            if (operatorDict is not IReadOnlyDictionary<string, object?> ops || ops.Count != 1 || !ops.ContainsKey("_eq"))
                throw new NotSupportedException("OData conformance filter must be a single { _eq: value } object.");

            var value = ops["_eq"];
            return value switch
            {
                null => "null",
                string s => "'" + s.Replace("'", "''") + "'",
                bool b => b ? "true" : "false",
                _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
            };
        }

        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> DecodeRows(
            string body, IReadOnlyDictionary<string, string> graphToDb)
        {
            var value = JsonDocument.Parse(body).RootElement.GetProperty("value");
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (var element in value.EnumerateArray())
            {
                var row = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var key = graphToDb.TryGetValue(property.Name, out var db) ? db : property.Name;
                    row[key] = DecodeJson(property.Value);
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>Pulls the sanitized OData error message off the wire so the kit sees the real rejection text.</summary>
        private static string ExtractErrorMessage(string body, int status)
        {
            try
            {
                return JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString()
                    ?? $"OData request rejected with status {status}.";
            }
            catch (Exception)
            {
                return $"OData request rejected with status {status}: {body}";
            }
        }

        private static object? DecodeJson(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True or JsonValueKind.False => element.GetBoolean(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText(),
        };

        /// <summary>An authenticated identity with no tenant claim — the OData equivalent of the kit's null principal.</summary>
        private static ClaimsPrincipal NoTenantPrincipal() =>
            new(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "user-no-tenant") }, authenticationType: "odata"));
    }
}
