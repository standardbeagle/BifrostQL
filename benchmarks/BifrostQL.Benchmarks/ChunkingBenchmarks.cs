using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BifrostQL.Server;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Benchmarks comparing chunked vs unchunked serialization and reassembly.
/// Measures the overhead of splitting into chunks, serializing each chunk,
/// and reassembling vs sending the entire message as a single frame.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
public class ChunkingBenchmarks
{
    [Params(100, 1000, 5000)]
    public int RowCount { get; set; }

    private BifrostMessage _result = null!;
    private ChunkSender _sender = null!;

    [GlobalSetup]
    public void Setup()
    {
        _result = BuildLargeResultMessage(RowCount);
        _sender = new ChunkSender(chunkThreshold: 64 * 1024, ackWindow: 8);
    }

    [Benchmark(Description = "Unchunked: serialize entire message")]
    public byte[] Unchunked_Serialize()
    {
        return _result.ToBytes();
    }

    [Benchmark(Description = "Chunked: split + serialize all chunks")]
    public int Chunked_SplitAndSerialize()
    {
        var chunks = _sender.SplitIntoChunks(_result);
        var totalBytes = 0;
        for (var i = 0; i < chunks.Count; i++)
        {
            totalBytes += chunks[i].ToBytes().Length;
        }
        return totalBytes;
    }

    [Benchmark(Description = "Unchunked: deserialize entire message")]
    public BifrostMessage Unchunked_Deserialize()
    {
        var bytes = _result.ToBytes();
        return BifrostMessage.FromBytes(bytes);
    }

    [Benchmark(Description = "Chunked: split + reassemble + deserialize")]
    public BifrostMessage Chunked_SplitAndReassemble()
    {
        var chunks = _sender.SplitIntoChunks(_result);
        var receiver = new ChunkReceiver();
        byte[]? assembled = null;
        for (var i = 0; i < chunks.Count; i++)
        {
            assembled = receiver.AddChunk(chunks[i]);
        }
        return BifrostMessage.FromBytes(assembled!);
    }

    private static BifrostMessage BuildLargeResultMessage(int rowCount)
    {
        var rows = new List<Dictionary<string, object?>>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = i + 1,
                ["order_number"] = $"ORD-{2024000 + i}",
                ["customer_name"] = $"Customer {i + 1}",
                ["customer_email"] = $"customer{i + 1}@company.com",
                ["product_name"] = $"Product {(i % 50) + 1} - Extended Name for Realism",
                ["quantity"] = (i % 20) + 1,
                ["unit_price"] = 9.99m + (i % 100) * 5.50m,
                ["total_price"] = (9.99m + (i % 100) * 5.50m) * ((i % 20) + 1),
                ["order_date"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i * 3),
                ["shipping_address"] = $"{100 + i} Main St, City {i % 200}, ST {(10000 + i % 90000)}",
                ["status"] = (i % 5) switch { 0 => "pending", 1 => "processing", 2 => "shipped", 3 => "delivered", _ => "cancelled" },
                ["notes"] = i % 3 == 0 ? null : $"Order notes with tracking info and special instructions for order {i + 1}",
            });
        }

        var payload = new { data = new { orders = rows } };
        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        return new BifrostMessage
        {
            RequestId = 1,
            Type = BifrostMessageType.Result,
            Payload = payloadBytes,
        };
    }
}
