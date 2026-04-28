# Product Requirements Document: Server-Side Batch Performance Optimizations

**Document Version**: 1.0  
**Date**: 2026-04-08  
**Status**: Draft  
**Related Task**: [DART-RchAOMmmULhA]  
**Related Benchmarks**: [DART-RCuZ8nMC9wvi]

---

## 1. Executive Summary

### 1.1 Overview
This document outlines the product requirements for implementing server-side batch performance optimizations in BifrostQL. The goal is to significantly improve bulk operation throughput (INSERT, UPDATE, DELETE) by leveraging database-specific batch capabilities, connection pooling, and efficient transaction management.

### 1.2 Current Performance Gap
Based on benchmark analysis (see [BulkOperationBenchmarks.cs](../benchmarks/BifrostQL.Benchmarks/BulkOperationBenchmarks.cs)), the current implementation has the following performance characteristics:

| Operation | 1K Rows | 10K Rows | 100K Rows |
|-----------|---------|----------|-----------|
| Raw ADO.NET (baseline) | ~50ms | ~500ms | ~5,000ms |
| Individual INSERTs | ~200ms | ~2,000ms | ~20,000ms |
| Multi-row INSERT | ~25ms | ~250ms | ~2,500ms |

**Key Finding**: Multi-row INSERT is **4-8x faster** than individual INSERTs, yet the current `DbTableBatchResolver` processes each action as a separate command.

### 1.3 Target Improvements
- **10x improvement** in bulk INSERT throughput
- **5x improvement** in bulk UPDATE throughput  
- **5x improvement** in bulk DELETE throughput
- **50% reduction** in memory allocations during batch operations
- Support for **streaming large datasets** without loading entire payload into memory

---

## 2. Current State Analysis

### 2.1 Architecture Overview

```
GraphQL Mutation
       ↓
DbTableBatchResolver
       ↓
  ┌────┴────┬────────┬────────┐
  ↓         ↓        ↓        ↓
Insert    Update   Delete   Upsert
  │         │        │        │
  └────┬────┴────────┴────────┘
       ↓
Individual SQL Commands
       ↓
   Database
```

### 2.2 Current Bottlenecks

#### 2.2.1 N+1 Command Problem
The `DbTableBatchResolver` (see [source](../src/BifrostQL.Core/Resolvers/DbTableBatchResolver.cs)) processes each action in a loop:

```csharp
foreach (var action in actions)
{
    totalAffected += await ExecuteAction(action, ...); // Separate command per action
}
```

**Impact**: For 100 INSERT actions, 100 separate `DbCommand` objects are created and executed.

#### 2.2.2 No Prepared Statement Reuse
Each command is constructed dynamically without using `cmd.Prepare()`:

```csharp
await using var cmd = conn.CreateCommand();
cmd.CommandText = $"INSERT INTO {tableRef}({columns}) VALUES({values});";
// cmd.Prepare() is NOT called
```

**Impact**: Database cannot reuse query plans, leading to parsing overhead.

#### 2.2.3 Lack of Multi-Row Operations
The current implementation generates single-row SQL statements:

```sql
INSERT INTO users (name, email) VALUES (@name, @email);
INSERT INTO users (name, email) VALUES (@name, @email);
-- ... repeated N times
```

**Benchmark Evidence**: Multi-row INSERT syntax is significantly faster:

```sql
INSERT INTO users (name, email) VALUES 
    (@name1, @email1),
    (@name2, @email2),
    ...;
```

#### 2.2.4 Connection Per Operation
The `DbTableInsertResolver` creates a new connection for each operation:

```csharp
private static async ValueTask<object?> ExecuteScalar(IDbConnFactory connFactory, ...)
{
    await using var conn = connFactory.GetConnection(); // New connection each time
    await conn.OpenAsync();
    // ...
}
```

**Impact**: Connection establishment overhead for each mutation.

#### 2.2.5 No Streaming Support
Large datasets must be fully loaded into memory before processing:

