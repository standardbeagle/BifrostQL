using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Operations;
using BifrostQL.Core.QueryModel;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <c>QueryAllAsync</c> reads a whole set past the row ceiling, as one statement (one
/// snapshot), as the caller, inside the operation's transaction. <c>QueryAsync</c> on the same
/// rows still stops at the ceiling. The caller owns 11 stored rows plus the one the operation
/// inserts before it reads; 3 rows belong to someone else and must not appear.
/// </summary>
public sealed class OperationQueryAllTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE items (id SERIAL PRIMARY KEY, owner_id INT NOT NULL, n INT NOT NULL);
        INSERT INTO items (owner_id, n) SELECT 1, g FROM generate_series(1, 11) AS g;
        INSERT INTO items (owner_id, n) SELECT 2, 100 + g FROM generate_series(1, 3) AS g;
        """;

    private readonly ConcurrentQueue<string> _sql = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_query_all", Ddl,
            new[] { ":root { max-query-rows: 5; operations: readAll[reader], readWindow[reader] }" },
            bifrost: options => options
                .AddOperation<ReadAll>()
                .AddOperation<ReadWindow>()
                .AddFilterTransformers(new IFilterTransformer[] { new OwnerScope() })
                .AddQueryObservers(new IQueryObserver[] { new SqlRecorder(_sql) }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task QueryAll_ReadsEveryRow_PastTheRowCeiling_InOneStatement()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { readAll }""", "1", "reader");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var values = reply.RootElement.GetProperty("data").GetProperty("readAll").GetString()!
            .Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)).ToList();
        values.Should().Equal(new[] { 500 }.Concat(Enumerable.Range(1, 11).Reverse()),
            "every caller row, the operation's own insert among them, in sort order, none of owner 2's");
        var read = _sql.Should().ContainSingle(sql => sql.Contains("\"items\"")).Subject;
        Regex.Matches(read, @"\bSELECT\b", RegexOptions.IgnoreCase).Should().HaveCount(1, read);
        read.Should().NotContainEquivalentOf("LIMIT");
    }

    [SkippableFact]
    public async Task QueryAsync_StillStopsAtTheCeiling()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { readWindow }""", "1", "reader");

        PostgresGraphQlHost.AssertNoErrors(reply);
        reply.RootElement.GetProperty("data").GetProperty("readWindow").GetInt32().Should().Be(5);
    }

    [Fact]
    public void ReadsWholeSet_IsNotOnThePublicSurface()
    {
        var property = typeof(GqlObjectQuery).GetProperty(
            "ReadsWholeSet", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)!;

        property.Should().NotBeNull();
        property.GetMethod!.IsPublic.Should().BeFalse();
        property.SetMethod!.IsPublic.Should().BeFalse("only the engine may lift the row window");
    }

    private sealed class ReadAll : IBifrostOperation
    {
        public string Name => "readAll";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": String";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            await context.InsertAsync("items", new Dictionary<string, object?> { ["owner_id"] = 1, ["n"] = 500 });
            var rows = await context.QueryAllAsync("items", columns: new[] { "n" }, sort: new[] { "n_desc" });
            return string.Join(',', rows.Select(r => Convert.ToString(r["n"], CultureInfo.InvariantCulture)));
        }
    }

    private sealed class ReadWindow : IBifrostOperation
    {
        public string Name => "readWindow";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
            => (await context.QueryAsync("items", columns: new[] { "n" }, limit: -1)).Count;
    }

    /// <summary>Scopes <c>items</c> to the caller's own rows.</summary>
    private sealed class OwnerScope : IFilterTransformer
    {
        public int Priority => 200;
        public bool AppliesTo(IDbTable table, QueryTransformContext context) => table.DbName == "items";
        public TableFilter? GetAdditionalFilter(IDbTable table, QueryTransformContext context)
            => TableFilterFactory.Equals(table, "owner_id",
                int.Parse(PolicyIdentity.FromUserContext(context.UserContext).Id, CultureInfo.InvariantCulture));
    }

    private sealed class SqlRecorder(ConcurrentQueue<string> sql) : IQueryObserver
    {
        public QueryPhase[] Phases => new[] { QueryPhase.AfterExecute };

        public ValueTask OnQueryPhaseAsync(QueryPhase phase, QueryObserverContext context)
        {
            if (context.Sql is { } text) sql.Enqueue(text);
            return ValueTask.CompletedTask;
        }
    }
}
