using FluentAssertions;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
using Xunit;

namespace BifrostQL.Integration.Test.Grpc
{
    /// <summary>
    /// Slice-7 criterion 2: an end-to-end loopback proof of the ASSEMBLED gRPC front door, driven from
    /// OUTSIDE the server assembly by a real <see cref="GrpcChannel"/> over the in-process HTTP/2
    /// handler against the full transformer pipeline + seeded SQLite. Proves — as one composed whole —
    /// reflection discovery, Get, filtered List, a cancellation-safe Stream, tenant isolation,
    /// soft-delete suppression, policy read-guarding, and parameterization. No production network
    /// dependency (criterion 5): in-proc SQLite + the in-memory handler.
    /// </summary>
    public sealed class GrpcLoopbackIntegrationTests : IAsyncLifetime
    {
        private GrpcLoopbackFixture _fixture = null!;
        private GrpcWireClient _client = null!;

        private static readonly string[] MetadataRules =
        {
            "*.orders { tenant-filter: tenant_id; soft-delete: deleted_at }",
            // documents: policy with an EMPTY read allow-list → the whole table is denied to a non-admin
            // (table-level invisibility). profiles: read IS allowed but the ssn column is read-denied
            // (column-level invisibility). The two prove both halves of invariant 4 end to end.
            "*.documents { policy-read-deny: body }",
            "*.profiles { policy-actions: read; policy-read-deny: ssn }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS orders",
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, tenant_id TEXT NOT NULL, name TEXT NOT NULL, deleted_at TEXT NULL)",
            """
            INSERT INTO orders(id, tenant_id, name, deleted_at) VALUES
                (1,'tenant-a','a-first',NULL),(2,'tenant-a','a-second',NULL),
                (3,'tenant-b','b-only',NULL),(4,'tenant-a','a-deleted','2026-01-01T00:00:00Z')
            """,
            "DROP TABLE IF EXISTS documents",
            "CREATE TABLE documents (id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL)",
            "INSERT INTO documents(id, title, body) VALUES (1,'public title','secret body')",
            "DROP TABLE IF EXISTS profiles",
            "CREATE TABLE profiles (id INTEGER PRIMARY KEY, name TEXT NOT NULL, ssn TEXT NOT NULL)",
            "INSERT INTO profiles(id, name, ssn) VALUES (1,'alice','111-22-3333')",
        };

        public async Task InitializeAsync()
        {
            _fixture = await GrpcLoopbackFixture.StartAsync(nameof(GrpcLoopbackIntegrationTests), MetadataRules, SeedSql);
            _client = new GrpcWireClient(_fixture.Invoker, _fixture.Contract);
        }

        public async Task DisposeAsync() => await _fixture.DisposeAsync();

        private static Metadata Tenant(string tenant) => GrpcLoopbackFixture.Identity("u", tenant, "member");

        // ---- reflection ----

        [Fact]
        public async Task Reflection_advertises_the_generated_service_and_a_permitted_table()
        {
            var client = new ServerReflection.ServerReflectionClient(_fixture.Channel);

            var services = await ReflectAsync(client, Tenant("tenant-a"), new ServerReflectionRequest { ListServices = "" });
            services.ListServicesResponse.Service.Select(s => s.Name).Should().Contain("bifrostql.BifrostQuery");

            var surface = await ReflectAsync(
                client, Tenant("tenant-a"), new ServerReflectionRequest { FileContainingSymbol = "bifrostql.BifrostQuery" });
            var (methods, _) = Surface(surface);
            methods.Should().Contain(new[] { "Getorders", "Listorders", "Streamorders" });
        }

        [Fact]
        public async Task Reflection_hides_a_policy_denied_column_and_a_denied_table_end_to_end()
        {
            var client = new ServerReflection.ServerReflectionClient(_fixture.Channel);
            var surface = await ReflectAsync(
                client, Tenant("tenant-a"), new ServerReflectionRequest { FileContainingSymbol = "bifrostql.BifrostQuery" });

            // Column-level (invariant 4): profiles is readable, but the read-denied ssn column never
            // appears in the discoverable row message — while its sibling column does.
            var profile = FieldNames(surface, "profilesRow");
            profile.Should().Contain("name").And.NotContain("ssn", "a policy-denied column must never appear in the discoverable surface");

            // Table-level (invariant 4): documents has an empty read allow-list, so the whole table —
            // message AND methods — is absent for the non-admin, indistinguishable from a table that
            // does not exist (no information-disclosure oracle).
            var (methods, messages) = Surface(surface);
            messages.Should().NotContain("documentsRow");
            methods.Should().NotContain("Getdocuments");
        }

        // ---- Get ----

        [Fact]
        public async Task Get_returns_the_addressed_row_for_the_owning_tenant()
        {
            var row = await _client.GetAsync("orders", new Dictionary<string, object?> { ["id"] = 1 }, Tenant("tenant-a"));
            row!["name"].Should().Be("a-first");
        }

