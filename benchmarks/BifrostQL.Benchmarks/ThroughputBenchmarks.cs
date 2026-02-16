using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BifrostQL.Server;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Measures message throughput (serialize-deserialize cycles per second) for protobuf vs JSON.
/// Uses OperationsPerInvoke to report per-message rates from batched iterations.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
public class ThroughputBenchmarks
{
    private const int BatchSize = 1000;

    private BifrostMessage _queryMsg = null!;
    private BifrostMessage _resultMsg = null!;

    [GlobalSetup]
    public void Setup()
    {
        _queryMsg = new BifrostMessage
        {
            RequestId = 1,
            Type = BifrostMessageType.Query,
            Query = "{ users(filter: { status: { _eq: \"active\" } }, limit: 25) { id name email role { name } } }",
            VariablesJson = JsonSerializer.Serialize(new { tenantId = "org_12345" }),
        };

        _resultMsg = BuildResultMessage(50);
    }

    [Benchmark(OperationsPerInvoke = BatchSize, Description = "Protobuf query roundtrip/s")]
    public int Protobuf_Query_Throughput()
    {
        var count = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var bytes = _queryMsg.ToBytes();
            var msg = BifrostMessage.FromBytes(bytes);
            count += (int)msg.RequestId;
        }
        return count;
    }

    [Benchmark(OperationsPerInvoke = BatchSize, Description = "JSON query roundtrip/s")]
    public int Json_Query_Throughput()
    {
        var count = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var json = ToJson(_queryMsg);
            var msg = FromJson(json);
            count += (int)msg.RequestId;
        }
        return count;
    }

    [Benchmark(OperationsPerInvoke = BatchSize, Description = "Protobuf result roundtrip/s")]
    public int Protobuf_Result_Throughput()
    {
        var count = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var bytes = _resultMsg.ToBytes();
            var msg = BifrostMessage.FromBytes(bytes);
            count += (int)msg.RequestId;
        }
        return count;
    }

    [Benchmark(OperationsPerInvoke = BatchSize, Description = "JSON result roundtrip/s")]
    public int Json_Result_Throughput()
    {
        var count = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            var json = ToJson(_resultMsg);
            var msg = FromJson(json);
            count += (int)msg.RequestId;
        }
        return count;
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
                ["balance"] = 500.0m + i * 12.75m,
                ["is_active"] = i % 4 != 0,
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
            Query = msg.Query,
            VariablesJson = msg.VariablesJson,
            Payload = msg.Payload.Length > 0 ? Convert.ToBase64String(msg.Payload) : null,
            Errors = msg.Errors.Count > 0 ? msg.Errors : null,
        };
        return JsonSerializer.Serialize(obj);
    }

    private static JsonBifrostMessage FromJson(string json)
    {
        return JsonSerializer.Deserialize<JsonBifrostMessage>(json)!;
    }
}
