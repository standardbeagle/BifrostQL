using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Jobs;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Comprehensive benchmarks for BifrostQL bulk operations.
/// Measures insert, update, and delete performance at various scales.
/// Compares different approaches: raw ADO.NET, individual statements, and multi-row inserts.
/// 
/// Based on UkrGuru's MassSqlDemo approach for measuring bulk SQL performance.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
[SimpleJob(launchCount: 1, warmupCount: 3, iterationCount: 5)]
public class BulkOperationBenchmarks : IDisposable
{
    private BulkOperationContext _context = null!;
    private List<Dictionary<string, object?>> _insertData1K = null!;
    private List<Dictionary<string, object?>> _insertData10K = null!;
    private List<Dictionary<string, object?>> _insertData100K = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = new BulkOperationContext();
        _context.Initialize();
        
        // Pre-generate test data to avoid generation overhead in benchmarks
        _insertData1K = _context.GenerateInsertData(1_000);
        _insertData10K = _context.GenerateInsertData(10_000);
        _insertData100K = _context.GenerateInsertData(100_000);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _context.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _context.ClearTable();
    }

    public void Dispose()
    {
        _context?.Dispose();
    }

    #region Bulk Insert Benchmarks

    /// <summary>
    /// Baseline: Raw ADO.NET with prepared statement and transaction.
    /// This represents the theoretical maximum performance.
    /// </summary>
    [Benchmark(Description = "Raw ADO.NET - 1K rows", Baseline = true)]
    public int RawAdoNet_Insert_1K()
    {
        return _context.RawAdoNetBulkInsert(_insertData1K);
    }

    [Benchmark(Description = "Raw ADO.NET - 10K rows")]
    public int RawAdoNet_Insert_10K()
    {
        return _context.RawAdoNetBulkInsert(_insertData10K);
    }

    [Benchmark(Description = "Raw ADO.NET - 100K rows")]
    public int RawAdoNet_Insert_100K()
    {
        return _context.RawAdoNetBulkInsert(_insertData100K);
    }

    /// <summary>
    /// Individual INSERT statements within a transaction.
    /// Simulates the BifrostQL batch approach where each row is a separate command.
    /// </summary>
    [Benchmark(Description = "Individual INSERTs - 1K rows")]
    public int IndividualInsert_1K()
    {
        return _context.IndividualInsertBulk(_insertData1K);
    }

    [Benchmark(Description = "Individual INSERTs - 10K rows")]
    public int IndividualInsert_10K()
    {
        return _context.IndividualInsertBulk(_insertData10K);
    }

    [Benchmark(Description = "Individual INSERTs - 100K rows")]
    public int IndividualInsert_100K()
    {
        return _context.IndividualInsertBulk(_insertData100K);
    }

    /// <summary>
    /// Multi-row INSERT using SQLite's VALUES (row1), (row2), ... syntax.
    /// More efficient for bulk operations.
    /// </summary>
    [Benchmark(Description = "Multi-row INSERT - 1K rows")]
    public int MultiRowInsert_1K()
    {
        return _context.MultiRowInsertBulk(_insertData1K);
    }

    [Benchmark(Description = "Multi-row INSERT - 10K rows")]
    public int MultiRowInsert_10K()
    {
        return _context.MultiRowInsertBulk(_insertData10K);
    }

    [Benchmark(Description = "Multi-row INSERT - 100K rows")]
    public int MultiRowInsert_100K()
    {
        return _context.MultiRowInsertBulk(_insertData100K);
    }

    #endregion

    #region Bulk Update Benchmarks

    /// <summary>
    /// Setup for update benchmarks - inserts test data first.
    /// </summary>
    private void SetupForUpdate(int rowCount)
    {
        _context.ClearTable();
        var data = _context.GenerateInsertData(rowCount);
        _context.MultiRowInsertBulk(data);
    }

    /// <summary>
    /// Generates update data for existing rows.
    /// </summary>
    private List<Dictionary<string, object?>> GenerateUpdateData(int rowCount, double percentage)
    {
        var count = (int)(rowCount * percentage);
        var data = new List<Dictionary<string, object?>>(count);
        
        for (int i = 1; i <= count; i++)
        {
            data.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = i,
                ["balance"] = 5000.0 + (i * 25.0),
                ["is_active"] = i % 2 == 0 ? 1 : 0,
            });
        }
        
        return data;
    }

    [Benchmark(Description = "Raw ADO.NET Update 10% of 10K")]
    public int RawAdoNet_Update_10Percent()
    {
        SetupForUpdate(10_000);
        var updateData = GenerateUpdateData(10_000, 0.10);
        return _context.RawAdoNetBulkUpdate(updateData);
    }

    [Benchmark(Description = "Raw ADO.NET Update 50% of 10K")]
    public int RawAdoNet_Update_50Percent()
    {
        SetupForUpdate(10_000);
        var updateData = GenerateUpdateData(10_000, 0.50);
        return _context.RawAdoNetBulkUpdate(updateData);
    }

    [Benchmark(Description = "Raw ADO.NET Update 100% of 10K")]
    public int RawAdoNet_Update_100Percent()
    {
        SetupForUpdate(10_000);
        var updateData = GenerateUpdateData(10_000, 1.0);
        return _context.RawAdoNetBulkUpdate(updateData);
    }

    [Benchmark(Description = "Raw ADO.NET Update 100% of 100K")]
    public int RawAdoNet_Update_100K()
    {
        SetupForUpdate(100_000);
        var updateData = GenerateUpdateData(100_000, 1.0);
        return _context.RawAdoNetBulkUpdate(updateData);
    }

    #endregion

    #region Bulk Delete Benchmarks

    /// <summary>
    /// Setup for delete benchmarks - inserts test data first.
    /// </summary>
    private void SetupForDelete(int rowCount)
    {
        _context.ClearTable();
        var data = _context.GenerateInsertData(rowCount);
        _context.MultiRowInsertBulk(data);
    }

    /// <summary>
    /// Generates IDs to delete.
    /// </summary>
    private List<int> GenerateDeleteIds(int rowCount, double percentage)
    {
        var count = (int)(rowCount * percentage);
        return Enumerable.Range(1, count).ToList();
    }

    [Benchmark(Description = "Raw ADO.NET Delete 10% of 10K")]
    public int RawAdoNet_Delete_10Percent()
    {
        SetupForDelete(10_000);
        var ids = GenerateDeleteIds(10_000, 0.10);
        return _context.RawAdoNetBulkDelete(ids);
    }

    [Benchmark(Description = "Raw ADO.NET Delete 50% of 10K")]
    public int RawAdoNet_Delete_50Percent()
    {
        SetupForDelete(10_000);
        var ids = GenerateDeleteIds(10_000, 0.50);
        return _context.RawAdoNetBulkDelete(ids);
    }

    [Benchmark(Description = "Raw ADO.NET Delete 100% of 10K")]
    public int RawAdoNet_Delete_100Percent()
    {
        SetupForDelete(10_000);
        var ids = GenerateDeleteIds(10_000, 1.0);
        return _context.RawAdoNetBulkDelete(ids);
    }

    [Benchmark(Description = "Raw ADO.NET Delete 100% of 100K")]
    public int RawAdoNet_Delete_100K()
    {
        SetupForDelete(100_000);
        var ids = GenerateDeleteIds(100_000, 1.0);
        return _context.RawAdoNetBulkDelete(ids);
    }

    #endregion
}

