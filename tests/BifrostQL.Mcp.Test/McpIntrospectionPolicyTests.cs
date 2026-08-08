using System.IO.Pipelines;
using System.Security.Claims;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// protocol-adapter-security invariant 4 for the MCP introspection surface: the
    /// schema tools (<c>bifrost_schema_overview</c>, <c>bifrost_describe_table</c>) and
    /// the <c>bifrost://schema/…</c> resources must be filtered by the SAME evaluator the
    /// data path calls, fail-closed.
    ///
    /// <para>The pre-existing schema fixtures were vacuous for this: every table in them
    /// carried no policy metadata, so a completely unfiltered surface satisfied them, and
    /// none exercised an unauthenticated caller against restricted data. The fixture here
    /// is built so the bug can manifest — an ANONYMOUS caller (the default FailClosed
    /// posture: no bearer, empty user context) against BOTH a table-level read denial and
    /// a column-level read denial, plus an admin caller to prove the filter is a real
    /// policy check rather than a blanket hide.</para>
    /// </summary>
    public sealed class McpIntrospectionPolicyTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"mcppolicy_{Guid.NewGuid():N}";

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;
        private IQueryIntentExecutor _executor = null!;
        private IBifrostAuthContextFactory _factory = null!;

        public async Task InitializeAsync()
        {
            var connectionString = $"Data Source={_connString};Mode=Memory;Cache=Shared";
            _keepAlive = new SqliteConnection(connectionString);
            await _keepAlive.OpenAsync();
            foreach (var sql in new[]
            {
                """
                CREATE TABLE customers (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL
                )
                """,
                // Table-level read denial: policy-actions names only 'create', so Read is
                // NOT in the allow-list and every non-admin caller is denied.
                """
                CREATE TABLE ledger_entries (
                    id INTEGER PRIMARY KEY,
                    customer_id INTEGER NOT NULL REFERENCES customers(id),
                    amount TEXT NOT NULL
                )
                """,
                // Column-level read denial on an otherwise READABLE table: the evaluator
                // treats any policy metadata as opt-in lockdown, so 'policy-actions: read'
                // is required for the table itself to stay visible — otherwise this fixture
                // would prove table-level denial twice and never exercise the column rule.
                """
                CREATE TABLE staff (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    ssn TEXT NOT NULL
                )
                """,
            })
            {
                await using var cmd = new SqliteCommand(sql, _keepAlive);
                await cmd.ExecuteNonQueryAsync();
            }

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = connectionString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = new[]
                            {
                                "*.ledger_entries { policy-actions: create }",
                                "*.staff { policy-actions: read; policy-read-deny: ssn }",
                            };
                            e.DisableAuth = true;
                        });
                    });
                });
                web.Configure(_ => { });
            });
            _host = await builder.StartAsync();
            _executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            _factory = _host.Services.GetRequiredService<IBifrostAuthContextFactory>();
        }

        public async Task DisposeAsync()
        {
            await _host.StopAsync();
            _host.Dispose();
            await _keepAlive.DisposeAsync();
        }

        // ---- anonymous caller (default FailClosed posture) --------------------

        [Fact]
        public async Task Anonymous_SchemaOverview_OmitsReadDeniedTableAndReadDeniedColumn()
        {
            await WithClientAsync(AnonymousProvider(), async client =>
            {
                var payload = (await CallAsync(client, "bifrost_schema_overview",
                    new Dictionary<string, object?> { ["detail"] = "full" })).StructuredContent!.Value;

                var tables = payload.GetProperty("tables").EnumerateArray()
                    .ToDictionary(t => t.GetProperty("name").GetString()!);

                tables.Keys.Should().BeEquivalentTo(new[] { "customers", "staff" },
                    "a table the caller cannot SELECT must not appear in introspection at all");
                payload.GetProperty("tableCount").GetInt32().Should().Be(2,
                    "the count must describe the caller's visible schema, not the model");

                tables["staff"].GetProperty("columns").EnumerateArray()
                    .Select(c => c.GetString()!)
                    .Should().NotContain(c => c.StartsWith("ssn:"),
                        "a read-denied column must not be named in introspection");
                tables["staff"].GetProperty("columnCount").GetInt32().Should().Be(2);
            });
        }

        [Fact]
        public async Task Anonymous_SchemaOverview_DropsRelationshipEdgesPointingAtAHiddenTable()
        {
            await WithClientAsync(AnonymousProvider(), async client =>
            {
                var payload = (await CallAsync(client, "bifrost_schema_overview",
                    new Dictionary<string, object?> { ["detail"] = "summary" })).StructuredContent!.Value;

                var customers = payload.GetProperty("tables").EnumerateArray()
                    .Single(t => t.GetProperty("name").GetString() == "customers");

                customers.GetProperty("referencedBy").EnumerateArray().Select(e => e.GetString()!)
                    .Should().NotContain(e => e.Contains("ledger_entries"),
                        "an edge is a two-ended fact: publishing it would disclose the hidden table's " +
                        "name and key columns through the visible table's metadata");
            });
        }

        [Fact]
        public async Task Anonymous_DescribeTable_ReadDeniedTable_IsIndistinguishableFromNonExistent()
        {
            await WithClientAsync(AnonymousProvider(), async client =>
            {
                var denied = await CallAsync(client, "bifrost_describe_table",
                    new Dictionary<string, object?> { ["table"] = "ledger_entries" });
                var missing = await CallAsync(client, "bifrost_describe_table",
                    new Dictionary<string, object?> { ["table"] = "no_such_table" });

                denied.IsError.Should().BeTrue();
                var deniedText = denied.Content.OfType<TextContentBlock>().Single().Text;
                var missingText = missing.Content.OfType<TextContentBlock>().Single().Text;

                deniedText.Should().Contain("Unknown table 'ledger_entries'");
                deniedText.Should().NotContain("ledger_entries.", "no column or key detail may leak");
                // Both answers list the SAME visible tables, so the response shape cannot
                // distinguish "denied" from "does not exist".
                deniedText.Should().Contain("Available tables: customers, staff.");
                missingText.Should().Contain("Available tables: customers, staff.");
            });
        }

        [Fact]
        public async Task Anonymous_DescribeTable_OmitsReadDeniedColumn()
        {
            await WithClientAsync(AnonymousProvider(), async client =>
            {
                var payload = (await CallAsync(client, "bifrost_describe_table",
                    new Dictionary<string, object?> { ["table"] = "staff" })).StructuredContent!.Value;

                payload.GetProperty("columns").EnumerateArray()
                    .Select(c => c.GetProperty("name").GetString()!)
                    .Should().BeEquivalentTo("id", "name");
            });
        }

        [Fact]
        public async Task Anonymous_Resources_HideTheReadDeniedTable_AndRefuseItsUri()
        {
            await WithClientAsync(AnonymousProvider(), async client =>
            {
                var resources = await client.ListResourcesAsync();
                resources.Select(r => r.Uri).Should().BeEquivalentTo(
                    "bifrost://schema/overview",
                    "bifrost://schema/customers",
                    "bifrost://schema/staff");

                var read = () => client.ReadResourceAsync("bifrost://schema/ledger_entries").AsTask();
                (await read.Should().ThrowAsync<McpException>())
                    .Which.Message.Should().Contain("Unknown table 'ledger_entries'");
            });
        }

        // ---- admin caller: the filter is a policy check, not a blanket hide ----

        [Fact]
        public async Task Admin_SeesEveryTableAndColumn()
        {
            await WithClientAsync(RoleProvider("admin"), async client =>
            {
                var payload = (await CallAsync(client, "bifrost_schema_overview",
                    new Dictionary<string, object?> { ["detail"] = "full" })).StructuredContent!.Value;

                var tables = payload.GetProperty("tables").EnumerateArray()
                    .ToDictionary(t => t.GetProperty("name").GetString()!);
                tables.Keys.Should().BeEquivalentTo("customers", "ledger_entries", "staff");
                tables["staff"].GetProperty("columns").EnumerateArray().Select(c => c.GetString()!)
                    .Should().Contain(c => c.StartsWith("ssn:"));
                tables["customers"].GetProperty("referencedBy").EnumerateArray().Select(e => e.GetString()!)
                    .Should().Contain(e => e.Contains("ledger_entries"));
            });
        }

        // ---- helpers ---------------------------------------------------------

        private static async Task<CallToolResult> CallAsync(
            McpClient client, string tool, Dictionary<string, object?> args)
            => await client.CallToolAsync(tool, args);

        /// <summary>The default fail-closed posture: no principal, so an empty user context.</summary>
        private Func<IDictionary<string, object?>> AnonymousProvider()
            => BifrostMcpAdapter.CreateProjectionProvider(_factory, _host.Services, principal: null);

        private Func<IDictionary<string, object?>> RoleProvider(params string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-" + string.Join("-", roles)) };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
            return BifrostMcpAdapter.CreateProjectionProvider(_factory, _host.Services, principal);
        }

        private async Task WithClientAsync(
            Func<IDictionary<string, object?>> provider, Func<McpClient, Task> body)
        {
            var options = BifrostMcpServerFactory.CreateServerOptions(_executor, EndpointPath, provider);
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var transport = new StreamServerTransport(
                clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-introspection");
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
