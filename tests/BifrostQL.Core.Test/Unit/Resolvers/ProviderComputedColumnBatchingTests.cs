using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using GraphQL.Types;
using NSubstitute;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Proves <see cref="SqlExecutionManager"/> computes provider-backed columns with
/// bounded parallelism instead of one sequential await per row (the N+1 shape), while
/// keeping per-row results in row order, capping concurrency, and threading the
/// request's cancellation token into each <c>ComputeAsync</c> call.
/// </summary>
public sealed class ProviderComputedColumnBatchingTests
{
    private const string ProviderName = "test-provider";
    private const string ColumnName = "calc";

    /// <summary>Per-row provider that records observed concurrency and tokens.</summary>
    private sealed class RecordingProvider : IComputedColumnProvider
    {
        private int _inFlight;
        public int MaxObservedConcurrency;
        public int CallCount;
        public readonly List<CancellationToken> Tokens = new();

        public string Name => ProviderName;

        public async ValueTask<object?> ComputeAsync(ComputedColumnContext context, CancellationToken cancellationToken = default)
        {
            var now = Interlocked.Increment(ref _inFlight);
            InterlockedMax(ref MaxObservedConcurrency, now);
            Interlocked.Increment(ref CallCount);
            lock (Tokens) Tokens.Add(cancellationToken);

            // Hold the slot long enough for other workers to overlap.
            await Task.Delay(25, cancellationToken);
            Interlocked.Decrement(ref _inFlight);

            // Value derived from the row so ordering is verifiable.
            return $"calc-{context.Row["Id"]}";
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while (value > (current = Volatile.Read(ref target)) &&
                   Interlocked.CompareExchange(ref target, value, current) != current)
            {
            }
        }
    }

    private static IDbModel BuildModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true))
            .Build();

    private static GqlObjectQuery BuildQuery(IDbModel model) => new()
    {
        DbTable = model.GetTableFromDbName("Orders"),
        TableName = "Orders",
        SchemaName = "dbo",
        GraphQlName = "Orders",
        ScalarColumns =
        {
            new GqlObjectColumn("Id"),
            new GqlObjectColumn(
                new ComputedColumnDefinition(ColumnName, "String", ComputedColumnKind.Provider, ProviderName, Array.Empty<string>()),
                ColumnName),
        },
    };

    private static IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> BuildResults(string resultName, int rowCount)
    {
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Id"] = 0 };
        var rows = new List<object?[]>();
        for (var i = 0; i < rowCount; i++)
            rows.Add(new object?[] { i });
        return new Dictionary<string, (IDictionary<string, int>, IList<object?[]>)>
        {
            [resultName] = (index, rows),
        };
    }

    private static async Task InvokeApplyAsync(
        SqlExecutionManager manager,
        GqlObjectQuery query,
        string resultName,
        IDictionary<string, (IDictionary<string, int> index, IList<object?[]> data)> results,
        IComputedColumnProviders providers,
        IBifrostFieldContext context)
    {
        var method = typeof(SqlExecutionManager).GetMethod(
            "ApplyProviderComputedColumnsForQueryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        method.Should().NotBeNull();
        var connFactory = Substitute.For<IDbConnFactory>();
        await (ValueTask)method!.Invoke(manager, new object?[] { query, resultName, results, providers, context, connFactory })!;
    }

    private static IBifrostFieldContext MakeContext(CancellationToken token)
    {
        var context = Substitute.For<IBifrostFieldContext>();
        context.CancellationToken.Returns(token);
        context.UserContext.Returns(new Dictionary<string, object?>());
        context.RequestServices.Returns((IServiceProvider?)null);
        return context;
    }

    [Fact]
    public async Task ProviderColumns_ComputedForAllRows_InRowOrder_WithBoundedParallelism()
    {
        // Arrange — 32 rows against a provider that holds each call ~25ms.
        var model = BuildModel();
        var manager = new SqlExecutionManager(model, Substitute.For<ISchema>());
        var query = BuildQuery(model);
        var results = BuildResults(query.KeyName, rowCount: 32);
        var provider = new RecordingProvider();
        using var cts = new CancellationTokenSource();

        // Act
        await InvokeApplyAsync(manager, query, query.KeyName, results, new ComputedColumnProviders(new[] { provider }), MakeContext(cts.Token));

        // Assert — every row got its own value, landed at the new column index, in order.
        provider.CallCount.Should().Be(32);
        var (index, data) = results[query.KeyName];
        index.Should().ContainKey(ColumnName);
        var calcIndex = index[ColumnName];
        for (var i = 0; i < data.Count; i++)
            data[i][calcIndex].Should().Be($"calc-{i}");

        // Assert — rows overlapped (no sequential N+1) but respected the cap.
        provider.MaxObservedConcurrency.Should().BeGreaterThan(1, "per-row computes must run in parallel");
        provider.MaxObservedConcurrency.Should().BeLessThanOrEqualTo(8, "parallelism must stay bounded");

        // Assert — the request token reached every provider call.
        provider.Tokens.Should().OnlyContain(t => t == cts.Token);
    }

    [Fact]
    public async Task ProviderColumns_NoRows_DoesNotInvokeProvider()
    {
        // Arrange
        var model = BuildModel();
        var manager = new SqlExecutionManager(model, Substitute.For<ISchema>());
        var query = BuildQuery(model);
        var results = BuildResults(query.KeyName, rowCount: 0);
        var provider = new RecordingProvider();

        // Act
        await InvokeApplyAsync(manager, query, query.KeyName, results, new ComputedColumnProviders(new[] { provider }), MakeContext(CancellationToken.None));

        // Assert — column registered, no compute calls issued.
        provider.CallCount.Should().Be(0);
        results[query.KeyName].index.Should().ContainKey(ColumnName);
    }
}
