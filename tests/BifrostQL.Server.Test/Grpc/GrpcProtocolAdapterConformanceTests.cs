using System.Security.Claims;
using System.Text.Json;
using BifrostQL.AdapterConformance;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Grpc;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Runs the shared protocol-adapter security-conformance suite against the gRPC HTTP/2 front door.
    /// Every read and write is issued as REAL gRPC wire traffic — a dynamically-dispatched
    /// Get/List/Insert/Update/Delete over a live <see cref="GrpcChannel"/> — through the actual
    /// <see cref="BifrostDynamicGrpcService"/> against the base fixture's real transformer-pipeline
    /// <see cref="IQueryIntentExecutor"/> / <see cref="IMutationIntentExecutor"/> and shared
    /// <see cref="IBifrostAuthContextFactory"/>. So tenant isolation, soft-delete, policy read guards,
    /// parameterization and the mutation transformer chain are proven on the wire, not shortcut into
    /// core (slice-7 acceptance criterion 1).
    ///
    /// <para><b>Write-capable with a real INSERT.</b> The gRPC surface exposes Insert/Update/Delete
    /// (slice 6), so <see cref="AdapterSupportsMutations"/> is true AND
    /// <see cref="AdapterSupportsInserts"/> stays true — unlike RESP's key-addressed wire, gRPC has a
    /// genuine row-creating verb, so every mutation fact (including the two INSERT-specific ones) runs
    /// against the real pipeline. Writes are gated a second time per-table by the <c>grpc-write</c>
    /// allow-list, so this derivation adds that opt-in to the fixture's <c>orders</c> table via the
    /// <see cref="MetadataRules"/> override (the tenant/soft-delete semantics the kit asserts are
    /// unchanged — only the adapter's write opt-in is added) and enables writes on the front door.</para>
    ///
    /// <para><b>Driven on its own front door bound to the base executors.</b> Like RESP/OData/pgwire,
    /// the gRPC door is a second in-process host whose DI binds the base fixture's real executors and
    /// shared auth factory, so the SQL-capture observer and metadata pipeline the kit's SQL assertions
    /// rely on are the SAME ones the reads travel. Nothing is registered on the HTTP endpoint options
    /// (<see cref="RegisterAdapter"/> is a no-op).</para>
    ///
    /// <para><b>Sanitized rejections (documented exemption from the canonical wire text, NOT from the
    /// fail-closed assertion).</b> Per protocol-adapter-security invariant 3 the gRPC status mapper
    /// funnels every fail-closed authorization fault (tenant-context-required, policy-read-deny) to a
    /// single generic status; the specific reason is logged server-side, never sent to the client. So
    /// <see cref="ExpectedRejectionFragment"/> is overridden to that sanitized wire text — the
    /// fail-closed facts still prove the read/write is REJECTED (the wire faults with an RpcException
    /// and no rows/writes land), while honoring the no-leak contract, exactly like pgwire and RESP.</para>
    /// </summary>
    public sealed class GrpcProtocolAdapterConformanceTests : ProtocolAdapterConformanceTests
    {
        private IHost _grpcHost = null!;
        private GrpcChannel _channel = null!;
        private GrpcWireTestClient _client = null!;
        private IDbModel _model = null!;

        // The gRPC write surface is gated per-table by the grpc-write allow-list; add that opt-in to the
        // orders table the mutation facts write to. tenant-filter + soft-delete (the semantics the kit
        // asserts) are unchanged — only the write opt-in is added.
        protected override IReadOnlyList<string> MetadataRules => new[]
        {
            "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at; grpc-write: enabled }",
            "*.documents { policy-read-deny: body }",
        };

        // gRPC is driven on its own HTTP/2 front door bound to the base fixture's real executors, so
        // nothing is registered on the HTTP endpoint options.
        protected override void RegisterAdapter(BifrostMultiDbOptions options) { }

        protected override bool AdapterSupportsMutations => true;

        // gRPC has a genuine INSERT verb (unlike the key-addressed RESP wire), so the insert facts run.
        protected override bool AdapterSupportsInserts => true;

        // Every fail-closed authorization fault is sanitized to one generic status (invariant 3); the
        // fail-closed facts assert the sanitized text the client actually receives, never the internal
        // reason. The read is still rejected with zero rows / zero writes — only the EXPECTED text is
        // relaxed, never the ASSERT (Lesson 1: adapt what you EXPECT, never what you ASSERT).
        protected override string ExpectedRejectionFragment(string canonicalServerFragment)
            => "The request was denied by policy.";

        // Filtering a policy-denied column hits the read compiler's field validation FIRST: a hidden
        // column is rejected as an unknown/unreadable field — deliberately indistinguishable from a
        // nonexistent field (invariant 4 anti-oracle), a different sanitized signal than the
        // table-level PERMISSION_DENIED that selecting the denied column's (invisible) table produces.
        protected override string ExpectedFilterRejectionFragment(string canonicalServerFragment)
            => "Unknown or unreadable field.";

        // RECONCILED (was a cross-op divergence): a missing-tenant fail-closed fault now surfaces the
        // SAME sanitized denial status on the WRITE path as on the READ path — PERMISSION_DENIED with the
        // generic "denied by policy" text. The write-side TenantMutationTransformer now tags its
        // fail-closed throw with AccessDeniedCode (matching the read-side TenantFilterTransformer), so the
        // single GrpcStatusMapper funnel maps it to PERMISSION_DENIED instead of a generic INTERNAL. Both
        // still fail closed (nothing is written); only the surfaced status/text is reconciled, and it
        // stays generic (invariant 3 — no tenant/column/SQL text on the wire). See the cross-op parity
        // fact MissingTenant_ReadAndWrite_SurfaceTheSameDeniedStatus.
        protected override string ExpectedWriteRejectionFragment(string canonicalServerFragment)
            => "The request was denied by policy.";

        public override async Task InitializeAsync()
        {
            await base.InitializeAsync();
            await BuildGrpcFrontDoorAsync();
        }

        public override async Task DisposeAsync()
        {
            _channel?.Dispose();
            if (_grpcHost is not null)
            {
                await _grpcHost.StopAsync();
                _grpcHost.Dispose();
            }
            await base.DisposeAsync();
        }

        /// <summary>
        /// Stands up the gRPC front door as a second in-process host whose DI binds the base fixture's
        /// real executors + shared auth factory, so reads/writes travel the base transformer pipeline
        /// and land the SQL in the kit's own capture observer.
        /// </summary>
        private async Task BuildGrpcFrontDoorAsync()
        {
            var baseReads = Host.Services.GetRequiredService<IQueryIntentExecutor>();
            var baseWrites = Host.Services.GetRequiredService<IMutationIntentExecutor>();

            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton(baseReads);
                    services.AddSingleton(baseWrites);
                    services.AddSingleton<IBifrostAuthContextFactory, HeaderTestAuthContextFactory>();
                    services.AddBifrostGrpc(o =>
                    {
                        o.Endpoint = EndpointPath;
                        o.EnableWrites = true;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapBifrostGrpc());
                });
            });
            _grpcHost = await builder.StartAsync();

            var handler = _grpcHost.GetTestServer().CreateHandler();
            _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });

            _model = await baseReads.GetModelAsync(EndpointPath);
            var visible = GrpcSchemaVisibility.ProjectAll(_model);
            var manifest = GrpcFieldNumberManifest.Empty().Reconcile(visible);
            var contract = GrpcSchemaGenerator.BuildContract(visible, manifest, true);
            _client = new GrpcWireTestClient(_channel.CreateCallInvoker(), contract);
        }

        protected override async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteReadAsync(
            ConformanceReadRequest request)
        {
            // A null principal models "no tenant identity": the wire cannot authenticate "nobody", so it
            // authenticates an identity carrying no tenant claim — the tenant transformer then fails closed.
            var headers = IdentityFor(request.Principal);
            var filter = BuildFilterJson(request.Filter);

            // List travels the same dynamic dispatch + read pipeline; a denied table (documents) or a
            // fail-closed tenant fault surfaces as an RpcException, which propagates so the kit's
            // fail-closed facts see the real rejection (never a swallowed error).
            var rows = await _client.ListAsync(request.Table, headers, filter: filter);
            return rows.Select(r => Project(r, request.Columns)).ToList();
        }

        protected override async Task<object?> ExecuteMutationAsync(ConformanceMutationRequest request)
        {
            var headers = IdentityFor(request.Principal);
            var table = _model.GetTableFromDbName(request.Table);
            var pkColumns = table.Columns.Where(c => c.IsPrimaryKey).Select(c => c.GraphQlName).ToList();

            switch (request.Action)
            {
                case ConformanceMutationAction.Insert:
                    return await _client.InsertAsync(request.Table, request.Data, headers);

                case ConformanceMutationAction.Update:
                {
                    var values = MergeKeyAndData(pkColumns, request.PrimaryKey, request.Data);
                    return await _client.UpdateAsync(request.Table, values, headers);
                }

                case ConformanceMutationAction.Delete:
                {
                    var key = MergeKeyAndData(pkColumns, request.PrimaryKey, new Dictionary<string, object?>());
                    return await _client.DeleteAsync(request.Table, key, headers);
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(request), request.Action, "unknown mutation action");
            }
        }

        /// <summary>Positionally binds the kit's primary-key values to the table's PK columns (composite-safe).</summary>
        private static IReadOnlyDictionary<string, object?> MergeKeyAndData(
            IReadOnlyList<string> pkColumns, IReadOnlyList<object?>? primaryKey, IReadOnlyDictionary<string, object?> data)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (primaryKey is not null)
            {
                if (primaryKey.Count != pkColumns.Count)
                    throw new InvalidOperationException(
                        $"Expected {pkColumns.Count} primary-key value(s) for [{string.Join(", ", pkColumns)}], got {primaryKey.Count}.");
                for (var i = 0; i < pkColumns.Count; i++)
                    values[pkColumns[i]] = primaryKey[i];
            }
            foreach (var kv in data)
                values[kv.Key] = kv.Value;
            return values;
        }

        /// <summary>Serializes the kit's GraphQL-shaped filter dictionary into the gRPC filter JSON string.</summary>
        private static string? BuildFilterJson(IReadOnlyDictionary<string, object?>? filter)
            => filter is { Count: > 0 } ? JsonSerializer.Serialize(filter) : null;

        /// <summary>
        /// Translates the kit's <see cref="ClaimsPrincipal"/> (or null → no-tenant identity) into the
        /// gRPC identity metadata the test auth factory projects. A null principal becomes an
        /// authenticated identity with no tenant claim — the wire equivalent of the kit's null principal.
        /// </summary>
        private static Metadata IdentityFor(ClaimsPrincipal? principal)
        {
            if (principal is null)
                return GrpcRealDbHarness.Identity("user-no-tenant");

            var user = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "user";
            var tenant = principal.FindFirst(LocalAuthClaims.Tenant)?.Value;
            return GrpcRealDbHarness.Identity(user, tenant);
        }

        private static IReadOnlyDictionary<string, object?> Project(
            IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> columns)
        {
            var record = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
            foreach (var column in columns)
                if (row.TryGetValue(column, out var value))
                    record[column] = value;
            return record;
        }

        /// <summary>
        /// Cross-op-class denial parity (slice-7 conformance derivation gap): the IDENTICAL
        /// missing-tenant fail-closed condition must surface the SAME sanitized gRPC status on a READ
        /// (List) and a WRITE (Insert) — not PERMISSION_DENIED on the read and a generic INTERNAL on the
        /// write. A differential status for one underlying condition is the anti-oracle/single-funnel
        /// class the S3 epic-close and OData single-funnel lessons warn against. Both still fail closed
        /// (the write lands nothing); this asserts only that the surfaced STATUS is reconciled, and that
        /// the reconciled write status stays generic (invariant 3 — no tenant/column/SQL text on the wire).
        /// </summary>
        [Fact]
        public async Task MissingTenant_ReadAndWrite_SurfaceTheSameDeniedStatus()
        {
            // Same "no tenant identity" condition on both op classes.
            var headers = GrpcRealDbHarness.Identity("user-no-tenant");

            var readFault = await Assert.ThrowsAsync<RpcException>(
                () => _client.ListAsync("orders", headers));

            var writeFault = await Assert.ThrowsAsync<RpcException>(
                () => _client.InsertAsync("orders", new Dictionary<string, object?> { ["name"] = "no-identity" }, headers));

            // Parity: the read denial and the write denial map to the SAME status through the single funnel.
            readFault.StatusCode.Should().Be(StatusCode.PermissionDenied);
            writeFault.StatusCode.Should().Be(
                readFault.StatusCode,
                "the identical missing-tenant condition must surface the same status on a write as on a read");
            writeFault.Status.Detail.Should().Be(readFault.Status.Detail);

            // The reconciled write status stays sanitized: the internal reason must not reach the wire.
            writeFault.Status.Detail.Should().NotContainAny("tenant_id", "Tenant context", "orders", "SQL");
        }
    }
}
