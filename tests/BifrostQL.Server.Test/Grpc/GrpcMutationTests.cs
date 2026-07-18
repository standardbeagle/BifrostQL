using BifrostQL.Server.Grpc;
using FluentAssertions;
using Grpc.Core;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Slice 6: the opt-in gRPC WRITE surface (Insert/Update/Delete) through
    /// <c>IMutationIntentExecutor</c>. Writes default OFF and are gated a SECOND time per-table by the
    /// <c>grpc-write: enabled</c> allow-list, so a disabled or non-allow-listed table's mutation RPCs
    /// are absent from dispatch AND reflection (UNIMPLEMENTED, indistinguishable from an unknown
    /// method — no oracle). Every enabled write routes through the full mutation pipeline under the
    /// caller's identity: the adapter builds no SQL/predicate, tenant scope narrows an out-of-tenant
    /// PK to ZERO rows (reported the same as an absent row), and a scoped-away write is detected via
    /// <c>AffectedRows</c>, never the intent's <c>Value</c> (the KEY on a single-key table). Fixtures
    /// span single-PK, composite-PK, and PK value 0.
    /// </summary>
    public sealed class GrpcMutationTests : IAsyncLifetime
    {
        private GrpcRealDbHarness _harness = null!;
        private GrpcWireTestClient _client = null!;

        private static readonly string[] MetadataRules =
        {
            "*.widgets { grpc-write: enabled }",
            "*.order_lines { grpc-write: enabled }",
            "*.orders { tenant-filter: tenant_id; grpc-write: enabled }",
            // readonly_t deliberately carries NO grpc-write metadata.
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            // Includes PK value 0 — the fixture that catches a guard built on Value instead of AffectedRows.
            "INSERT INTO widgets(id, name) VALUES (0,'zero'),(1,'first'),(2,'second')",
            "DROP TABLE IF EXISTS order_lines",
            "CREATE TABLE order_lines (order_id INTEGER NOT NULL, line_no INTEGER NOT NULL, qty INTEGER NOT NULL, PRIMARY KEY (order_id, line_no))",
            "INSERT INTO order_lines(order_id, line_no, qty) VALUES (10,1,7),(10,2,9)",
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL)",
            "INSERT INTO orders(id, tenant_id, name) VALUES (1,'tenant-a','a1'),(2,'tenant-b','b1')",
            "DROP TABLE IF EXISTS readonly_t",
            "CREATE TABLE readonly_t (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "INSERT INTO readonly_t(id, name) VALUES (1,'ro')",
        };

        public async Task InitializeAsync()
        {
            _harness = await GrpcRealDbHarness.StartAsync(
                nameof(GrpcMutationTests), MetadataRules, SeedSql,
                new GrpcWireOptions { EnableWrites = true });
            _client = new GrpcWireTestClient(_harness.Invoker, _harness.Contract);
        }

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static Metadata User() => GrpcRealDbHarness.Identity("u1", roles: "member");
        private static Metadata Tenant(string tenant) => GrpcRealDbHarness.Identity("u", tenant, "member");

        // ---- criterion 2: writes route through IMutationIntentExecutor (single + composite PK) ----

        [Fact]
        public async Task Insert_routes_through_the_mutation_pipeline()
        {
            var result = await _client.InsertAsync(
                "widgets", new Dictionary<string, object?> { ["name"] = "inserted" }, User());

            result.AffectedRows.Should().Be(1);
            result.ReturnedKey.Should().NotBeNullOrEmpty();

            // The write travelled the real pipeline — read the row back by its generated key.
            var id = Convert.ToInt32(result.ReturnedKey);
            var row = await _client.GetAsync("widgets", new Dictionary<string, object?> { ["id"] = id }, User());
            row!["name"].Should().Be("inserted");
            _harness.CapturedSql("widgets").Should().NotBeEmpty();
        }

        [Fact]
        public async Task Insert_binds_all_columns_of_a_composite_primary_key()
        {
            var result = await _client.InsertAsync(
                "order_lines",
                new Dictionary<string, object?> { ["order_id"] = 10, ["line_no"] = 3, ["qty"] = 99 },
                User());

            result.AffectedRows.Should().Be(1);

            var row = await _client.GetAsync(
                "order_lines", new Dictionary<string, object?> { ["order_id"] = 10, ["line_no"] = 3 }, User());
            Convert.ToInt32(row!["qty"]).Should().Be(99);
        }

        // ---- criterion 4: Update reports the REAL affected count via AffectedRows, incl. PK value 0 ----

        [Fact]
        public async Task Update_reports_affected_rows_even_for_primary_key_value_zero()
        {
            // The row's PK is 0. A guard reading the intent's Value (which IS the key, 0) as a count
            // would MISFIRE and report a no-op; AffectedRows reports the true 1.
            var result = await _client.UpdateAsync(
                "widgets", new Dictionary<string, object?> { ["id"] = 0, ["name"] = "zeroed" }, User());

            result.AffectedRows.Should().Be(1);

            var row = await _client.GetAsync("widgets", new Dictionary<string, object?> { ["id"] = 0 }, User());
            row!["name"].Should().Be("zeroed");
        }

        [Fact]
        public async Task Update_composite_primary_key_sets_the_addressed_row()
        {
            var result = await _client.UpdateAsync(
                "order_lines",
                new Dictionary<string, object?> { ["order_id"] = 10, ["line_no"] = 2, ["qty"] = 50 },
                User());

            result.AffectedRows.Should().Be(1);

            var row = await _client.GetAsync(
                "order_lines", new Dictionary<string, object?> { ["order_id"] = 10, ["line_no"] = 2 }, User());
            Convert.ToInt32(row!["qty"]).Should().Be(50);
        }

        [Fact]
        public async Task Update_out_of_tenant_affects_zero_rows_without_an_existence_oracle()
        {
            // Own row updates.
            var own = await _client.UpdateAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 1, ["name"] = "a-renamed" }, Tenant("tenant-a"));
            own.AffectedRows.Should().Be(1);

            // Another tenant's row: the pipeline scopes it away — ZERO rows, not a denial.
            var crossTenant = await _client.UpdateAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 2, ["name"] = "hijack" }, Tenant("tenant-a"));

            // A genuinely-absent row.
            var absent = await _client.UpdateAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 999, ["name"] = "ghost" }, Tenant("tenant-a"));

            crossTenant.AffectedRows.Should().Be(0);
            absent.AffectedRows.Should().Be(0);
            crossTenant.AffectedRows.Should().Be(absent.AffectedRows,
                "a scoped-away write reports the SAME as an absent row — no existence oracle");

            // The victim row is untouched.
            var victim = await _client.GetAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 2 }, Tenant("tenant-b"));
            victim!["name"].Should().Be("b1");
        }

        // ---- criterion 5: Delete routes a Delete intent; pipeline decides hard vs soft ----

        [Fact]
        public async Task Delete_routes_a_delete_intent_through_the_pipeline()
        {
            var result = await _client.DeleteAsync(
                "widgets", new Dictionary<string, object?> { ["id"] = 2 }, User());

            result.AffectedRows.Should().Be(1);

            // No soft-delete metadata → the pipeline hard-deletes: the row is gone.
            var row = await _client.GetAsync("widgets", new Dictionary<string, object?> { ["id"] = 2 }, User());
            row.Should().BeNull();
        }

        [Fact]
        public async Task Delete_out_of_tenant_affects_zero_rows()
        {
            var crossTenant = await _client.DeleteAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 2 }, Tenant("tenant-a"));
            crossTenant.AffectedRows.Should().Be(0);

            var own = await _client.DeleteAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 1 }, Tenant("tenant-a"));
            own.AffectedRows.Should().Be(1);

            // The other tenant's row survived the cross-tenant delete attempt.
            var survivor = await _client.GetAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 2 }, Tenant("tenant-b"));
            survivor.Should().NotBeNull();
        }

        // ---- criterion 1/2: identity is fail-closed before any write intent ----

        [Fact]
        public async Task Anonymous_write_fails_closed_before_any_intent()
        {
            var act = () => _client.InsertAsync(
                "widgets", new Dictionary<string, object?> { ["name"] = "anon" }, new Metadata());

            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _harness.CapturedSql("widgets").Should().BeEmpty();
        }

        // ---- criterion 4: concurrency/validation faults are sanitized through the single funnel ----

        [Fact]
        public async Task Constraint_violation_is_sanitized_never_leaking_schema_or_sql()
        {
            // Insert an explicit PK that already exists → a UNIQUE constraint fault. It must surface as
            // a sanitized status, never the driver/schema text (invariant 3).
            var act = () => _client.InsertAsync(
                "widgets", new Dictionary<string, object?> { ["id"] = 1, ["name"] = "dup" }, User());

            var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
            ex.StatusCode.Should().Be(StatusCode.Internal);
            ex.Status.Detail.Should().NotContainEquivalentOf("unique")
                .And.NotContainEquivalentOf("constraint")
                .And.NotContainEquivalentOf("widgets")
                .And.NotContainEquivalentOf("sqlite");
        }

        // ---- criterion 3: a non-allow-listed table's mutation RPCs are unprobeable ----

        [Fact]
        public async Task Non_allow_listed_table_mutation_rpc_is_indistinguishable_from_an_unknown_method()
        {
            // readonly_t is published (readable) but NOT write-allow-listed: its Insert RPC is never
            // generated, so it is UNIMPLEMENTED — the SAME as a method for a table that does not exist.
            var notAllowListed = () => _client.RawUnaryAsync("Insertreadonly_t", Array.Empty<byte>(), User());
            var unknownMethod = () => _client.RawUnaryAsync("InsertNoSuchTable", Array.Empty<byte>(), User());

            (await notAllowListed.Should().ThrowAsync<RpcException>()).Which.StatusCode
                .Should().Be(StatusCode.Unimplemented);
            (await unknownMethod.Should().ThrowAsync<RpcException>()).Which.StatusCode
                .Should().Be(StatusCode.Unimplemented);

            // A read of the same table still works — the allow-list gates writes only, not reads.
            var row = await _client.GetAsync("readonly_t", new Dictionary<string, object?> { ["id"] = 1 }, User());
            row!["name"].Should().Be("ro");
        }

        // ---- criterion 1: writes disabled → the whole mutation surface is absent, builds zero intent ----

        [Fact]
        public async Task Disabled_writes_make_every_mutation_rpc_unprobeable_and_build_no_intent()
        {
            await using var disabled = await GrpcRealDbHarness.StartAsync(
                nameof(Disabled_writes_make_every_mutation_rpc_unprobeable_and_build_no_intent),
                MetadataRules, SeedSql,
                new GrpcWireOptions { EnableWrites = false });
            var client = new GrpcWireTestClient(disabled.Invoker, disabled.Contract);

            // Even an allow-listed table's Insert is absent when the global switch is off.
            var act = () => client.RawUnaryAsync("Insertwidgets", Array.Empty<byte>(), User());

            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unimplemented);
            // Fail-closed by construction: no handler ran, so no intent/SQL was built.
            disabled.CapturedSql("widgets").Should().BeEmpty();
        }
    }
}
