using System.IO.Pipelines;
using System.Text.Json;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Table identity on the MCP schema surface (<c>bifrost_describe_table</c> and the
    /// <c>bifrost://schema/{table}</c> resources): a client-supplied name resolves by the
    /// client-name rule, never by a first match on the bare DB name. Before the fix,
    /// <c>SchemaReadVisibility.Find(visible, string)</c> took the first visible table whose
    /// DbName matched, so with <c>sales.orders</c> and <c>hr.orders</c> both readable,
    /// <c>orders</c> always described one of them, the other could not be described at all, and
    /// the resource list published two identical URIs.
    ///
    /// <para>The fixture is the bug's minimal repro: two READABLE tables sharing a bare name in
    /// two schemas, with different columns so the assertion can tell which one answered, plus a
    /// third same-named table the anonymous caller may not read (<c>ops.orders</c>).</para>
    /// </summary>
    public sealed class McpSchemaTableIdentityTests
    {
        private static DbTable Table(string schema, string name, string column, string? policyActions = null)
        {
            var columns = new[]
            {
                new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
                new ColumnDto { ColumnName = column, GraphQlName = column, DataType = "nvarchar", OrdinalPosition = 2 },
            };
            return new DbTable
            {
                DbName = name,
                GraphQlName = $"{schema}_{name}",
                NormalizedName = name,
                TableSchema = schema,
                TableType = "BASE TABLE",
                ColumnLookup = columns.ToDictionary(c => c.DbName, StringComparer.OrdinalIgnoreCase),
                GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
                Metadata = policyActions is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { [MetadataKeys.Policy.Actions] = policyActions },
            };
        }

        private static IDbModel Model(bool withDeniedTwin = true) => new DbModel
        {
            Tables = withDeniedTwin
                ? [Table("sales", "orders", "amount"), Table("hr", "orders", "salary"),
                   Table("ops", "orders", "runbook", policyActions: "read[ops.read]")]
                : [Table("sales", "orders", "amount"), Table("hr", "orders", "salary")],
            Metadata = new Dictionary<string, object?>(),
        };

        private static IReadOnlyList<VisibleTable> AnonymousVisible(IDbModel model) =>
            SchemaReadVisibility.Project(model, new Dictionary<string, object?>());

        [Theory]
        [InlineData("sales.orders", "sales", "amount", "salary")]
        [InlineData("hr.orders", "hr", "salary", "amount")]
        [InlineData("HR.ORDERS", "hr", "salary", "amount")]
        public async Task DescribeTable_ResolvesASchemaQualifiedNameToThatSchemasTable(
            string requested, string schema, string ownColumn, string twinColumn)
        {
            await WithClientAsync(Model(), async client =>
            {
                var result = await client.CallToolAsync("bifrost_describe_table",
                    new Dictionary<string, object?> { ["table"] = requested });

                result.IsError.Should().NotBe(true, "a qualified name names exactly one table");
                var payload = result.StructuredContent!.Value;
                payload.GetProperty("schema").GetString().Should().Be(schema);
                var columns = payload.GetProperty("columns").EnumerateArray()
                    .Select(c => c.GetProperty("name").GetString()).ToList();
                columns.Should().Contain(ownColumn);
                columns.Should().NotContain(twinColumn, "the other schema's table must not answer");
            });
        }

        [Fact]
        public async Task DescribeTable_AnAmbiguousBareName_AnswersExactlyLikeAnUnknownOrDeniedTable()
        {
            var model = Model();
            var visible = AnonymousVisible(model);
            await WithClientAsync(model, async client =>
            {
                var ambiguous = await ErrorTextAsync(client, "orders");
                var denied = await ErrorTextAsync(client, "ops.orders");

                ambiguous.Should().Be(SchemaDescriber.UnknownTableMessage(visible, "orders"),
                    "an ambiguous bare name is a miss, answered by the one unknown-table writer");
                denied.Should().Be(SchemaDescriber.UnknownTableMessage(visible, "ops.orders"));
            });

            // The denied answer must not depend on whether the denied table exists: the same
            // request against a model WITHOUT ops.orders gets a byte-equal body.
            var withoutTwin = Model(withDeniedTwin: false);
            string deniedBody = null!, absentBody = null!;
            await WithClientAsync(model, async c => deniedBody = await ErrorTextAsync(c, "ops.orders"));
            await WithClientAsync(withoutTwin, async c => absentBody = await ErrorTextAsync(c, "ops.orders"));
            deniedBody.Should().Be(absentBody);

            string ambiguousBody = null!, ambiguousWithoutTwinBody = null!;
            await WithClientAsync(model, async c => ambiguousBody = await ErrorTextAsync(c, "orders"));
            await WithClientAsync(withoutTwin, async c => ambiguousWithoutTwinBody = await ErrorTextAsync(c, "orders"));
            ambiguousBody.Should().Be(ambiguousWithoutTwinBody,
                "the hidden same-named table must not change the answer to the bare name");
        }

        [Fact]
        public async Task DescribeTable_TheUnknownTableListNamesEachSharedTableQualified()
        {
            await WithClientAsync(Model(), async client =>
            {
                var text = await ErrorTextAsync(client, "orders");
                text.Should().Contain("Available tables: hr.orders, sales.orders.",
                    "every listed name must resolve when sent back");
                text.Should().NotContain("ops.orders", "a denied table is never listed");
            });
        }

        [Fact]
        public async Task Resources_ListOneResolvableUriPerSharedName_AndEachReadsItsOwnTable()
        {
            await WithClientAsync(Model(), async client =>
            {
                var uris = (await client.ListResourcesAsync())
                    .Select(r => r.Uri)
                    .Where(u => u != BifrostMcpServerFactory.OverviewResourceUri)
                    .ToList();

                uris.Should().BeEquivalentTo(new[] { "bifrost://schema/hr.orders", "bifrost://schema/sales.orders" });

                foreach (var (uri, schema) in new[] { ("bifrost://schema/sales.orders", "sales"), ("bifrost://schema/hr.orders", "hr") })
                {
                    var read = await client.ReadResourceAsync(uri);
                    var json = JsonDocument.Parse(read.Contents.OfType<TextResourceContents>().Single().Text).RootElement;
                    json.GetProperty("schema").GetString().Should().Be(schema);
                }
            });
        }

        [Fact]
        public async Task Resources_AnAmbiguousBareNameIsTheSameNotFoundAsADeniedTable()
        {
            var visible = AnonymousVisible(Model());
            await WithClientAsync(Model(), async client =>
            {
                var ambiguous = (await client.Invoking(c => c.ReadResourceAsync("bifrost://schema/orders").AsTask())
                    .Should().ThrowAsync<McpException>()).Which.Message;
                ambiguous.Should().Contain(SchemaDescriber.UnknownTableMessage(visible, "orders"));
            });
        }

        private static async Task<string> ErrorTextAsync(McpClient client, string table)
        {
            var result = await client.CallToolAsync("bifrost_describe_table",
                new Dictionary<string, object?> { ["table"] = table });
            result.IsError.Should().BeTrue($"'{table}' must not resolve");
            return result.Content.OfType<TextContentBlock>().Single().Text;
        }

        private sealed class ModelOnlyExecutor(IDbModel model) : IQueryIntentExecutor
        {
            public Task<IDbModel> GetModelAsync(string? endpoint = null) => Task.FromResult(model);

            public Task<QueryIntentResult> ExecuteAsync(QueryIntent intent, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("schema tools never execute a data read");
        }

        private static async Task WithClientAsync(IDbModel model, Func<McpClient, Task> body)
        {
            var options = BifrostMcpServerFactory.CreateServerOptions(new ModelOnlyExecutor(model));
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-identity-test");
            await using var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
            using var stop = new CancellationTokenSource();
            var run = server.RunAsync(stop.Token);
            var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));
            try { await body(client); }
            finally
            {
                await client.DisposeAsync();
                await stop.CancelAsync();
                try { await run; } catch (OperationCanceledException) { }
            }
        }
    }
}
