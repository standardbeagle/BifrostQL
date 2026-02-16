using System.Text;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BifrostQL.Server;

namespace BifrostQL.Benchmarks;

/// <summary>
/// Benchmarks comparing BifrostMessage protobuf wire format against JSON serialization
/// for typical GraphQL query result shapes.
/// </summary>
[MemoryDiagnoser]
[HideColumns(Column.Error, Column.StdDev)]
public class BifrostMessageBenchmarks
{
    private BifrostMessage _smallResult = null!;
    private BifrostMessage _mediumResult = null!;
    private BifrostMessage _largeResult = null!;
    private BifrostMessage _queryMessage = null!;

    private byte[] _smallProtobuf = null!;
    private byte[] _mediumProtobuf = null!;
    private byte[] _largeProtobuf = null!;
    private byte[] _queryProtobuf = null!;

    private string _smallJson = null!;
    private string _mediumJson = null!;
    private string _largeJson = null!;
    private string _queryJson = null!;

    [GlobalSetup]
    public void Setup()
    {
        _smallResult = BuildResultMessage(1, 10);
        _mediumResult = BuildResultMessage(2, 100);
        _largeResult = BuildResultMessage(3, 1000);

        _queryMessage = new BifrostMessage
        {
            RequestId = 42,
            Type = BifrostMessageType.Query,
            Query = "{ users(filter: { status: { _eq: \"active\" } }, sort: \"-created_at\", limit: 50) { id name email created_at role { id name } orders(limit: 5) { id total status } } }",
            VariablesJson = JsonSerializer.Serialize(new { tenantId = "org_abc123", userId = "usr_987" }),
        };

        _smallProtobuf = _smallResult.ToBytes();
        _mediumProtobuf = _mediumResult.ToBytes();
        _largeProtobuf = _largeResult.ToBytes();
        _queryProtobuf = _queryMessage.ToBytes();

        _smallJson = ToJson(_smallResult);
        _mediumJson = ToJson(_mediumResult);
        _largeJson = ToJson(_largeResult);
        _queryJson = ToJson(_queryMessage);
    }

    // -- Serialization benchmarks --

    [Benchmark(Description = "Protobuf Serialize 10 rows")]
    public byte[] Protobuf_Serialize_Small() => _smallResult.ToBytes();

    [Benchmark(Description = "JSON Serialize 10 rows")]
    public string Json_Serialize_Small() => ToJson(_smallResult);

    [Benchmark(Description = "Protobuf Serialize 100 rows")]
    public byte[] Protobuf_Serialize_Medium() => _mediumResult.ToBytes();

    [Benchmark(Description = "JSON Serialize 100 rows")]
    public string Json_Serialize_Medium() => ToJson(_mediumResult);

    [Benchmark(Description = "Protobuf Serialize 1000 rows")]
    public byte[] Protobuf_Serialize_Large() => _largeResult.ToBytes();

    [Benchmark(Description = "JSON Serialize 1000 rows")]
    public string Json_Serialize_Large() => ToJson(_largeResult);

    [Benchmark(Description = "Protobuf Serialize Query")]
    public byte[] Protobuf_Serialize_Query() => _queryMessage.ToBytes();

    [Benchmark(Description = "JSON Serialize Query")]
    public string Json_Serialize_Query() => ToJson(_queryMessage);

    // -- Deserialization benchmarks --

    [Benchmark(Description = "Protobuf Deserialize 10 rows")]
    public BifrostMessage Protobuf_Deserialize_Small() => BifrostMessage.FromBytes(_smallProtobuf);

    [Benchmark(Description = "JSON Deserialize 10 rows")]
    public JsonBifrostMessage Json_Deserialize_Small() => FromJson(_smallJson);

    [Benchmark(Description = "Protobuf Deserialize 100 rows")]
    public BifrostMessage Protobuf_Deserialize_Medium() => BifrostMessage.FromBytes(_mediumProtobuf);

    [Benchmark(Description = "JSON Deserialize 100 rows")]
    public JsonBifrostMessage Json_Deserialize_Medium() => FromJson(_mediumJson);

    [Benchmark(Description = "Protobuf Deserialize 1000 rows")]
    public BifrostMessage Protobuf_Deserialize_Large() => BifrostMessage.FromBytes(_largeProtobuf);

    [Benchmark(Description = "JSON Deserialize 1000 rows")]
    public JsonBifrostMessage Json_Deserialize_Large() => FromJson(_largeJson);

    [Benchmark(Description = "Protobuf Deserialize Query")]
    public BifrostMessage Protobuf_Deserialize_Query() => BifrostMessage.FromBytes(_queryProtobuf);

    [Benchmark(Description = "JSON Deserialize Query")]
    public JsonBifrostMessage Json_Deserialize_Query() => FromJson(_queryJson);

    // -- Roundtrip benchmarks --

    [Benchmark(Description = "Protobuf Roundtrip 100 rows")]
    public BifrostMessage Protobuf_Roundtrip_Medium()
    {
        var bytes = _mediumResult.ToBytes();
        return BifrostMessage.FromBytes(bytes);
    }

    [Benchmark(Description = "JSON Roundtrip 100 rows")]
    public JsonBifrostMessage Json_Roundtrip_Medium()
    {
        var json = ToJson(_mediumResult);
        return FromJson(json);
    }

    // -- Helpers --

    /// <summary>
    /// Builds a Result message with a realistic GraphQL query result payload.
    /// Each row simulates a user record with mixed column types (int, string, datetime, decimal, bool, nullable).
    /// </summary>
    private static BifrostMessage BuildResultMessage(uint requestId, int rowCount)
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
                ["department"] = i % 5 == 0 ? null : $"Department {i % 5}",
                ["login_count"] = i * 42,
                ["role_id"] = (i % 4) + 1,
                ["notes"] = i % 7 == 0 ? null : $"Notes for user {i + 1} with some additional text to simulate realistic payload sizes",
            });
        }

        var payload = new { data = new { users = rows } };
        var payloadBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        return new BifrostMessage
        {
            RequestId = requestId,
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
            ChunkSequence = msg.ChunkSequence,
            ChunkTotal = msg.ChunkTotal,
            ChunkOffset = msg.ChunkOffset,
            TotalBytes = msg.TotalBytes,
            ChunkChecksum = msg.ChunkChecksum,
            LastSequence = msg.LastSequence,
        };
        return JsonSerializer.Serialize(obj);
    }

    private static JsonBifrostMessage FromJson(string json)
    {
        return JsonSerializer.Deserialize<JsonBifrostMessage>(json)!;
    }
}

/// <summary>
/// JSON-equivalent representation of BifrostMessage for baseline comparison.
/// Payload is base64-encoded to match the binary field semantics.
/// </summary>
public sealed class JsonBifrostMessage
{
    public uint RequestId { get; set; }
    public int Type { get; set; }
    public string Query { get; set; } = "";
    public string VariablesJson { get; set; } = "";
    public string? Payload { get; set; }
    public List<string>? Errors { get; set; }
    public uint ChunkSequence { get; set; }
    public uint ChunkTotal { get; set; }
    public ulong ChunkOffset { get; set; }
    public ulong TotalBytes { get; set; }
    public uint ChunkChecksum { get; set; }
    public uint LastSequence { get; set; }
}
