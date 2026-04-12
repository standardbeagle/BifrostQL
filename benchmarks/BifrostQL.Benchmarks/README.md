# BifrostQL Benchmarks

This project contains performance benchmarks for BifrostQL using [BenchmarkDotNet](https://benchmarkdotnet.org/).

## Benchmark Categories

### 1. Multi-Database Benchmarks (`MultiDatabaseBenchmarks.cs`)

Cross-database performance comparison across SQL Server, PostgreSQL, MySQL, and SQLite.

#### Scenarios Covered:
- **Simple SELECT Queries**: Single table operations, filtering, pagination, aggregates
- **Complex JOIN Queries**: 2-table, 3-table, and 4-table joins with aggregation
- **INSERT Mutations**: Single row and batch inserts (10, 100 rows)
- **UPDATE Mutations**: Single and multiple row updates
- **DELETE Mutations**: Row deletion
- **Filter Operations**: Equality, range (BETWEEN), LIKE patterns, IN clauses, complex AND/OR
- **Schema Operations**: Table and column metadata queries

#### Running Multi-Database Benchmarks

```bash
# Run all multi-database benchmarks (SQLite only by default)
dotnet run --configuration Release -- --filter MultiDatabaseBenchmarks

# Run specific benchmark
dotnet run --configuration Release -- --filter "MultiDatabaseBenchmarks.SimpleSelectAll"

# Run dialect benchmarks
dotnet run --configuration Release -- --filter SqlDialectBenchmarks

# Run throughput benchmarks
dotnet run --configuration Release -- --filter MultiDatabaseThroughputBenchmarks
```

### 2. Bulk Operation Benchmarks (`BulkOperationBenchmarks.cs`)

Measures insert, update, and delete performance at various scales.

```bash
dotnet run --configuration Release -- --filter BulkOperationBenchmarks
```

### 3. Message Benchmarks (`BifrostMessageBenchmarks.cs`)

Tests serialization performance for BifrostQL's messaging protocol.

```bash
dotnet run --configuration Release -- --filter BifrostMessageBenchmarks
```

### 4. Other Benchmarks

- **ChunkingBenchmarks**: Message chunking performance
- **PayloadSizeBenchmarks**: Payload size analysis
- **ThroughputBenchmarks**: Throughput measurements

## Configuration

### Database Connection Strings

To run benchmarks against SQL Server, PostgreSQL, or MySQL, set the following environment variables:

```bash
export BIFROST_BENCH_SQLSERVER="Server=localhost;Database=bifrost_bench;User Id=sa;Password=...;TrustServerCertificate=True"
export BIFROST_BENCH_POSTGRES="Host=localhost;Database=bifrost_bench;Username=postgres;Password=..."
export BIFROST_BENCH_MYSQL="Server=localhost;Database=bifrost_bench;Uid=root;Pwd=..."
```

Then modify the `Provider` property in the benchmark class to include the desired providers:

```csharp
[Params(DatabaseProvider.Sqlite, DatabaseProvider.SqlServer, DatabaseProvider.PostgreSQL, DatabaseProvider.MySQL)]
public DatabaseProvider Provider { get; set; }
```

## Interpreting Results

BenchmarkDotNet produces detailed statistics including:

- **Mean**: Average execution time
- **Error**: Half of 99.9% confidence interval
- **StdDev**: Standard deviation
- **Gen0/Gen1/Gen2**: Garbage collection counts
- **Allocated**: Memory allocated per operation

Example output:
```
| Method | Provider | Mean | Error | StdDev | Gen0 | Allocated |
|--------|----------|------|-------|--------|------|-----------|
| SimpleSelectAll | Sqlite | 45.23 μs | 0.89 μs | 1.20 μs | 0.0610 | 10.5 KB |
```

## Best Practices

1. **Always run in Release mode**: `dotnet run --configuration Release`
2. **Close other applications**: Minimize background noise
3. **Run multiple times**: Results can vary between runs
4. **Use filters**: Run only the benchmarks you need
5. **Check memory**: Watch allocation numbers for memory pressure

## Adding New Benchmarks

1. Create a new class with the `[MemoryDiagnoser]` attribute
2. Add `[GlobalSetup]` and `[GlobalCleanup]` methods
3. Use `[Benchmark]` attribute on test methods
4. Follow existing patterns for database context management

Example:
```csharp
[MemoryDiagnoser]
public class MyBenchmarks
{
    private MultiDatabaseContext _context = null!;

    [GlobalSetup]
    public void Setup()
    {
        _context = new MultiDatabaseContext(DatabaseProvider.Sqlite);
        _context.Initialize();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _context.Dispose();
    }

    [Benchmark]
    public int MyBenchmark()
    {
        return _context.ExecuteQuery("SELECT * FROM Categories");
    }
}
```

## Documentation

See [DatabasePerformanceComparison.md](../../docs/DatabasePerformanceComparison.md) for detailed analysis and optimization recommendations.
