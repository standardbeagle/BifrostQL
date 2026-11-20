using System.Collections.Concurrent;
using System.Globalization;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A filter transformer sees the node's own request filter (<c>QueryTransformContext.Filter</c>),
/// so it can refuse a read that pins one record outside the caller's scope instead of only
/// narrowing it to nothing. The filter is the client's, before any transformer's addition, on
/// top-level and nested nodes alike.
/// </summary>
public sealed class FilterTransformerRequestFilterTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE people (id INT PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE docs (id SERIAL PRIMARY KEY, owner_id INT NOT NULL REFERENCES people(id), title TEXT NOT NULL);
        INSERT INTO people (id, name) VALUES (1, 'ann'), (2, 'bob');
        INSERT INTO docs (owner_id, title) VALUES (1, 'ann-doc'), (2, 'bob-doc');
        CREATE TABLE __history (
            id BIGSERIAL PRIMARY KEY, entity TEXT NOT NULL, entity_id JSONB NOT NULL, op TEXT NOT NULL,
            actor TEXT NULL, changed_at TIMESTAMPTZ NOT NULL DEFAULT now(),
            before JSONB NULL, after JSONB NULL, changed_columns JSONB NULL);
        """;

    private readonly ConcurrentQueue<string> _seen = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_request_filter", Ddl,
            new[] { "public.docs { history: enabled }", ":root { history-table: public.__history }" },
            bifrost: options => options.AddFilterTransformers(
                new IFilterTransformer[] { new OwnerScope(_seen), new HistoryFilterRecorder(_seen) }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task FilterTransformer_SeesTheRequestsFilter_AndCanRefuseOneRecord()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        // Pinned to another caller: refused with the transformer's own code.
        var refused = await _host!.PostAsync("""{ docs(filter: { owner_id: { _eq: 2 } }) { data { id } } }""", "1");
        var error = PostgresGraphQlHost.FirstError(refused);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(OwnerScope.Code);

        // The same read without the pin is narrowed to the caller's rows.
        var narrowed = await _host.PostAsync("""{ docs { data { title } } }""", "1");
        PostgresGraphQlHost.AssertNoErrors(narrowed);
        narrowed.RootElement.GetProperty("data").GetProperty("docs").GetProperty("data")
            .EnumerateArray().Select(row => row.GetProperty("title").GetString())
            .Should().Equal("ann-doc");

        // A nested node's context carries that node's own filter, not the parent's.
        _seen.Clear();
        var nested = await _host.PostAsync(
            """{ people(filter: { id: { _eq: 1 } }) { data { id docs(filter: { title: { _eq: "ann-doc" } }) { data { id } } } } }""",
            "1");
        PostgresGraphQlHost.AssertNoErrors(nested);
        _seen.Should().Contain("nested:title");
    }

    [SkippableFact]
    public async Task HistoryTrailRead_RequestFilter_LeavesOutTheServersEntityPin()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""{ docsHistory { data { id } } }""", "1");

        PostgresGraphQlHost.AssertNoErrors(reply);
        _seen.Should().Contain("history:none");
    }

    /// <summary>Records the request filter a trail read of the history table hands a transformer.</summary>
    private sealed class HistoryFilterRecorder(ConcurrentQueue<string> seen) : IFilterTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, QueryTransformContext context) => table.DbName == "__history";

        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
        {
            seen.Enqueue($"history:{context.Filter?.ColumnName ?? (context.Filter is null ? "none" : "and")}");
            return null;
        }
    }

    /// <summary>
    /// Scopes <c>docs</c> to the caller, and refuses outright a read whose own filter pins
    /// <c>owner_id</c> by <c>_eq</c> to someone else.
    /// </summary>
    private sealed class OwnerScope(ConcurrentQueue<string> seen) : IFilterTransformer
    {
        public const string Code = "OWNER_FORBIDDEN";

        public int Priority => 200;

        public bool AppliesTo(IDbTable table, QueryTransformContext context) => table.DbName == "docs";

        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
        {
            var caller = PolicyIdentity.FromUserContext(context.UserContext).Id;
            if (context.IsNestedQuery && context.Filter is { } nestedFilter)
                seen.Enqueue($"nested:{nestedFilter.ColumnName}");
            if (context.Filter is { ColumnName: "owner_id", Next: { RelationName: FilterOperators.Eq } pin }
                && Convert.ToString(pin.Value, CultureInfo.InvariantCulture) != caller)
                throw new BifrostExecutionError("That record is not yours.") { ErrorCode = Code };
            return TableFilterFactory.Equals(table, "owner_id", int.Parse(caller, CultureInfo.InvariantCulture));
        }
    }
}