        // ---- filtered List + parameterization ----

        [Fact]
        public async Task Filtered_list_narrows_within_tenant_scope()
        {
            var rows = await _client.ListAsync("orders", Tenant("tenant-a"), filter: """{ "name": { "_eq": "a-second" } }""");
            rows.Should().ContainSingle(r => (string)r["name"]! == "a-second");
        }

        // ---- cancellation-safe Stream ----

        [Fact]
        public async Task A_cancelled_stream_faults_cleanly_without_hanging()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // pre-cancelled: the stream must fault promptly, never hang or leak rows

            var act = () => _client.StreamAsync("orders", Tenant("tenant-a"), cts.Token);

            var ex = await act.Should().ThrowAsync<Exception>();
            ex.Which.Should().Match(e => e is OperationCanceledException || e is RpcException);
        }

        [Fact]
        public async Task An_uncancelled_stream_yields_the_tenants_rows()
        {
            var rows = await _client.StreamAsync("orders", Tenant("tenant-a"));
            rows.Select(r => Convert.ToInt32(r["id"])).Should().BeEquivalentTo(new[] { 1, 2 });
        }

        // ---- tenant isolation + soft-delete ----

        [Fact]
        public async Task Tenant_isolation_and_soft_delete_hold_end_to_end()
        {
            var tenantA = await _client.ListAsync("orders", Tenant("tenant-a"));
            // tenant-a sees only its own live rows; the soft-deleted a-deleted row never surfaces…
            tenantA.Select(r => (string)r["name"]!).Should().BeEquivalentTo("a-first", "a-second");
            tenantA.Select(r => (string)r["name"]!).Should().NotContain("a-deleted");

            // …and tenant-b's rows are invisible to tenant-a and vice versa.
            var tenantB = await _client.ListAsync("orders", Tenant("tenant-b"));
            tenantB.Select(r => (string)r["name"]!).Should().BeEquivalentTo("b-only");

            // A cross-tenant Get is indistinguishable from a missing row (null) — no existence oracle.
            (await _client.GetAsync("orders", new Dictionary<string, object?> { ["id"] = 3 }, Tenant("tenant-a")))
                .Should().BeNull();
        }

        // ---- policy read guard + cross-op-class status parity ----

        [Fact]
        public async Task A_hidden_column_filter_is_rejected_the_same_on_list_and_stream()
        {
            // Filtering a policy-denied column is a hidden/unknown field on BOTH read op classes; the
            // SAME condition must map to the SAME status (no read-vs-read op-class oracle).
            const string filter = """{ "body": { "_eq": "secret body" } }""";

            var listStatus = await StatusOf(() => _client.ListAsync("documents", Tenant("tenant-a"), filter: filter));
            var streamStatus = await StatusOf(() => _client.StreamAsync("documents", Tenant("tenant-a"), default, filter: filter));

            listStatus.Should().Be(StatusCode.InvalidArgument);
            streamStatus.Should().Be(listStatus, "the same denied-column condition must surface identically across op classes");
        }

        // ---- helpers ----

        private static async Task<StatusCode> StatusOf(Func<Task> call)
        {
            var ex = await Assert.ThrowsAnyAsync<RpcException>(call);
            return ex.StatusCode;
        }

        private static async Task<ServerReflectionResponse> ReflectAsync(
            ServerReflection.ServerReflectionClient client, Metadata identity, ServerReflectionRequest request)
        {
            using var call = client.ServerReflectionInfo(identity);
            await call.RequestStream.WriteAsync(request);
            await call.RequestStream.CompleteAsync();
            ServerReflectionResponse? last = null;
            await foreach (var response in call.ResponseStream.ReadAllAsync())
                last = response;
            return last!;
        }

        private static (IReadOnlyList<string> Methods, IReadOnlyList<string> Messages) Surface(ServerReflectionResponse response)
        {
            var methods = new List<string>();
            var messages = new List<string>();
            foreach (var bytes in response.FileDescriptorResponse.FileDescriptorProto)
            {
                var file = FileDescriptorProto.Parser.ParseFrom(bytes);
                foreach (var service in file.Service)
                    methods.AddRange(service.Method.Select(m => m.Name));
                messages.AddRange(file.MessageType.Select(m => m.Name));
            }
            return (methods, messages);
        }

        private static IReadOnlyList<string> FieldNames(ServerReflectionResponse response, string messageName)
        {
            foreach (var bytes in response.FileDescriptorResponse.FileDescriptorProto)
            {
                var file = FileDescriptorProto.Parser.ParseFrom(bytes);
                var message = file.MessageType.FirstOrDefault(m => m.Name == messageName);
                if (message is not null)
                    return message.Field.Select(f => f.Name).ToList();
            }
            return Array.Empty<string>();
        }
    }
}