```csharp
var actions = context.GetArgument<List<Dictionary<string, object?>>>("actions");
// All actions loaded into memory at once
```

**Impact**: Memory pressure and potential OOM for large batches.

### 2.3 Database-Specific Capabilities

| Database | Multi-Row INSERT | Bulk Copy API | Prepared Statements | RETURNING |
|----------|------------------|---------------|---------------------|-----------|
| SQL Server | ✓ | SqlBulkCopy | ✓ | ✓ (OUTPUT) |
| PostgreSQL | ✓ | COPY | ✓ | ✓ |
| MySQL | ✓ | LOAD DATA | ✓ | ✗ |
| SQLite | ✓ | ✗ | ✓ | ✓ |

---

## 3. Proposed Optimizations

### 3.1 Optimization 1: Multi-Row INSERT Batching

#### Description
Group multiple INSERT operations into a single multi-row INSERT statement.

#### Implementation Approach
```csharp
// New: BatchSqlGenerator class
public class BatchSqlGenerator
{
    public string GenerateMultiRowInsert(
        string tableRef, 
        List<string> columns, 
        int rowCount,
        ISqlDialect dialect)
    {
        // Generates: INSERT INTO table (col1, col2) VALUES (@p0, @p1), (@p2, @p3), ...
    }
}
```

#### Expected Performance Gain
- **8x faster** for 1K rows (based on benchmarks)
- **Reduced network round trips**: 1 vs N

#### Database Support
All supported databases (SQL Server, PostgreSQL, MySQL, SQLite)

---

### 3.2 Optimization 2: Prepared Statement Caching

#### Description
Cache and reuse prepared statements within a batch operation.

#### Implementation Approach
```csharp
public class PreparedStatementCache
{
    private readonly Dictionary<string, DbCommand> _cache = new();
    
    public DbCommand GetOrCreate(string sql, DbConnection conn)
    {
        if (!_cache.TryGetValue(sql, out var cmd))
        {
            cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.Prepare(); // Compile once
            _cache[sql] = cmd;
        }
        return cmd;
    }
}
```

#### Expected Performance Gain
- **20-30% reduction** in CPU usage for repeated operations
- **Reduced memory allocations** (no repeated SQL string building)

---

### 3.3 Optimization 3: Bulk UPDATE with CASE Statements

#### Description
For updating multiple rows with different values, use CASE statements instead of individual UPDATEs.

#### Implementation Approach
```sql
-- Instead of:
UPDATE users SET balance = 100 WHERE id = 1;
UPDATE users SET balance = 200 WHERE id = 2;
-- ... N times

-- Use:
UPDATE users SET balance = CASE id
    WHEN 1 THEN 100
    WHEN 2 THEN 200
    -- ...
END
WHERE id IN (1, 2, ...);
```

#### Expected Performance Gain
- **5-10x faster** for bulk updates
- Single table scan vs N scans

#### Limitations
- Best for updates with different values per row
- Maximum case statement size may apply

---

### 3.4 Optimization 4: Efficient Bulk DELETE

#### Description
Use IN clause for batch deletes instead of individual DELETE statements.

#### Implementation Approach
```sql
-- Instead of:
DELETE FROM users WHERE id = 1;
DELETE FROM users WHERE id = 2;
-- ... N times

-- Use:
DELETE FROM users WHERE id IN (@p0, @p1, @p2, ...);
```

#### Expected Performance Gain
- **5-8x faster** for bulk deletes
- Single table scan vs N scans

---

### 3.5 Optimization 5: Connection Pooling Integration

#### Description
Ensure proper use of connection pooling and consider connection reuse within batch operations.

