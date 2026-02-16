using BenchmarkDotNet.Running;
using BifrostQL.Benchmarks;

if (args.Length > 0 && args[0] == "--sizes")
{
    PrintPayloadSizes();
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(BifrostMessageBenchmarks).Assembly).Run(args);

static void PrintPayloadSizes()
{
    Console.WriteLine("Payload Size Comparison: Protobuf vs JSON");
    Console.WriteLine(new string('=', 70));
    Console.WriteLine($"{"Rows",-8} {"Protobuf (B)",-15} {"JSON (B)",-15} {"Ratio",-10} {"Savings"}");
    Console.WriteLine(new string('-', 70));

    foreach (var rowCount in new[] { 1, 10, 50, 100, 500, 1000 })
    {
        var msg = BuildResult(rowCount);
        var protoBytes = msg.ToBytes().Length;
        var jsonBytes = System.Text.Encoding.UTF8.GetByteCount(ToJson(msg));
        var ratio = (double)protoBytes / jsonBytes;
        var savings = (1.0 - ratio) * 100;
        Console.WriteLine($"{rowCount,-8} {protoBytes,-15:N0} {jsonBytes,-15:N0} {ratio,-10:F3} {savings:F1}%");
    }

    static BifrostQL.Server.BifrostMessage BuildResult(int rowCount)
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
        var payloadBytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload));
        return new BifrostQL.Server.BifrostMessage
        {
            RequestId = 1,
            Type = BifrostQL.Server.BifrostMessageType.Result,
            Payload = payloadBytes,
        };
    }

    static string ToJson(BifrostQL.Server.BifrostMessage msg)
    {
        var obj = new BifrostQL.Benchmarks.JsonBifrostMessage
        {
            RequestId = msg.RequestId,
            Type = (int)msg.Type,
            Payload = msg.Payload.Length > 0 ? Convert.ToBase64String(msg.Payload) : null,
        };
        return System.Text.Json.JsonSerializer.Serialize(obj);
    }
}
