using FluentAssertions;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Reflection.V1Alpha;
using Xunit;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// Criterion 2 (protocol-adapter-security invariant 4): server reflection lists EXACTLY the
    /// PERMITTED surface so a client works without local protos, and it is filtered by the SAME read
    /// policy the query path enforces. A table denied to the caller is ABSENT from what reflection
    /// returns — its method/message never appear, and a direct symbol lookup for it is NOT_FOUND,
    /// indistinguishable from an unknown symbol (no information-disclosure oracle).
    /// </summary>
    public sealed class GrpcReflectionTests : IAsyncLifetime
    {
        private GrpcRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "*.secrets { policy-actions: create }", // no "read" → non-admin denied
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS widgets",
            "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
            "DROP TABLE IF EXISTS secrets",
            "CREATE TABLE secrets (id INTEGER PRIMARY KEY, code TEXT NOT NULL)",
        };

        public async Task InitializeAsync()
            => _harness = await GrpcRealDbHarness.StartAsync(nameof(GrpcReflectionTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        private static Metadata Admin() => GrpcRealDbHarness.Identity("admin", roles: "admin");
        private static Metadata Member() => GrpcRealDbHarness.Identity("member", roles: "member");

        private async Task<ServerReflectionResponse> ReflectAsync(Metadata identity, ServerReflectionRequest request)
        {
            var client = new ServerReflection.ServerReflectionClient(_harness.Channel);
            using var call = client.ServerReflectionInfo(identity);
            await call.RequestStream.WriteAsync(request);
            await call.RequestStream.CompleteAsync();

            ServerReflectionResponse? last = null;
            await foreach (var response in call.ResponseStream.ReadAllAsync())
                last = response;
            return last!;
        }

        private static (IReadOnlyList<string> Methods, IReadOnlyList<string> Messages) Surface(
            ServerReflectionResponse response)
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

        [Fact]
        public async Task List_services_advertises_the_generated_service()
        {
            var response = await ReflectAsync(Admin(), new ServerReflectionRequest { ListServices = "" });

            response.ListServicesResponse.Service.Select(s => s.Name)
                .Should().Contain("bifrostql.BifrostQuery");
        }

        [Fact]
        public async Task Admin_reflection_includes_the_policy_restricted_table()
        {
            var response = await ReflectAsync(
                Admin(), new ServerReflectionRequest { FileContainingSymbol = "bifrostql.BifrostQuery" });

            var (methods, messages) = Surface(response);
            // SQLite table names are lowercase, so the generated GraphQL/method names are too.
            methods.Should().Contain("Getsecrets");
            messages.Should().Contain("secretsRow");
            methods.Should().Contain("Getwidgets");
        }

        [Fact]
        public async Task Member_reflection_omits_the_denied_table_but_keeps_permitted_ones()
        {
            var response = await ReflectAsync(
                Member(), new ServerReflectionRequest { FileContainingSymbol = "bifrostql.BifrostQuery" });

            var (methods, messages) = Surface(response);
            // The denied table vanishes entirely from what the member can discover…
            methods.Should().NotContain("Getsecrets");
            messages.Should().NotContain("secretsRow");
            // …while the permitted table remains.
            methods.Should().Contain("Getwidgets");
        }

        [Fact]
        public async Task Denied_symbol_lookup_is_not_found_like_an_unknown_symbol()
        {
            var response = await ReflectAsync(
                Member(), new ServerReflectionRequest { FileContainingSymbol = "bifrostql.secretsRow" });

            // A denied symbol is reported exactly like a genuinely unknown one — no existence oracle.
            response.MessageResponseCase.Should().Be(ServerReflectionResponse.MessageResponseOneofCase.ErrorResponse);
            response.ErrorResponse.ErrorCode.Should().Be((int)StatusCode.NotFound);
        }
    }
}
