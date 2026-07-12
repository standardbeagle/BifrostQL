using System.Net;
using BifrostQL.Core.Modules;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test
{
    /// <summary>
    /// Security-conformance facts for the chat endpoints, equivalent to
    /// <c>ProtocolAdapterConformanceTests</c>.
    ///
    /// <para><b>Why this is not a derivation of the kit</b>: the kit's contract is an
    /// <c>IProtocolAdapter</c> that can execute an arbitrary
    /// <c>ConformanceReadRequest</c>/<c>ConformanceMutationRequest</c> against any
    /// fixture table (orders, documents) through its own wire. The chat endpoints
    /// are fixed-shape HTTP middleware over <c>ChatConversationStore</c> — they
    /// cannot read arbitrary tables/columns or run caller-supplied filters, so the
    /// kit's request shapes are untranslatable. The kit's security claims are proven
    /// directly on the chat wire instead: transformer injection (tenant WHERE in the
    /// generated SQL) and parameterization here; fail-closed identity (401 before any
    /// call), missing-tenant denial (403), and cross-tenant/nonexistent
    /// indistinguishability (404 both directions) in <see cref="ChatEndpointTests"/>,
    /// which shares this exact host shape.</para>
    /// </summary>
    public sealed class ChatEndpointConformanceTests : IAsyncLifetime
    {
        private readonly ChatEndpointHost _h = new();

        public Task InitializeAsync() => _h.InitializeAsync();

        public async Task DisposeAsync() => await _h.DisposeAsync();

        [Fact]
        public async Task ChatSql_CarriesTheTenantPredicate_AndBindsCallerValuesAsParameters()
        {
            const string content = "the-secret-caller-content";
            var capture = new SqlCaptureObserver();
            var client = await _h.StartAsync(observers: new IQueryObserver[] { capture });
            var conversationId = await _h.CreateConversationAsync(client, "tenant-a");

            using var response = await _h.PostMessageAsync(client, conversationId, content);
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            // (a) security transformers apply: every chat read the endpoint issued
            // carries the tenant predicate injected by the transformer pipeline —
            // the endpoint itself never authored a WHERE clause with tenant_id.
            var conversationSql = capture.SqlFor("conversations");
            var messagesSql = capture.SqlFor("messages");
            conversationSql.Should().NotBeEmpty();
            messagesSql.Should().NotBeEmpty();
            string.Join("\n", conversationSql).Should().MatchRegex(@"WHERE[\s\S]*tenant_id");
            string.Join("\n", messagesSql).Should().MatchRegex(@"WHERE[\s\S]*tenant_id");

            // (b) SQL is parameterized: neither the caller's message content nor the
            // tenant value appears in SQL text — both bind as parameters.
            var allSql = string.Join("\n", conversationSql.Concat(messagesSql));
            allSql.Should().NotContain(content, "caller content must bind as a parameter, never concatenate");
            allSql.Should().NotContain("tenant-a", "tenant values must bind as parameters, never concatenate");
            allSql.Should().Contain("@", "predicates must reference bound parameters");
        }

        /// <summary>Captures generated SQL per table at the AfterExecute phase.</summary>
        private sealed class SqlCaptureObserver : IQueryObserver
        {
            private readonly object _gate = new();
            private readonly List<(string Table, string Sql)> _captured = new();

            public QueryPhase[] Phases { get; } = { QueryPhase.AfterExecute };

            public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
            {
                lock (_gate)
                    _captured.Add((context.Table.DbName, context.Sql ?? string.Empty));
                return ValueTask.CompletedTask;
            }

            public IReadOnlyList<string> SqlFor(string table)
            {
                lock (_gate)
                    return _captured
                        .Where(c => string.Equals(c.Table, table, StringComparison.OrdinalIgnoreCase))
                        .Select(c => c.Sql)
                        .ToArray();
            }
        }
    }
}