#### Implementation Approach
```csharp
public class BatchConnectionManager
{
    private DbConnection? _sharedConnection;
    private int _operationCount;
    private const int MaxOperationsPerConnection = 1000;
    
    public async Task<DbConnection> GetConnectionAsync()
    {
        if (_sharedConnection == null || _operationCount >= MaxOperationsPerConnection)
        {
            _sharedConnection?.Dispose();
            _sharedConnection = await CreateNewConnectionAsync();
            _operationCount = 0;
        }
        _operationCount++;
        return _sharedConnection;
    }
}
```

#### Expected Performance Gain
- **Eliminate connection setup/teardown overhead** within batches
- **Better resource utilization**

---

### 3.6 Optimization 6: Async Streaming for Large Datasets

#### Description
Process large batches as streams instead of loading entire payload into memory.

#### Implementation Approach
```csharp
public interface IBatchActionStream
{
    IAsyncEnumerable<BatchAction> GetActionsAsync();
}

public class StreamingBatchResolver
{
    public async Task<int> ProcessStreamAsync(IBatchActionStream stream, ...)
    {
        var batch = new List<BatchAction>(BatchSize);
        await foreach (var action in stream.GetActionsAsync())
        {
            batch.Add(action);
            if (batch.Count >= BatchSize)
            {
                await ExecuteBatchAsync(batch);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            await ExecuteBatchAsync(batch);
    }
}
```

#### Expected Performance Gain
- **Constant memory usage** regardless of batch size
- **Progress reporting** capability
- **Cancellation support** at row level

---

### 3.7 Optimization 7: Database-Specific Bulk Copy APIs

#### Description
Use native bulk copy APIs (SqlBulkCopy, PostgreSQL COPY) for maximum performance.

#### Implementation Approach
```csharp
public interface IBulkCopyProvider
{
    Task<int> BulkInsertAsync(IDataReader reader, string tableName);
}

public class SqlServerBulkCopyProvider : IBulkCopyProvider
{
    public async Task<int> BulkInsertAsync(IDataReader reader, string tableName)
    {
        using var bulkCopy = new SqlBulkCopy(_connection);
        bulkCopy.DestinationTableName = tableName;
        await bulkCopy.WriteToServerAsync(reader);
        return bulkCopy.RowsCopied;
    }
}
```

#### Expected Performance Gain
- **10-50x faster** for very large datasets (100K+ rows)
- **Minimal logging overhead**

#### Database Support
- SQL Server: SqlBulkCopy
- PostgreSQL: COPY command
- MySQL: LOAD DATA or MySqlBulkLoader
- SQLite: Not supported (fallback to multi-row INSERT)

---

## 4. Implementation Plan

### 4.1 Phase 1: Multi-Row INSERT (Week 1-2)

**Scope**: Implement multi-row INSERT batching as the foundation.

#### Tasks
1. Create `BatchSqlGenerator` class
2. Modify `DbTableBatchResolver` to group INSERT actions
3. Add batch size limits (configurable, default 1000)
4. Write unit tests for batch SQL generation
5. Update benchmarks to measure improvement

#### Acceptance Criteria
- [ ] Multi-row INSERT generates correct SQL for all dialects
- [ ] 1000 INSERTs execute in single command
- [ ] Benchmarks show 5x+ improvement
- [ ] Existing tests pass

---

### 4.2 Phase 2: Prepared Statement Caching (Week 2-3)

**Scope**: Add prepared statement reuse within batch operations.

#### Tasks
1. Create `PreparedStatementCache` class
2. Integrate with `DbTableBatchResolver`
3. Add cache eviction policies
4. Memory profiling to ensure no leaks

#### Acceptance Criteria
- [ ] Prepared statements reused within batch
- [ ] Memory usage reduced by 30%+
- [ ] No statement handle leaks

---

### 4.3 Phase 3: Bulk UPDATE and DELETE (Week 3-4)

**Scope**: Implement CASE-based UPDATE and IN-based DELETE.

#### Tasks
1. Implement `GenerateCaseUpdateSql()` method
2. Implement `GenerateInDeleteSql()` method
3. Add optimizer to choose strategy (single vs bulk)
4. Handle edge cases (NULL values, large IN clauses)

