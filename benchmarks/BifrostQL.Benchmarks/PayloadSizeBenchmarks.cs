using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BifrostQL.Server;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Measures serialized payload sizes for protobuf vs JSON across different result set sizes.
/// Reports both raw byte counts and the size ratio (protobuf / JSON).
/// </summary>
[SimpleJob(iterationCount: 1, warmupCount: 0, launchCount: 1)]
[HideColumns(Column.Error, Column.StdDev, Column.Median, Column.Mean)]
public class PayloadSizeBenchmarks
{
    [Params(1, 10, 100, 500, 1000)]
    public int RowCount { get; set; }

    private BifrostMessage _result = null!;

    [GlobalSetup]
    public void Setup()
    {
        _result = BuildResultMessage(RowCount);
    }

    [Benchmark(Description = "Protobuf bytes")]
    public int Protobuf_Size()
    {
        return _result.ToBytes().Length;
    }

    [Benchmark(Description = "JSON bytes")]
    public int Json_Size()
    {
        return Encoding.UTF8.GetByteCount(ToJson(_result));
    }

    private static BifrostMessage BuildResultMessage(int rowCount)
    {
        var rows = new List<Dictionary<string, object?>>(rowCount);
        for (var i = 0; i < rowCount; i++)
        {
            rows.Add(new Dictionary<string, object?>
            {
                ["id"] = i + 1,
                ["name"] = $"User {i + 1}",
                ["email"] = $"user{i + 1}@example.com",
                ["created_at"] = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(i),
                ["balance"] = 1000.50m + i * 17.33m,
                ["is_active"] = i % 3 != 0,
                ["department"] = i % 5 == 0 ? null : $"Dept-{i % 5}",
                ["login_count"] = i * 42,
            });
        }

        var payload = new { data = new { users = rows } };
        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        return new BifrostMessage
        {
            RequestId = 1,
            Type = BifrostMessageType.Result,
            Payload = payloadBytes,
        };
    }

    private static string ToJson(BifrostMessage msg)
    {
        var obj = new JsonBifrostMessage
        {
            RequestId = msg.RequestId,
            Type = (int)msg.Type,
            Payload = msg.Payload.Length > 0 ? Convert.ToBase64String(msg.Payload) : null,
        };
        return JsonSerializer.Serialize(obj);
    }
}
