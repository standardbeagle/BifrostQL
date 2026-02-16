# BifrostQL Performance Benchmarks

## Summary

This directory contains BenchmarkDotNet performance tests comparing BifrostQL's binary protobuf transport against JSON baseline.

## Key Findings

### Payload Size Comparison (Protobuf vs JSON)

| Rows | Protobuf (B) | JSON (B) | Ratio | Savings |
|------|--------------|----------|-------|---------|
| 1    | 186          | 420      | 0.443 | 55.7%   |
| 10   | 1,662        | 2,388    | 0.696 | 30.4%   |
| 50   | 8,349        | 11,304   | 0.739 | 26.1%   |
| 100  | 16,730       | 22,476   | 0.744 | 25.6%   |
| 500  | 85,204       | 113,776  | 0.749 | 25.1%   |
| 1000 | 171,454      | 228,776  | 0.749 | 25.1%   |

**Conclusion:** Protobuf provides 25-56% payload size reduction compared to JSON, with the largest savings for small payloads (single rows). At scale (100+ rows), protobuf consistently delivers ~25% bandwidth savings.

### Benchmark Categories

1. **BifrostMessageBenchmarks.cs**
   - Serialization performance (ToBytes vs JSON)
   - Deserialization performance (FromBytes vs JSON)
   - Roundtrip performance (serialize + deserialize)
   - Tested across small (10 rows), medium (100 rows), and large (1000 rows) payloads

2. **PayloadSizeBenchmarks.cs**
   - Byte-level size comparison
   - Parameterized across 1, 10, 100, 500, 1000 row counts
   - Reports both absolute sizes and compression ratio

3. **ChunkingBenchmarks.cs**
   - Unchunked vs chunked serialization overhead
   - Reassembly performance
   - Tested with 100, 1000, 5000 row payloads

4. **ThroughputBenchmarks.cs**
   - Messages per second (query and result roundtrips)
   - Batch size: 1000 operations
   - Compares protobuf vs JSON throughput

## Running Benchmarks

### Full Benchmark Suite

```bash
cd benchmarks/BifrostQL.Benchmarks
dotnet run -c Release -- --filter '*'
```

**Note:** Full suite takes approximately 40-60 minutes to complete all 44 benchmarks.

### Quick Payload Size Check

```bash
cd benchmarks/BifrostQL.Benchmarks
dotnet run -c Release -- --sizes
```

Returns payload size comparison table in under 1 second.

### Run Specific Benchmark Category

```bash
# Serialization benchmarks only
dotnet run -c Release -- --filter '*BifrostMessageBenchmarks*'

# Payload size only
dotnet run -c Release -- --filter '*PayloadSizeBenchmarks*'

# Chunking only
dotnet run -c Release -- --filter '*ChunkingBenchmarks*'

# Throughput only
dotnet run -c Release -- --filter '*ThroughputBenchmarks*'
```

## Benchmark Configuration

- **BenchmarkDotNet Version:** 0.14.0
- **Target Framework:** net10.0
- **Memory Diagnostics:** Enabled (MemoryDiagnoser)
- **Data Realism:** All benchmarks use realistic query result shapes with mixed column types (int, string, datetime, decimal, bool, nullable)
- **No Hardcoded Data:** Result sets are procedurally generated with diverse data patterns

## Interpreting Results

BenchmarkDotNet reports include:

- **Mean:** Average execution time
- **Error:** Half of 99.9% confidence interval
- **StdDev:** Standard deviation of all measurements
- **Allocated:** Total allocated memory per operation
- **Gen0/Gen1/Gen2:** GC collection counts

Lower values are better for all metrics except throughput (higher = better).

## Project Structure

```
benchmarks/BifrostQL.Benchmarks/
├── BifrostMessageBenchmarks.cs  # Core serialization benchmarks
├── PayloadSizeBenchmarks.cs     # Size comparison
├── ChunkingBenchmarks.cs        # Chunking overhead
├── ThroughputBenchmarks.cs      # Messages per second
├── Program.cs                   # Entry point with --sizes flag
└── BifrostQL.Benchmarks.csproj  # BenchmarkDotNet project
```

## Next Steps

Future benchmarks to consider:

1. **Network simulation:** WebSocket overhead with real network conditions
2. **Compression:** Protobuf + gzip vs JSON + gzip
3. **Multi-protocol:** GraphQL JSON vs OData JSON vs Protobuf binary
4. **Concurrency:** Parallel request throughput
5. **Memory pressure:** Large result sets (10K+ rows) with GC impact