#### Acceptance Criteria
- [ ] Bulk UPDATE uses CASE statements when beneficial
- [ ] Bulk DELETE uses IN clause
- [ ] Performance improvement measurable in benchmarks
- [ ] Correctness verified with edge cases

---

### 4.4 Phase 4: Connection Pooling & Streaming (Week 4-5)

**Scope**: Implement connection reuse and async streaming.

#### Tasks
1. Create `BatchConnectionManager`
2. Implement `IBatchActionStream` interface
3. Add streaming GraphQL resolver
4. Add progress reporting hooks

#### Acceptance Criteria
- [ ] Connection reused within batch transaction
- [ ] Large batches stream without OOM
- [ ] Progress callbacks work correctly

---

### 4.5 Phase 5: Database-Specific Bulk Copy (Week 5-6)

**Scope**: Implement native bulk copy APIs for SQL Server and PostgreSQL.

#### Tasks
1. Create `IBulkCopyProvider` interface
2. Implement SQL Server provider (SqlBulkCopy)
3. Implement PostgreSQL provider (COPY)
4. Add configuration options for bulk copy thresholds

#### Acceptance Criteria
- [ ] SqlBulkCopy used for SQL Server large batches
- [ ] COPY used for PostgreSQL large batches
- [ ] Fallback to multi-row INSERT for other databases
- [ ] 10x+ improvement for 100K+ row batches

---

### 4.6 Phase 6: Integration & Optimization (Week 6-7)

**Scope**: Integration testing, performance tuning, and documentation.

#### Tasks
1. End-to-end integration tests
2. Performance regression testing
3. Configuration documentation
4. Migration guide for users

#### Acceptance Criteria
- [ ] All existing tests pass
- [ ] No performance regressions
- [ ] Documentation complete
- [ ] Migration guide published

---

## 5. Success Metrics

### 5.1 Performance Targets

| Metric | Current | Target | Measurement |
|--------|---------|--------|-------------|
| INSERT 1K rows | 200ms | 25ms | BenchmarkDotNet |
| INSERT 10K rows | 2,000ms | 250ms | BenchmarkDotNet |
| INSERT 100K rows | 20,000ms | 2,000ms | BenchmarkDotNet |
| UPDATE 10% of 10K | 150ms | 30ms | BenchmarkDotNet |
| DELETE 10% of 10K | 100ms | 20ms | BenchmarkDotNet |
| Memory per 1K rows | 5MB | 2MB | MemoryDiagnoser |
| Throughput (rows/sec) | 5,000 | 50,000 | ThroughputBenchmarks |

### 5.2 Quality Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Test Coverage | >90% | Coverlet |
| Benchmark Regression | <5% | CI Benchmark |
| Memory Leaks | 0 | dotMemory |
| Thread Safety | Pass | Parallel Tests |

### 5.3 Operational Metrics

| Metric | Target | Measurement |
|--------|--------|-------------|
| Configuration Options | <5 | Code Review |
| Breaking Changes | 0 | API Diff |
| Migration Effort | <1 hour | Documentation |

---

## 6. Risks and Mitigations

### 6.1 Risk: SQL Injection in Dynamic SQL

**Description**: Multi-row INSERT requires dynamic parameter names which could be vulnerable to injection.

**Mitigation**:
- Use parameterized queries exclusively
- Validate all identifiers against schema
- Use `SqlParameter` objects, never string concatenation

**Verification**:
- Security audit of `BatchSqlGenerator`
- Fuzz testing with malicious inputs

---

### 6.2 Risk: Database Parameter Limits

**Description**: Some databases have limits on parameter count (e.g., SQL Server: 2100 parameters).

**Mitigation**:
- Calculate max rows per batch based on column count
- Auto-split large batches into multiple commands
- Document limits in configuration

**Verification**:
- Test with maximum column tables
- Verify auto-splitting behavior

---

### 6.3 Risk: Transaction Timeout

