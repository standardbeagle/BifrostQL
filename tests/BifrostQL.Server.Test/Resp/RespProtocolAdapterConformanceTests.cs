using System.Security.Claims;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Resp;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Test.Resp
{
    /// <summary>
    /// Runs the shared protocol-adapter security-conformance suite against the RESP (Redis) front
    /// door. Every read and write is issued as REAL RESP wire traffic — a bulk-string command array
    /// over a loopback TCP socket, replies decoded with the real codec — through the actual
    /// <see cref="RespConnectionHandler"/> against the base fixture's real transformer-pipeline
    /// <see cref="IQueryIntentExecutor"/> / <see cref="IMutationIntentExecutor"/>. So tenant isolation,
    /// soft-delete, policy read guards, parameterization and the mutation transformer chain are proven
    /// on the wire, not shortcut into core.
    ///
    /// <para><b>Read composition.</b> RESP has no "SELECT columns FROM table WHERE …" verb; a whole-table
    /// read is composed from the primitives the wire actually exposes — <c>SCAN &lt;table&gt;:*</c> to
    /// enumerate the identity's visible primary keys, then <c>HGETALL</c> per key for the row's visible
    /// columns. Both travel the real read pipeline, so the tenant/soft-delete/policy WHERE clauses are
    /// injected server-side on every command. A caller column filter is applied client-side here because
    /// RESP has no server-side arbitrary-column predicate — which is itself a stronger property: a caller
    /// value the wire cannot express can never be concatenated into SQL.</para>
    ///
    /// <para><b>Writes: UPDATE/DELETE, no INSERT.</b> <see cref="AdapterSupportsMutations"/> is true — the
    /// RESP write surface (SET = update, HSET = update, DEL = delete) MUST prove tenant scoping,
    /// cross-tenant no-op and soft-delete on the wire, so the conformance run enables writes
    /// (<c>EnableWrites = true</c>) and those facts run against the real mutation pipeline.
    /// <see cref="AdapterSupportsInserts"/> is false: the key-addressed wire has no row-creating command,
    /// so the two INSERT-specific facts are skipped honestly rather than faked through an update.</para>
    ///
    /// <para><b>Sanitized rejections.</b> Per protocol-adapter-security invariant 3 the RESP handler maps
    /// every unexpected server-side fault (tenant-context-required, policy-read-deny) to a single generic
    /// <c>ERR internal error</c>; the specific reason is logged server-side, never sent to the client. So
    /// <see cref="ExpectedRejectionFragment"/> is overridden to that sanitized wire text — the fail-closed
    /// facts still prove the read is REJECTED (the wire returns <c>-ERR</c> and <see cref="ExecuteReadAsync"/>
    /// throws — zero rows delivered), while honoring the no-leak contract, exactly like pgwire.</para>
    /// </summary>
    public sealed class RespProtocolAdapterConformanceTests : ProtocolAdapterConformanceTests
    {
        private const string LoginUser = "u";
        private const string LoginSecret = "pw";

        // RESP is driven here on its own loopback front door bound to the fixture's real executors, so
        // nothing is registered on the HTTP endpoint options. The base host still builds the
        // transformer-pipeline executors and the SQL-capture observer this derivation relies on.
        protected override void RegisterAdapter(BifrostMultiDbOptions options) { }

        // The RESP write surface exposes UPDATE (SET/HSET) and DELETE (DEL) — its mutation facts must run.
        protected override bool AdapterSupportsMutations => true;

        // …but the key-addressed wire has no INSERT verb; the two insert facts are skipped honestly.
        protected override bool AdapterSupportsInserts => false;

        // The wire withholds the specific rejection reason (invariant 3); the fail-closed facts assert
        // the sanitized text the client actually receives.
        protected override string ExpectedRejectionFragment(string canonicalServerFragment)
            => RespProtocol.InternalError;

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            // A null principal models "no tenant identity". The wire cannot AUTH "nobody", so it AUTHs an
            // identity that simply carries no tenant claim — the tenant transformer then fails closed.
            var principal = request.Principal ?? NoTenantPrincipal();

            await using var fixture = await StartFixtureAsync(principal, request.Endpoint);
            await RespWire.AuthenticateAsync(fixture.Client, LoginUser, LoginSecret);

            // SCAN enumerates only the identity's visible PKs; HGETALL fetches each row's visible columns.
            var keys = await RespWire.ScanAllKeysAsync(fixture.Client, request.Table);
            var rows = new List<IReadOnlyDictionary<string, object?>>(keys.Count);
            foreach (var key in keys)
            {
                var hash = await RespWire.HGetAllAsync(fixture.Client, key);
                rows.Add(hash.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal));
            }

            var filtered = ApplyCallerFilter(rows, request.Filter);
            return filtered.Select(row => Project(row, request.Columns)).ToList();
        }

        protected override async Task<object?> ExecuteMutationAsync(ConformanceMutationRequest request)
        {
            var principal = request.Principal ?? NoTenantPrincipal();
            await using var fixture = await StartFixtureAsync(principal, request.Endpoint);
            await RespWire.AuthenticateAsync(fixture.Client, LoginUser, LoginSecret);

            var key = BuildKey(request.Table, request.PrimaryKey, request.Data);
            return request.Action switch
            {
                // SET/HSET are both UPDATE on the wire; the conformance update facts drive named columns.
                ConformanceMutationAction.Update => await RespWire.HSetAsync(fixture.Client, key, request.Data),
                ConformanceMutationAction.Delete => await RespWire.DelAsync(fixture.Client, key),
                ConformanceMutationAction.Insert => throw new NotSupportedException(
                    "the RESP wire has no INSERT verb; AdapterSupportsInserts is false so this is never reached"),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Action, "unknown mutation action"),
            };
        }

        /// <summary>Starts a loopback RESP front door bound to the base fixture's real executors, writes enabled.</summary>
        private Task<RespFixture> StartFixtureAsync(ClaimsPrincipal principal, string endpoint)
        {
            var store = new FakeRespCredentialStore().Add(LoginUser, LoginSecret, principal);
            var options = new RespWireOptions
            {
                RequireAuthentication = true,
                EnableWrites = true,
                Endpoint = endpoint,
            };
            // The write handlers resolve IQueryIntentExecutor/IMutationIntentExecutor/RespWireOptions
            // from the command context's services — bind the base fixture's real executors here.
            var handlerServices = new ServiceCollection()
                .AddSingleton(Host.Services.GetRequiredService<IQueryIntentExecutor>())
                .AddSingleton(Host.Services.GetRequiredService<IMutationIntentExecutor>())
                .AddSingleton(options)
                .BuildServiceProvider();
            return RespFixture.StartAsync(store, handlerServices, options, RespDataHandlers.All());
        }

        /// <summary>Applies the kit's single-<c>_eq</c>-operator filter client-side (RESP has no server-side column filter).</summary>
        private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ApplyCallerFilter(
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, IReadOnlyDictionary<string, object?>? filter)
        {
            if (filter is not { Count: > 0 })
                return rows;
            return rows.Where(row => filter.All(f => MatchesEq(row, f.Key, f.Value))).ToList();
        }

        private static bool MatchesEq(IReadOnlyDictionary<string, object?> row, string column, object? operatorDict)
        {
            if (operatorDict is not IReadOnlyDictionary<string, object?> ops || ops.Count != 1 || !ops.ContainsKey("_eq"))
                throw new NotSupportedException(
                    $"RESP conformance filter for '{column}' must be a single {{ _eq: value }} object.");
            var expected = Convert.ToString(ops["_eq"], System.Globalization.CultureInfo.InvariantCulture);
            var actual = row.TryGetValue(column, out var value)
                ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
                : null;
            return string.Equals(expected, actual, StringComparison.Ordinal);
        }

        private static IReadOnlyDictionary<string, object?> Project(
            IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> columns)
        {
            var record = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            foreach (var column in columns)
                record[column] = row.GetValueOrDefault(column);
            return record;
        }

        /// <summary>Formats a mutation request's primary key as the RESP key <c>&lt;table&gt;:&lt;pk…&gt;</c>.</summary>
        private static string BuildKey(string table, IReadOnlyList<object?>? primaryKey, IReadOnlyDictionary<string, object?> data)
        {
            var pk = primaryKey ?? throw new NotSupportedException("RESP writes address a row by its primary key.");
            var segments = pk.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
            return $"{table}{RespProtocol.KeySeparator}{string.Join(RespProtocol.KeySeparator, segments)}";
        }

        /// <summary>An authenticated identity with no tenant claim — the wire equivalent of the kit's null principal.</summary>
        private static ClaimsPrincipal NoTenantPrincipal() =>
            new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "user-no-tenant") }, authenticationType: "resp"));
    }
}
