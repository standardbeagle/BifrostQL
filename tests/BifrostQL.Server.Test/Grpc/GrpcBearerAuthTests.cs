using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Slice 4: bearer-metadata identity projected through the SHARED <c>IBifrostAuthContextFactory</c>
    /// and fail-closed status mapping. Every credential fault (missing, malformed, unmapped issuer,
    /// subject-less, oversized) fails closed as UNAUTHENTICATED BEFORE any intent is built — there is
    /// no path to the executor with a permissive identity. Validation faults surface a
    /// google.rpc.BadRequest referencing only request fields; row-addressed Get hides authorization
    /// denial as NOT_FOUND; and identity A never reads identity B's rows on Get/List/Stream.
    /// </summary>
    public sealed class GrpcBearerAuthTests : IAsyncLifetime
    {
        private GrpcRealDbHarness _harness = null!;
        private GrpcWireTestClient _client = null!;

        private static readonly string[] MetadataRules =
        {
            "*.orders { tenant-filter: tenant_id }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL)",
            """
            INSERT INTO orders(id, tenant_id, name) VALUES
                (1,'tenant-a','a-first'),(2,'tenant-a','a-second'),
                (3,'tenant-b','b-first'),(4,'tenant-b','b-second'),(5,'tenant-b','b-third')
            """,
        };

        public async Task InitializeAsync()
        {
            _harness = await GrpcRealDbHarness.StartAsync(nameof(GrpcBearerAuthTests), MetadataRules, SeedSql);
            _client = new GrpcWireTestClient(_harness.Invoker, _harness.Contract);
        }

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static Metadata Tenant(string tenant) => GrpcRealDbHarness.Identity("u", tenant, "member");

        private static Metadata IdentityHeader(string raw)
        {
            var md = new Metadata();
            md.Add(GrpcRealDbHarness.IdentityHeader, raw);
            return md;
        }

        // ---- criterion 1: every bad/absent credential fails closed BEFORE intent, never anonymous ----

        [Fact]
        public async Task Missing_credential_fails_closed_before_intent()
        {
            var act = () => _client.ListAsync("orders", new Metadata());

            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _harness.CapturedSql("orders").Should().BeEmpty();
        }

        [Fact]
        public async Task Malformed_bearer_that_authenticates_to_nothing_fails_closed()
        {
            // An unrecognized bearer produces no principal → the shared factory projects an EMPTY
            // context → fail closed, never anonymous data.
            var headers = new Metadata { { "authorization", "Bearer not-a-real-token" } };

            var act = () => _client.ListAsync("orders", headers);

            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _harness.CapturedSql("orders").Should().BeEmpty();
        }

        [Fact]
        public async Task Unmapped_issuer_fails_closed_without_revealing_configured_issuers()
        {
            var act = () => _client.ListAsync("orders", IdentityHeader(HeaderTestAuthContextFactory.UnmappedIssuerSentinel));

            var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
            ex.StatusCode.Should().Be(StatusCode.Unauthenticated);
            // No projection detail (issuer identity, claim shape) may reach the wire (invariant 3).
            ex.Status.Detail.Should().NotContain("issuer").And.NotContain("OIDC");
            _harness.CapturedSql("orders").Should().BeEmpty();
        }

        [Fact]
        public async Task Subjectless_principal_fails_closed()
        {
            // "|tenant-a|member" → authenticated but no subject → the factory throws → fail closed.
            var act = () => _client.ListAsync("orders", IdentityHeader("|tenant-a|member"));

            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _harness.CapturedSql("orders").Should().BeEmpty();
        }

        // ---- criterion 5: oversized metadata → clean fail-closed, not a crash/unbounded alloc ----

        [Fact]
        public async Task Oversized_authorization_metadata_fails_closed_cleanly()
        {
            var huge = "Bearer " + new string('x', 12_000);
            var headers = new Metadata { { "authorization", huge } };

            var act = () => _client.ListAsync("orders", headers);

            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
            _harness.CapturedSql("orders").Should().BeEmpty();
        }

        // ---- criterion 5: repeated failures — no state corruption, no oracle from details ----

        [Fact]
        public async Task Repeated_auth_failures_are_stateless_and_identical()
        {
            var details = new List<string>();
            for (var i = 0; i < 5; i++)
            {
                var ex = (await ((Func<Task>)(() => _client.ListAsync("orders", new Metadata())))
                    .Should().ThrowAsync<RpcException>()).Which;
                ex.StatusCode.Should().Be(StatusCode.Unauthenticated);
                details.Add(ex.Status.Detail);
            }

            // Every failure is byte-identical — no timing/detail signal accumulates across attempts.
            details.Distinct().Should().HaveCount(1);

            // A valid caller still succeeds afterwards: no corrupted/poisoned state.
            var ok = await _client.ListAsync("orders", Tenant("tenant-a"));
            ok.Select(r => Convert.ToInt32(r["id"])).Should().BeEquivalentTo(new[] { 1, 2 });
        }

        // ---- criterion 4: validation → google.rpc.BadRequest referencing ONLY request fields ----

        [Fact]
        public async Task Missing_request_field_maps_to_bad_request_with_only_request_field_names()
        {
            // Valid identity (passes auth), but the Get omits the primary-key request field.
            var act = () => _client.GetAsync("orders", new Dictionary<string, object?>(), Tenant("tenant-a"));

            var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
            ex.StatusCode.Should().Be(StatusCode.InvalidArgument);

            var violations = DecodeFieldViolations(ex);
            violations.Should().ContainSingle();
            violations[0].Field.Should().Be("id", "the violation must name the request field");

            // No internal column/table/SQL detail may appear anywhere in the rich error (invariant 3).
            var all = ex.Status.Detail + "|" + violations[0].Field + "|" + violations[0].Description;
            all.Should().NotContain("SELECT").And.NotContain("tenant_id")
                .And.NotContain("dbo").And.NotContain("orders");
        }

        // ---- criterion 3 + 5: cross-tenant isolation on Get / List / Stream ----

        [Fact]
        public async Task Cross_tenant_isolation_holds_on_get_list_and_stream()
        {
            // Get: tenant-a asking for a tenant-b row is indistinguishable from a missing row (null).
            (await _client.GetAsync("orders", new Dictionary<string, object?> { ["id"] = 3 }, Tenant("tenant-a")))
                .Should().BeNull();
            (await _client.GetAsync("orders", new Dictionary<string, object?> { ["id"] = 1 }, Tenant("tenant-a")))!
                ["name"].Should().Be("a-first");

            // List: disjoint sets.
            (await _client.ListAsync("orders", Tenant("tenant-a"))).Select(r => Convert.ToInt32(r["id"]))
                .Should().BeEquivalentTo(new[] { 1, 2 });
            (await _client.ListAsync("orders", Tenant("tenant-b"))).Select(r => Convert.ToInt32(r["id"]))
                .Should().BeEquivalentTo(new[] { 3, 4, 5 });

            // Stream: same isolation over the streaming RPC class.
            (await _client.StreamAsync("orders", Tenant("tenant-a"))).Select(r => Convert.ToInt32(r["id"]))
                .Should().BeEquivalentTo(new[] { 1, 2 });
            (await _client.StreamAsync("orders", Tenant("tenant-b"))).Select(r => Convert.ToInt32(r["id"]))
                .Should().BeEquivalentTo(new[] { 3, 4, 5 });
        }

        // ---- criterion 5: deadlines ----

        [Fact]
        public async Task Expired_deadline_faults_without_returning_rows()
        {
            var act = () => _client.GetAsync(
                "orders", new Dictionary<string, object?> { ["id"] = 1 }, Tenant("tenant-a"),
                deadline: DateTime.UtcNow.AddMilliseconds(-1));

            var ex = (await act.Should().ThrowAsync<RpcException>()).Which;
            ex.StatusCode.Should().BeOneOf(StatusCode.DeadlineExceeded, StatusCode.Cancelled);
        }

        // ---- helper: hand-decode the grpc-status-details-bin google.rpc.Status → BadRequest ----

        private static IReadOnlyList<(string Field, string Description)> DecodeFieldViolations(RpcException ex)
        {
            var statusBytes = ex.Trailers.GetValueBytes("grpc-status-details-bin");
            statusBytes.Should().NotBeNull("a validation fault must carry the rich status-details trailer");

            // google.rpc.Status { int32 code=1; string message=2; repeated Any details=3; }
            var input = new CodedInputStream(statusBytes);
            byte[]? anyBytes = null;
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag >> 3)
                {
                    case 1: input.ReadInt32(); break;
                    case 2: input.ReadString(); break;
                    case 3: anyBytes = input.ReadBytes().ToByteArray(); break;
                    default: input.SkipLastField(); break;
                }
            }
            anyBytes.Should().NotBeNull();

            // google.protobuf.Any { string type_url=1; bytes value=2; }
            var anyInput = new CodedInputStream(anyBytes);
            byte[]? badRequestBytes = null;
            while ((tag = anyInput.ReadTag()) != 0)
            {
                switch (tag >> 3)
                {
                    case 1: anyInput.ReadString(); break;
                    case 2: badRequestBytes = anyInput.ReadBytes().ToByteArray(); break;
                    default: anyInput.SkipLastField(); break;
                }
            }
            badRequestBytes.Should().NotBeNull();

            // google.rpc.BadRequest { repeated FieldViolation field_violations=1; }
            var brInput = new CodedInputStream(badRequestBytes);
            var result = new List<(string, string)>();
            while ((tag = brInput.ReadTag()) != 0)
            {
                if (tag >> 3 == 1)
                    result.Add(DecodeViolation(brInput.ReadBytes().ToByteArray()));
                else
                    brInput.SkipLastField();
            }
            return result;
        }

        private static (string Field, string Description) DecodeViolation(byte[] bytes)
        {
            // FieldViolation { string field=1; string description=2; }
            var input = new CodedInputStream(bytes);
            string field = "", description = "";
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag >> 3)
                {
                    case 1: field = input.ReadString(); break;
                    case 2: description = input.ReadString(); break;
                    default: input.SkipLastField(); break;
                }
            }
            return (field, description);
        }
    }
}