**Description**: Large batches may exceed database transaction timeouts.

**Mitigation**:
- Add configurable batch size limits
- Support chunked transactions
- Document timeout configuration

**Verification**:
- Load testing with large batches
- Verify timeout handling

---

### 6.4 Risk: Memory Pressure in CASE Statements

**Description**: Very large CASE statements may cause memory issues.

**Mitigation**:
- Limit CASE statement size (max 1000 branches)
- Fall back to individual UPDATEs for very large sets
- Monitor memory during benchmarks

**Verification**:
- Memory profiling with large UPDATE batches

---

### 6.5 Risk: Breaking Changes to API

**Description**: Changes to batch processing may affect existing behavior.

**Mitigation**:
- Maintain backward compatibility
- Use feature flags for new optimizations
- Deprecation cycle for old behavior

**Verification**:
- Full regression test suite
- API compatibility checks

---

### 6.6 Risk: Database-Specific Bugs

**Description**: Optimizations may behave differently across database providers.

**Mitigation**:
- Comprehensive testing for each dialect
- Abstract database-specific logic
- Clear fallback behavior

**Verification**:
- CI tests for all supported databases
- Dialect-specific benchmark suite

---

## 7. Configuration

### 7.1 Proposed Configuration Options

```json
{
  "BifrostQL": {
    "Batch": {
      "MaxBatchSize": 1000,
      "UseMultiRowInsert": true,
      "UsePreparedStatements": true,
      "UseBulkUpdate": true,
      "UseBulkDelete": true,
      "UseStreaming": true,
      "BulkCopyThreshold": 10000,
      "MaxParametersPerCommand": 2000,
      "MaxCaseBranches": 1000,
      "EnableProgressReporting": false
    }
  }
}
```

### 7.2 Per-Table Configuration

```csharp
// In schema configuration
public class TableBatchConfig
{
    public string TableName { get; set; }
    public int? MaxBatchSize { get; set; }
    public bool? UseBulkCopy { get; set; }
    public string[] ExcludeColumnsFromBulk { get; set; }
}
```

---

## 8. Appendix

### 8.1 Related Documents

- [Bulk Operation Benchmarks](../benchmarks/BifrostQL.Benchmarks/BulkOperationBenchmarks.cs)
- [DbTableBatchResolver](../src/BifrostQL.Core/Resolvers/DbTableBatchResolver.cs)
- [ISqlDialect](../src/BifrostQL.Core/QueryModel/ISqlDialect.cs)
- [Multi-Database Benchmarks](../benchmarks/BifrostQL.Benchmarks/MultiDatabaseBenchmarks.cs)

### 8.2 Benchmark Results Reference

See [BenchmarkDotNet.Artifacts](../BenchmarkDotNet.Artifacts/) for latest benchmark results.

### 8.3 Database-Specific Notes

#### SQL Server
- Use `SqlBulkCopy` for maximum performance
- Consider `TABLOCK` hint for minimal logging
- OUTPUT clause for returning identity values

#### PostgreSQL
- Use `COPY` command for bulk operations
- Consider `UNLOGGED` tables for temporary data
- RETURNING clause supported

#### MySQL
- Use `LOAD DATA INFILE` or `MySqlBulkLoader`
- Consider `INSERT DELAYED` for non-critical data
- No RETURNING support

#### SQLite
- Multi-row INSERT is the best option
- Consider `PRAGMA synchronous = OFF` for bulk loads
- RETURNING clause supported (v3.35+)

---

## 9. Review and Approval

| Role | Name | Date | Status |
|------|------|------|--------|
| Author | AI Assistant | 2026-04-08 | Draft |
| Technical Review | TBD | - | Pending |
| Product Owner | TBD | - | Pending |
| QA Review | TBD | - | Pending |

---

**Next Steps**:
1. Review PRD with stakeholders
2. Prioritize phases based on business needs
3. Create detailed technical design documents for Phase 1
4. Schedule implementation sprint
