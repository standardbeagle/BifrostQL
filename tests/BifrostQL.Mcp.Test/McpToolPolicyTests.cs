using System.IO.Pipelines;
using System.Security.Claims;
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
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// Tests slice 6 of the declarative MCP tool epic: the tool-budget guardrail (warn past a
    /// configurable declared-tool count, fail load past a configurable hard cap) and the optional
    /// per-identity role→tool allow-list. The budget tests drive <see cref="BifrostMcpServerFactory"/>
    /// directly (load-time behavior). The filtering tests project a real <see cref="ClaimsPrincipal"/>
    /// through the shared <see cref="IBifrostAuthContextFactory"/> — proving roles are derived ONLY from
    /// the factory-produced user context, with no bespoke claim reading — and exercise the gate over the
    /// real SDK client/server pair so tools/list and tools/call agree.
    /// </summary>
    public sealed class McpToolPolicyTests : IAsyncLifetime
    {
        private const string EndpointPath = "/graphql";

        private readonly string _connString =
            $"Data Source=mcptoolpolicy_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";

        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;
        private IQueryIntentExecutor _executor = null!;
        private IBifrostAuthContextFactory _factory = null!;

        public async Task InitializeAsync()
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            await using (var cmd = new SqliteCommand(
                "CREATE TABLE widgets (id INTEGER PRIMARY KEY, name TEXT NOT NULL, qty INTEGER NOT NULL)", _keepAlive))
                await cmd.ExecuteNonQueryAsync();

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
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
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

        // ---- Tool-budget guardrail -------------------------------------------------------------

        [Fact]
        public void Budget_LogsWarning_NamingCountAndBudget_WhenDeclaredCountExceedsWarnThreshold()
        {
            // Criterion 1: warn past the configured threshold, naming the declared-tool count and the
            // configured budget. Thresholds are options, not baked-in constants: a low WarnThreshold
            // trips the warning against the real declared surface.
            var logger = new ListLogger();
            var policy = new McpToolPolicyOptions { Budget = new McpToolBudgetOptions { WarnThreshold = 5, HardCap = 100 } };

            _ = BifrostMcpServerFactory.CreateServerOptions(_executor, EndpointPath, toolPolicy: policy, logger: logger);

            var warning = logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning).Subject;
            warning.Message.Should().MatchRegex(@"declares \d+ tools")   // names the declared-tool count
                .And.Contain("warn threshold of 5")                       // names the configured budget
                .And.Contain("hard cap 100");
        }

        [Fact]
        public void Budget_FailsLoad_WithPreciseError_PastHardCap()
        {
            // Criterion 1: past the configurable hard cap the loader FAILS (throws), with an error naming
            // the count, cap, and warn threshold.
            var policy = new McpToolPolicyOptions { Budget = new McpToolBudgetOptions { WarnThreshold = 1, HardCap = 5 } };

            var act = () => BifrostMcpServerFactory.CreateServerOptions(_executor, EndpointPath, toolPolicy: policy);

            act.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().MatchRegex(@"declares \d+ tools")
                .And.Contain("hard cap of 5")
                .And.Contain("warn threshold 1");
        }

        [Fact]
        public void Budget_Silent_UnderDefaultThreshold()
        {
            // The default warn threshold (12) is well above the shipped read surface, so a default budget
            // logs nothing — proves the threshold is a real option, not a constant that always fires.
            var logger = new ListLogger();

            _ = BifrostMcpServerFactory.CreateServerOptions(_executor, EndpointPath, logger: logger);

            logger.Entries.Should().NotContain(e => e.Level == LogLevel.Warning);
        }

        // ---- Per-identity tool filtering -------------------------------------------------------

        [Fact]
        public async Task RoleFilter_HidesGatedToolAndRefusesDirectCall_ForIdentityLackingRole()
        {
            // Criteria 2 & 3: bifrost_aggregate is gated to role "analyst". A "reader" identity — whose
            // roles come ONLY from the factory-projected user context — must not see it in the list, and a
            // direct call by name must be refused (fail-closed).
            var provider = ProviderForRoles("reader");
            provider()["roles"].Should().BeEquivalentTo(new[] { "reader" },
                "roles are derived from the shared factory's user-context projection, not bespoke claim reading");

            var policy = GateAggregateToAnalyst();
            await using var session = await StartSessionAsync(provider, policy);

            var listed = await session.Client.ListToolsAsync();
            listed.Select(t => t.Name).Should()
                .NotContain(AggregateTools.ToolName, "a role-gated tool is hidden from an identity lacking the role")
                .And.Contain(DataTools.QueryToolName);

            var result = await session.Client.CallToolAsync(
                AggregateTools.ToolName, new Dictionary<string, object?> { ["table"] = "widgets" });
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("not available for the current identity");
        }

        [Fact]
        public async Task RoleFilter_LeavesUngatedToolVisibleAndCallable_ForEveryIdentity()
        {
            // Criterion 4: a tool named by NO allow-list stays visible to all authenticated identities —
            // gating one tool must not silently hide the default surface.
            var provider = ProviderForRoles("reader");
            var policy = GateAggregateToAnalyst();
            await using var session = await StartSessionAsync(provider, policy);

            var listed = await session.Client.ListToolsAsync();
            listed.Select(t => t.Name).Should().Contain(DataTools.QueryToolName);

            var result = await session.Client.CallToolAsync(
                DataTools.QueryToolName, new Dictionary<string, object?> { ["table"] = "widgets" });
            result.IsError.Should().NotBe(true,
                "an ungated tool is never refused by the gate: " +
                result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
        }

        [Fact]
        public async Task RoleFilter_ShowsGatedToolAndPassesCallThrough_ForMatchingRole()
        {
            // Criteria 2 & 3 (allowed path): the "analyst" identity sees the gated tool and its call is NOT
            // refused by the gate (it reaches the real handler).
            var provider = ProviderForRoles("analyst");
            var policy = GateAggregateToAnalyst();
            await using var session = await StartSessionAsync(provider, policy);

            var listed = await session.Client.ListToolsAsync();
            listed.Select(t => t.Name).Should().Contain(AggregateTools.ToolName);

            var result = await session.Client.CallToolAsync(
                AggregateTools.ToolName, new Dictionary<string, object?>
                {
                    ["table"] = "widgets",
                    ["aggregates"] = new[] { new Dictionary<string, object?> { ["op"] = "count", ["as"] = "n" } },
                });
            (result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty)
                .Should().NotContain("not available for the current identity",
                    "the gate lets a permitted role through to the handler");
        }

        // ---- Declarative tools compose with the budget and the gate ----------------------------

        [Fact]
        public void DeclarativeTools_CountAgainstTheSameToolBudget_AndTipLoadPastHardCap()
        {
            // Criterion 3 (compose with slice-6 budget): three declarative tools plus the six built-in
            // read tools exceed a hard cap of 8, so load fails — proving declarative tools count against
            // the SAME budget. The same cap without the declarative tools does not fail (they are what
            // tipped it), so the guardrail is not a constant that always fires.
            var declarative = DeclarativeToolCount(3);
            var budget = new McpToolBudgetOptions { WarnThreshold = 1, HardCap = 8 };

            var overCap = () => BifrostMcpServerFactory.CreateServerOptions(
                _executor, EndpointPath,
                toolPolicy: new McpToolPolicyOptions { Budget = budget }, declarativeTools: declarative);
            overCap.Should().Throw<InvalidOperationException>()
                .Which.Message.Should().Contain("hard cap of 8");

            var builtInsOnly = () => BifrostMcpServerFactory.CreateServerOptions(
                _executor, EndpointPath, toolPolicy: new McpToolPolicyOptions { Budget = budget });
            builtInsOnly.Should().NotThrow();
        }

        [Fact]
        public async Task DeclarativeTool_HonorsTheGate_HiddenAndRefused_ForIdentityLackingRole()
        {
            // Criterion 3 (compose with slice-6 gate): a declarative tool gated to "analyst" is hidden
            // from a "reader" in tools/list and a direct call by name is refused fail-closed — the same
            // gate the built-in tools honor, agreeing across list and call.
            var declarative = await DeclarativeWidgetLookupDocAsync("widget_lookup");
            var policy = GateToolToRole("widget_lookup", "analyst");
            await using var session = await StartSessionAsync(ProviderForRoles("reader"), policy, declarative);

            var listed = await session.Client.ListToolsAsync();
            listed.Select(t => t.Name).Should()
                .NotContain("widget_lookup", "a role-gated declarative tool is hidden from an identity lacking the role")
                .And.Contain(DataTools.QueryToolName);

            var result = await session.Client.CallToolAsync(
                "widget_lookup", new Dictionary<string, object?> { ["widgetId"] = "1" });
            result.IsError.Should().BeTrue();
            result.Content.OfType<TextContentBlock>().Single().Text
                .Should().Contain("not available for the current identity");
        }

        [Fact]
        public async Task DeclarativeTool_VisibleAndReachesHandler_ForMatchingRole()
        {
            // Criterion 3 (allowed path): the "analyst" identity sees the gated declarative tool and its
            // call is NOT refused by the gate — it reaches the handler and executes through the intent
            // path, returning the stable found/data envelope.
            var declarative = await DeclarativeWidgetLookupDocAsync("widget_lookup");
            var policy = GateToolToRole("widget_lookup", "analyst");
            await using var session = await StartSessionAsync(ProviderForRoles("analyst"), policy, declarative);

            var listed = await session.Client.ListToolsAsync();
            listed.Select(t => t.Name).Should().Contain("widget_lookup");

            var result = await session.Client.CallToolAsync(
                "widget_lookup", new Dictionary<string, object?> { ["widgetId"] = "1" });
            result.IsError.Should().NotBe(true,
                "a permitted role reaches the declarative handler: " +
                result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            result.StructuredContent!.Value.GetProperty("found").GetBoolean().Should().BeFalse(
                "no widget row exists, but the tool executed and returned the stable envelope");
        }

        private static McpToolPolicyOptions GateToolToRole(string toolName, string role) => new()
        {
            RoleToolAllowList = new Dictionary<string, IReadOnlyList<string>>
            {
                [role] = new[] { toolName },
            },
        };

        private static DeclarativeToolDocument DeclarativeToolCount(int count) => new()
        {
            Version = 1,
            Tools = Enumerable.Range(0, count).Select(i => new DeclarativeToolDefinition
            {
                Name = $"decl_tool_{i}",
                Description = "Budget-composition probe tool.",
                Params = new Dictionary<string, DeclarativeToolParameter> { ["p"] = new() { Type = "id" } },
                Root = new DeclarativeToolRoot { Table = "dbo.unused", ById = "p", Fields = [] },
            }).ToList(),
        };

        private async Task<DeclarativeToolDocument> DeclarativeWidgetLookupDocAsync(string toolName)
        {
            var model = await _executor.GetModelAsync(EndpointPath);
            var widgets = model.Tables.Single(t => string.Equals(t.DbName, "widgets", StringComparison.OrdinalIgnoreCase));
            var qualified = $"{widgets.TableSchema}.{widgets.DbName}";
            var json = $$"""
                {
                  "version": 1,
                  "tools": [{
                    "name": "{{toolName}}",
                    "description": "Look up one widget by its primary key.",
                    "params": { "widgetId": { "type": "id", "table": "{{qualified}}", "description": "Widget id." } },
                    "root": { "table": "{{qualified}}", "byId": "widgetId", "fields": ["id", "name", "qty"] }
                  }]
                }
                """;
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            return new DeclarativeToolDocumentLoader(
                new StreamDeclarativeToolDocumentSource(stream, "widget-lookup test")).Load();
        }

        private static McpToolPolicyOptions GateAggregateToAnalyst() => new()
        {
            RoleToolAllowList = new Dictionary<string, IReadOnlyList<string>>
            {
                ["analyst"] = new[] { AggregateTools.ToolName },
            },
        };

        private Func<IDictionary<string, object?>> ProviderForRoles(params string[] roles)
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-" + string.Join("-", roles)) };
            claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Test"));
            return BifrostMcpAdapter.CreateProjectionProvider(_factory, _host.Services, principal);
        }

        private async Task<McpSession> StartSessionAsync(
            Func<IDictionary<string, object?>> provider, McpToolPolicyOptions policy,
            DeclarativeToolDocument? declarativeTools = null)
        {
            var options = BifrostMcpServerFactory.CreateServerOptions(
                _executor, EndpointPath, userContextProvider: provider, toolPolicy: policy,
                declarativeTools: declarativeTools);
            return await McpSession.StartAsync(options);
        }

        /// <summary>An in-memory MCP client/server pair over a paired stream transport, torn down on dispose.</summary>
        private sealed class McpSession : IAsyncDisposable
        {
            private readonly McpServer _server;
            private readonly Task _run;
            private readonly CancellationTokenSource _stop;
            public McpClient Client { get; }

            private McpSession(McpServer server, Task run, CancellationTokenSource stop, McpClient client)
            {
                _server = server;
                _run = run;
                _stop = stop;
                Client = client;
            }

            public static async Task<McpSession> StartAsync(McpServerOptions options)
            {
                var clientToServer = new Pipe();
                var serverToClient = new Pipe();
                var transport = new StreamServerTransport(
                    clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream(), serverName: "BifrostQL-toolpolicy");
                var server = McpServer.Create(transport, options, loggerFactory: null, serviceProvider: null);
                var stop = new CancellationTokenSource();
                var run = server.RunAsync(stop.Token);
                var client = await McpClient.CreateAsync(new StreamClientTransport(
                    serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));
                return new McpSession(server, run, stop, client);
            }

            public async ValueTask DisposeAsync()
            {
                await Client.DisposeAsync();
                await _stop.CancelAsync();
                try { await _run; } catch (OperationCanceledException) { }
                await _server.DisposeAsync();
                _stop.Dispose();
            }
        }

        private sealed record LogEntry(LogLevel Level, string Message);

        /// <summary>Minimal <see cref="ILogger"/> that records entries so a test can assert the budget warning.</summary>
        private sealed class ListLogger : ILogger
        {
            public List<LogEntry> Entries { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
                => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }
    }
}
