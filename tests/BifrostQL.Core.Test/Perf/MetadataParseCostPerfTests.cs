using System.Diagnostics;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using GraphQL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;

namespace BifrostQL.Core.Test.Perf;

/// <summary>
/// What per-table metadata parsing costs on the read path: one
/// <see cref="ComputedColumnConfigCollector.FromTable(IDbTable, IDbModel?)"/> call, one
/// <see cref="PolicyConfigCollector.FromTable"/> call, and a whole GraphQL request
/// (40 columns x 100 rows, with <c>_can</c>) over in-memory SQLite to set them against.
/// </summary>
public sealed class MetadataParseCostPerfTests : IAsyncLifetime
{
    private const int Columns = 40;
    private const int Rows = 100;

    private readonly ITestOutputHelper _output;
    private readonly string _connString = $"Data Source=bifrost_meta_parse_perf_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private SqliteConnection _keepAlive = null!;
    private IDbModel _model = null!;
    private IDbTable _table = null!;

    public MetadataParseCostPerfTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(_connString);
        await _keepAlive.OpenAsync();
        var columns = string.Join(", ", Enumerable.Range(0, Columns).Select(i => $"c{i} TEXT NULL"));
        await using (var ddl = new SqliteCommand($"CREATE TABLE members (id INTEGER PRIMARY KEY, user_id INTEGER NOT NULL, {columns});", _keepAlive))
            await ddl.ExecuteNonQueryAsync();
        var values = string.Join(", ", Enumerable.Range(1, Rows).Select(i =>
            $"({i}, {i % 3}, " + string.Join(", ", Enumerable.Range(0, Columns).Select(c => $"'v{c}'")) + ")"));
        await using (var seed = new SqliteCommand($"INSERT INTO members VALUES {values};", _keepAlive))
            await seed.ExecuteNonQueryAsync();

        _model = await new DbModelLoader(new SqliteDbConnFactory(_connString), new MetadataLoader(new[]
        {
            "main.members { policy-actions: read,update,delete; policy-read-deny: c39; policy-row-scope-exempt: time.edit_others }",
        })).LoadAsync();
        _table = _model.GetTableFromDbName("members");
        _table.Metadata[MetadataKeys.Policy.RowScope] = "user_id = {user_id}";
        _table.Metadata[MetadataKeys.Computed.Expression] = "label:String:c0 || '-' || c1";
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    [PerfFact]
    public async Task MeasureParseCostAgainstARequest()
    {
        var computedNs = PerCallNanoseconds(() => ComputedColumnConfigCollector.FromTable(_table, _model), out var computedBytes);
        var policyNs = PerCallNanoseconds(() => PolicyConfigCollector.FromTable(_table), out var policyBytes);

        var selection = string.Join(" ", Enumerable.Range(0, Columns - 1).Select(i => $"c{i}"));
        var query = $"{{ members(sort: [id_asc], limit: {Rows}) {{ data {{ id {selection} label _can {{ update delete }} }} }} }}";
        var schema = DbSchema.FromModel(_model);
        var services = new ServiceCollection();
        services.AddSingleton<IComputedColumnProvider>(_ => new RowCapabilityProvider());
        services.AddSingleton<IComputedColumnProviders>(sp => new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));
        await using var provider = services.BuildServiceProvider();
        var executor = new DocumentExecuter();
        // The host's built-in read filters, so the policy is consulted where production consults it.
        var transformers = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new PolicyFilterTransformer(), new TenantFilterTransformer(),
                new SoftDeleteFilterTransformer(), new AutoFilterTransformer(),
            },
        });
        var factory = new SqliteDbConnFactory(_connString);

        async Task Run()
        {
            var result = await executor.ExecuteAsync(options =>
            {
                options.Schema = schema;
                options.Query = query;
                options.RequestServices = provider;
                options.UserContext = new Dictionary<string, object?> { ["user_id"] = 1 };
                options.Extensions = new Inputs(new Dictionary<string, object?>
                {
                    ["connFactory"] = factory,
                    ["model"] = _model,
                    // One manager per request, as production builds it: it memoizes the parsed document.
                    ["tableReaderFactory"] = new SqlExecutionManager(_model, schema, transformers),
                });
            });
            if (result.Errors is { Count: > 0 })
                throw new InvalidOperationException(result.Errors[0].Message);
        }

        for (var i = 0; i < 50; i++) await Run();
        const int requests = 300;
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < requests; i++) await Run();
        sw.Stop();
        var requestUs = sw.Elapsed.TotalMilliseconds * 1000 / requests;

        _output.WriteLine($"ComputedColumnConfigCollector.FromTable: {computedNs:F0} ns/call, {computedBytes} B/call");
        _output.WriteLine($"PolicyConfigCollector.FromTable:        {policyNs:F0} ns/call, {policyBytes} B/call");
        _output.WriteLine($"GraphQL request ({Columns} cols x {Rows} rows, _can): {requestUs:F0} us/request");
    }

    private static double PerCallNanoseconds(Func<object> call, out long bytesPerCall)
    {
        for (var i = 0; i < 20_000; i++) call();
        const int iterations = 200_000;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++) call();
        sw.Stop();
        bytesPerCall = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
        return sw.Elapsed.TotalMilliseconds * 1_000_000 / iterations;
    }
}
