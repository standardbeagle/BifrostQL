# BifrostQL Bulk Operation Benchmarks

This document describes the bulk operation benchmarks for BifrostQL, based on UkrGuru's MassSqlDemo approach for measuring bulk SQL performance.

## Overview

The bulk operation benchmarks measure the performance of database operations at scale, comparing different approaches:

- **Raw ADO.NET**: Prepared statements with transactions (baseline)
- **Individual INSERTs**: Separate SQL command per row (simulates BifrostQL batch approach)
- **Multi-row INSERT**: Single INSERT with multiple VALUES clauses

## Benchmark Classes

### 1. BulkOperationBenchmarks

Main benchmark class measuring latency for bulk operations at different scales.

**Insert Benchmarks:**
- `RawAdoNet_Insert_1K` - Raw ADO.NET insert of 1,000 rows
- `RawAdoNet_Insert_10K` - Raw ADO.NET insert of 10,000 rows
- `RawAdoNet_Insert_100K` - Raw ADO.NET insert of 100,000 rows
- `IndividualInsert_1K` - Individual INSERT statements, 1,000 rows
- `IndividualInsert_10K` - Individual INSERT statements, 10,000 rows
- `IndividualInsert_100K` - Individual INSERT statements, 100,000 rows
- `MultiRowInsert_1K` - Multi-row INSERT, 1,000 rows
- `MultiRowInsert_10K` - Multi-row INSERT, 10,000 rows
- `MultiRowInsert_100K` - Multi-row INSERT, 100,000 rows

**Update Benchmarks:**
- `RawAdoNet_Update_10Percent` - Update 10% of 10K rows
- `RawAdoNet_Update_50Percent` - Update 50% of 10K rows
- `RawAdoNet_Update_100Percent` - Update 100% of 10K rows
- `RawAdoNet_Update_100K` - Update 100% of 100K rows

**Delete Benchmarks:**
- `RawAdoNet_Delete_10Percent` - Delete 10% of 10K rows
- `RawAdoNet_Delete_50Percent` - Delete 50% of 10K rows
- `RawAdoNet_Delete_100Percent` - Delete 100% of 10K rows
- `RawAdoNet_Delete_100K` - Delete 100% of 100K rows

### 2. BulkOperationThroughputBenchmarks

Throughput-focused benchmarks reporting rows per second using `OperationsPerInvoke`.

- `RawAdoNet_Throughput` - Raw ADO.NET throughput
- `IndividualInsert_Throughput` - Individual INSERT throughput
- `MultiRowInsert_Throughput` - Multi-row INSERT throughput

### 3. BulkOperationMemoryBenchmarks

Memory allocation benchmarks at different data sizes.

**Parameters:** 100, 1,000, 10,000 rows

- `GenerateData` - Memory to generate test data
- `RawAdoNet_Insert_Memory` - Memory during raw ADO.NET insert
- `IndividualInsert_Memory` - Memory during individual INSERTs
- `MultiRowInsert_Memory` - Memory during multi-row INSERT

## Test Schema

```sql
CREATE TABLE benchmark_users (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    email TEXT NOT NULL,
    age INTEGER,
    balance REAL,
    is_active INTEGER DEFAULT 1,
    created_at TEXT DEFAULT CURRENT_TIMESTAMP,
    department TEXT
);

CREATE INDEX idx_users_email ON benchmark_users(email);
CREATE INDEX idx_users_department ON benchmark_users(department);
```

## Running the Benchmarks

### Run all bulk operation benchmarks:
```bash
dotnet run --project benchmarks/BifrostQL.Benchmarks --configuration Release -- --filter "*BulkOperation*"
```

### Run specific benchmark class:
```bash
dotnet run --project benchmarks/BifrostQL.Benchmarks --configuration Release -- --filter "*BulkOperationBenchmarks"
```

### Run with short job for quick testing:
```bash
dotnet run --project benchmarks/BifrostQL.Benchmarks --configuration Release -- --filter "*BulkOperation*" --job short
```

### Run only insert benchmarks:
```bash
dotnet run --project benchmarks/BifrostQL.Benchmarks --configuration Release -- --filter "*BulkOperationBenchmarks*Insert*"
```

## Implementation Details

### BulkOperationContext

The `BulkOperationContext` class provides:

- **SQLite in-memory database**: Fast, consistent test environment
- **Test data generation**: Creates realistic user records with varied data types
- **Raw ADO.NET baseline**: Prepared statements with transaction wrapping
- **Individual insert simulation**: Separate commands per row (like BifrostQL batch)
- **Multi-row insert**: Optimized bulk insert using SQLite's multi-row VALUES syntax

### Key Techniques

1. **Prepared Statements**: All raw ADO.NET operations use prepared commands for optimal performance
2. **Transaction Wrapping**: Each bulk operation is wrapped in a transaction
3. **Parameter Reuse**: Parameters are reused across rows in prepared statements
4. **Memory Efficiency**: Test data is pre-generated to exclude generation overhead from measurements

## Expected Results

Based on typical SQLite in-memory performance:

| Operation | 1K Rows | 10K Rows | 100K Rows |
|-----------|---------|----------|-----------|
| Raw ADO.NET | ~10ms | ~85ms | ~550ms |
| Individual INSERTs | ~25ms | ~250ms | ~2500ms |
| Multi-row INSERT | ~160ms | ~800ms | ~8000ms |

**Key Observations:**
- Raw ADO.NET with prepared statements is fastest for bulk operations
- Individual INSERTs are 2-5x slower than raw ADO.NET
- Multi-row INSERT is slower for SQLite due to SQL parsing overhead
- For BifrostQL, the individual INSERT approach (current batch implementation) provides good balance of performance and flexibility

## Comparison with BifrostQL

These benchmarks establish baselines for comparing BifrostQL's batch operation performance:

1. **Current BifrostQL Approach**: Uses individual INSERT/UPDATE/DELETE statements within a transaction (similar to `IndividualInsertBulk`)
2. **Optimization Opportunity**: Multi-row INSERT could be added as an optimization for large batches
3. **Throughput Target**: BifrostQL batch operations should aim for within 2x of raw ADO.NET performance

## Files

- `BulkOperationBenchmarks.cs` - Main benchmark classes
- `BulkOperationContext.cs` - Database context and helper methods
- `BifrostQL.Benchmarks.csproj` - Updated to include SQLite dependency

## References

- Based on UkrGuru's MassSqlDemo approach for bulk SQL performance testing
- Uses BenchmarkDotNet for accurate, reproducible measurements
- SQLite in-memory database for consistent, fast test execution
