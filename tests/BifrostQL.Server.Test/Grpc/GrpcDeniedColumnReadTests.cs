using FluentAssertions;
using Grpc.Core;
using Xunit;
using GrpcMetadata = Grpc.Core.Metadata;

namespace BifrostQL.Server.Test.Grpc
{
    /// <summary>
    /// A gRPC read has no column selection: it returns every column the caller may read. Both row
    /// builders selected every column in the TABLE instead, so one <c>policy-read-deny</c> column
    /// made the column guard refuse the whole read and a caller who could read every other column
    /// got PERMISSION_DENIED (List, Stream) or NOT_FOUND (Get). The selection now comes from the
    /// caller's read projection, so the denied column is simply absent from each row.
    /// </summary>
    public sealed class GrpcDeniedColumnReadTests
    {
        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS documents",
            "CREATE TABLE documents (id INTEGER PRIMARY KEY, title TEXT NOT NULL, body TEXT NOT NULL)",
            "INSERT INTO documents(id, title, body) VALUES (1,'first','secret-1'),(2,'second','secret-2')",
        };

        // policy-actions: read makes the TABLE readable, so only the body column is denied.
        private static readonly string[] DeniedBody =
        {
            "*.documents { policy-actions: read; policy-read-deny: body }",
        };

        private static GrpcMetadata User() => GrpcRealDbHarness.Identity("u1", roles: "member");

        [Fact]
        public async Task List_Stream_and_Get_return_the_readable_columns_and_omit_the_denied_one()
        {
            await using var harness = await GrpcRealDbHarness.StartAsync("denied-column-read", DeniedBody, SeedSql);
            var client = new GrpcWireTestClient(harness.Invoker, harness.Contract);

            var listed = await client.ListAsync("documents", User(), orderBy: "id asc");
            listed.Select(r => r["title"]).Should().Equal("first", "second");
            listed.Should().OnlyContain(r => !r.ContainsKey("body"),
                "the denied column never crosses the wire");

            var streamed = await client.StreamAsync("documents", User(), orderBy: "id asc");
            streamed.Select(r => r["title"]).Should().Equal("first", "second");
            streamed.Should().OnlyContain(r => !r.ContainsKey("body"));

            var got = await client.GetAsync("documents", new Dictionary<string, object?> { ["id"] = 1L }, User());
            got.Should().NotBeNull("the row is readable; only one of its columns is denied");
            got!["title"].Should().Be("first");
            got.ContainsKey("body").Should().BeFalse();
        }

        [Fact]
        public async Task A_wholly_denied_table_is_still_refused_by_the_pipeline()
        {
            // Control: the projection narrows columns only. A table the caller may not read at all
            // still reaches the pipeline and gets its authoritative PERMISSION_DENIED (invariant 10).
            // No policy-actions: the evaluator's empty AllowedActions denies the table wholesale.
            var deniedTable = new[] { "*.documents { policy-read-deny: body }" };
            await using var harness = await GrpcRealDbHarness.StartAsync("denied-table-read", deniedTable, SeedSql);
            var client = new GrpcWireTestClient(harness.Invoker, harness.Contract);

            // No orderBy: a denied table exposes no visible columns, so naming one would be refused
            // as an unknown field before the pipeline runs.
            var act = () => client.ListAsync("documents", User());
            (await act.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        }
    }
}
