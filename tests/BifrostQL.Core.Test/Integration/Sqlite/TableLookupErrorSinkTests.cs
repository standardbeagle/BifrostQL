using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// The server-side half of "sanitize the wire, keep the detail" (findings H6/M31;
/// task 01M1RAV45GJJEFZPDBW4NSNBEQ): a client-shape table miss drops the
/// caller-supplied name from the wire text, and the shared
/// <see cref="BifrostErrorSink"/> seam must log that raw detail server-side in the
/// same call. Each fact drives the PRODUCTION executor (no fake model/lookup — the
/// same builders <see cref="TableLookupWireSafetyTests"/> uses) and asserts the
/// sink captured the raw name while the wire error stayed sanitized.
/// </summary>
public sealed class TableLookupErrorSinkTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_table_lookup_sink_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private const string PhantomTable = "m31_phantom_table";

    // Shared in-memory SQLite connections die with the last open handle; hold one.
    private Microsoft.Data.Sqlite.SqliteConnection? _conn;

    public async Task InitializeAsync()
    {
        _conn = new Microsoft.Data.Sqlite.SqliteConnection(ConnString);
        await _conn.OpenAsync();
        await using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
            "CREATE TABLE IF NOT EXISTS orders (id INTEGER PRIMARY KEY, name TEXT NOT NULL)", _conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (_conn != null) await _conn.DisposeAsync();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    private static IDisposable CaptureSink(CapturingLogger logger)
        => BifrostErrorSink.OverrideForTesting(logger);

    private static MutationIntentExecutor BuildMutationExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });
        return new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
    }

    private static QueryIntentExecutor BuildQueryExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        return new QueryIntentExecutor(pathCache, transformerService);
    }

    [Fact]
    public async Task MutationIntent_UnknownTable_LogsRawNameServerSide()
    {
        var logger = new CapturingLogger();
        using (CaptureSink(logger))
        {
            var executor = BuildMutationExecutor();

            var act = () => executor.ExecuteAsync(new MutationIntent
            {
                Table = PhantomTable,
                Action = MutationIntentAction.Insert,
                Data = new Dictionary<string, object?> { ["name"] = "x" },
                Endpoint = EndpointPath,
            });

            var error = (await act.Should().ThrowAsync<BifrostExecutionError>()).Which;
            error.Message.Should().NotContain(PhantomTable,
                "the wire text stays sanitized");
            logger.Messages.Should().Contain(m => m.Contains(PhantomTable),
                "the shared seam must log the raw caller-supplied name server-side on a miss");
        }
    }

    [Fact]
    public async Task QueryIntent_UnknownTable_LogsRawNameServerSide()
    {
        var logger = new CapturingLogger();
        using (CaptureSink(logger))
        {
            var executor = BuildQueryExecutor();
            var phantom = DbModelTestFixture.Create()
                .WithTable(PhantomTable, t => t.WithPrimaryKey("id"))
                .Build()
                .GetTableFromDbName(PhantomTable);
            var query = new GqlObjectQuery
            {
                DbTable = phantom,
                SchemaName = phantom.TableSchema,
                TableName = phantom.DbName,
                GraphQlName = phantom.GraphQlName,
                Path = phantom.GraphQlName,
                ScalarColumns = { new GqlObjectColumn("id") },
            };

            var act = () => executor.ExecuteAsync(new QueryIntent
            {
                Query = query,
                Endpoint = EndpointPath,
            });

            var error = (await act.Should().ThrowAsync<BifrostExecutionError>()).Which;
            error.Message.Should().NotContain(PhantomTable,
                "the wire text stays sanitized");
            logger.Messages.Should().Contain(m => m.Contains(PhantomTable),
                "the shared seam must log the raw caller-supplied name server-side on a miss");
        }
    }

    [Fact]
    public async Task MutationIntent_UnknownTable_LogsRawNameWithForeignLoggerAttached()
    {
        using var foreign = CaptureSink(new CapturingLogger());
        var logger = new CapturingLogger();
        using var local = CaptureSink(logger);
        var executor = BuildMutationExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = PhantomTable,
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "x" },
            Endpoint = EndpointPath,
        });

        var error = (await act.Should().ThrowAsync<BifrostExecutionError>()).Which;
        error.Message.Should().NotContain(PhantomTable);
        logger.Messages.Should().Contain(m => m.Contains(PhantomTable));
    }

    [Fact]
    public async Task MutationIntent_ConcurrentNoServiceExecutorCannotClobberCapturedLogger()
    {
        var logger = new CapturingLogger();
        using var local = CaptureSink(logger);
        var task = Task.Run(BuildMutationExecutor);
        var executor = BuildMutationExecutor();
        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = PhantomTable,
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "x" },
            Endpoint = EndpointPath,
        });

        var error = (await act.Should().ThrowAsync<BifrostExecutionError>()).Which;
        await task;
        error.Message.Should().NotContain(PhantomTable);
        logger.Messages.Should().Contain(m => m.Contains(PhantomTable));
    }
}