/// <summary>
/// Throughput-focused benchmarks that report rows per second.
/// These use OperationsPerInvoke to calculate throughput metrics.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
[SimpleJob(launchCount: 1, warmupCount: 2, iterationCount: 3)]
public class BulkOperationThroughputBenchmarks : IDisposable
{
    private const int BatchSize = 100;
    private BulkOperationContext _context = null!;
    private List<Dictionary<string, object?>> _insertData = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = new BulkOperationContext();
        _context.Initialize();
        _insertData = _context.GenerateInsertData(BatchSize);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _context.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _context.ClearTable();
    }

    public void Dispose()
    {
        _context?.Dispose();
    }

    /// <summary>
    /// Measures throughput of raw ADO.NET inserts.
    /// Reports rows per second.
    /// </summary>
    [Benchmark(OperationsPerInvoke = BatchSize, Description = "Raw ADO.NET throughput (rows/sec)")]
    public int RawAdoNet_Throughput()
    {
        // We run multiple batches to get stable measurements
        var totalRows = 0;
        for (int i = 0; i < 10; i++)
        {
            totalRows += _context.RawAdoNetBulkInsert(_insertData);
        }
        return totalRows;
    }

    /// <summary>
    /// Measures throughput of individual INSERT statements.
    /// </summary>
    [Benchmark(OperationsPerInvoke = BatchSize, Description = "Individual INSERT throughput (rows/sec)")]
    public int IndividualInsert_Throughput()
    {
        var totalRows = 0;
        for (int i = 0; i < 10; i++)
        {
            totalRows += _context.IndividualInsertBulk(_insertData);
        }
        return totalRows;
    }

    /// <summary>
    /// Measures throughput of multi-row INSERT.
    /// </summary>
    [Benchmark(OperationsPerInvoke = BatchSize, Description = "Multi-row INSERT throughput (rows/sec)")]
    public int MultiRowInsert_Throughput()
    {
        var totalRows = 0;
        for (int i = 0; i < 10; i++)
        {
            totalRows += _context.MultiRowInsertBulk(_insertData);
        }
        return totalRows;
    }
}

/// <summary>
/// Memory-focused benchmarks for bulk operations.
/// Measures allocations at different data sizes.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev, Column.Median, Column.Mean)]
[SimpleJob(iterationCount: 1, warmupCount: 0, launchCount: 1)]
public class BulkOperationMemoryBenchmarks : IDisposable
{
    private BulkOperationContext _context = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _context = new BulkOperationContext();
        _context.Initialize();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _context.Dispose();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _context.ClearTable();
    }

    public void Dispose()
    {
        _context?.Dispose();
    }

    [Params(100, 1_000, 10_000)]
    public int RowCount { get; set; }

    /// <summary>
    /// Measures memory allocations for generating insert data.
    /// </summary>
    [Benchmark(Description = "Generate insert data")]
    public int GenerateData()
    {
        var data = _context.GenerateInsertData(RowCount);
        return data.Count;
    }

    /// <summary>
    /// Measures memory allocations during raw ADO.NET bulk insert.
    /// </summary>
    [Benchmark(Description = "Raw ADO.NET insert")]
    public int RawAdoNet_Insert_Memory()
    {
        var data = _context.GenerateInsertData(RowCount);
        return _context.RawAdoNetBulkInsert(data);
    }

    /// <summary>
    /// Measures memory allocations during individual INSERT bulk operation.
    /// </summary>
    [Benchmark(Description = "Individual INSERTs")]
    public int IndividualInsert_Memory()
    {
        var data = _context.GenerateInsertData(RowCount);
        return _context.IndividualInsertBulk(data);
    }

    /// <summary>
    /// Measures memory allocations during multi-row INSERT bulk operation.
    /// </summary>
    [Benchmark(Description = "Multi-row INSERT")]
    public int MultiRowInsert_Memory()
    {
        var data = _context.GenerateInsertData(RowCount);
        return _context.MultiRowInsertBulk(data);
    }
}
