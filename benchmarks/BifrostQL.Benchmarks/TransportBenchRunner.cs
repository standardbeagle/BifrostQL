using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Benchmarks;

/// <summary>
/// End-to-end transport comparison, invoked with <c>dotnet run -c Release -- --transport</c>.
/// One Kestrel host on a loopback port serves a SQLite table of typed columns over both the
/// HTTP/JSON endpoint and the binary WebSocket endpoint, and the same grid-shaped query is
/// timed through each:
///
///  - single   : one query at a time, by result size. Latency median/p95 and bytes allocated
///               per query (process-wide, so the client's share is included on both sides).
///  - parallel : a burst of concurrent page-sized queries, the shape an editor issues when a
///               view opens. HTTP runs them on parallel connections; the binary client sends
///               them all on its one socket, as the TypeScript client does.
///
/// Wall-clock harness, not BenchmarkDotNet: the interesting costs are socket round trips and
/// whole-request allocation, which process-isolated micro-benchmarks do not see.
/// </summary>
public static class TransportBenchRunner
{
    private const string GraphQlPath = "/graphql";
    private const string SocketPath = "/bifrost-ws";
    private const int TableRows = 20_000;

    public static async Task RunAsync(string[] args)
    {
        var iterations = IntArg(args, "--iterations", 30);
        var burst = IntArg(args, "--burst", 8);

        var dbPath = Path.Combine(Path.GetTempPath(), $"bifrost_transport_bench_{Guid.NewGuid():N}.db");
        var connString = $"Data Source={dbPath}";
        await SeedAsync(connString);

        await using var app = await StartHostAsync(connString);
        var baseUri = new Uri(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First());

        using var http = new HttpClient { BaseAddress = baseUri };
        await using var binary = await BinaryBenchClient.ConnectAsync(
            new UriBuilder(baseUri) { Scheme = "ws", Path = SocketPath }.Uri);

        Console.WriteLine($"Transport bench: {TableRows:N0}-row SQLite table, {iterations} iterations, loopback Kestrel");
        Console.WriteLine();
        Console.WriteLine("| scenario | transport | median ms | p95 ms | KB on wire | KB allocated/op |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|");

        foreach (var rows in new[] { 100, 1_000, 10_000 })
        {
            var query = GridQuery(rows);
            await ReportAsync($"single {rows:N0} rows", "http", iterations, () => HttpQueryAsync(http, query));
            await ReportAsync($"single {rows:N0} rows", "binary", iterations, () => binary.QueryAsync(query));
        }

        var page = GridQuery(100);
        var large = GridQuery(10_000);
        await ReportAsync($"burst {burst}x100 rows", "http", iterations,
            () => BurstAsync(burst, () => HttpQueryAsync(http, page)));
        await ReportAsync($"burst {burst}x100 rows", "binary", iterations,
            () => BurstAsync(burst, () => binary.QueryAsync(page)));
        await ReportAsync($"10,000 rows + {burst}x100 rows", "http", Math.Max(5, iterations / 3),
            () => BurstAsync(1, () => HttpQueryAsync(http, large), burst, () => HttpQueryAsync(http, page)));
        await ReportAsync($"10,000 rows + {burst}x100 rows", "binary", Math.Max(5, iterations / 3),
            () => BurstAsync(1, () => binary.QueryAsync(large), burst, () => binary.QueryAsync(page)));

        await app.StopAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(dbPath);
    }

    private static string GridQuery(int limit) =>
        $"{{ items(limit: {limit}, sort: [id_asc]) {{ total offset limit data {{ id name sku qty price ratio created_at active notes }} }} }}";

    private static async Task<int> HttpQueryAsync(HttpClient http, string query)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        using var response = await http.PostAsync(GraphQlPath, content);
        var body = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Encoding.UTF8.GetString(body)}");
        EnsureRows(body);
        return body.Length;
    }

    private static void EnsureRows(byte[] body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("errors", out var errors))
            throw new InvalidOperationException("GraphQL errors: " + errors);
        if (doc.RootElement.GetProperty("data").GetProperty("items").GetProperty("data").GetArrayLength() == 0)
            throw new InvalidOperationException("No rows returned");
    }

    private static async Task<int> BurstAsync(int count, Func<Task<int>> query, int extraCount = 0, Func<Task<int>>? extra = null)
    {
        var tasks = new List<Task<int>>(count + extraCount);
        for (var i = 0; i < count; i++) tasks.Add(query());
        for (var i = 0; i < extraCount; i++) tasks.Add(extra!());
        var sizes = await Task.WhenAll(tasks);
        return sizes.Sum();
    }

    private static async Task ReportAsync(string scenario, string transport, int iterations, Func<Task<int>> run)
    {
        try
        {
            await run(); // warmup
            await run();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"| {scenario} | {transport} | fails: {ex.Message} | | | |");
            return;
        }
        var times = new double[iterations];
        var bytes = 0;
        GC.Collect();
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        for (var i = 0; i < iterations; i++)
        {
            var sw = Stopwatch.StartNew();
            bytes = await run();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }
        var allocated = (GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore) / iterations;
        Array.Sort(times);
        var median = times[times.Length / 2];
        var p95 = times[Math.Min(times.Length - 1, (int)(times.Length * 0.95))];
        Console.WriteLine($"| {scenario} | {transport} | {median:F2} | {p95:F2} | {bytes / 1024.0:N1} | {allocated / 1024.0:N0} |");
    }

    private static async Task SeedAsync(string connString)
    {
        await using var conn = new SqliteConnection(connString);
        await conn.OpenAsync();
        await using (var ddl = conn.CreateCommand())
        {
            ddl.CommandText = """
                CREATE TABLE items (
                    id INTEGER PRIMARY KEY,
                    name TEXT NOT NULL,
                    sku TEXT NOT NULL,
                    qty INTEGER NOT NULL,
                    price DECIMAL(18,2) NOT NULL,
                    ratio REAL NOT NULL,
                    created_at DATETIME NOT NULL,
                    -- INTEGER, not BOOLEAN: a SQLite BOOLEAN column fails to resolve
                    -- (the driver reads 0/1 as long, which the Boolean scalar rejects).
                    active INTEGER NOT NULL,
                    notes TEXT NULL
                );
                """;
            await ddl.ExecuteNonQueryAsync();
        }

        await using var tx = await conn.BeginTransactionAsync();
        await using var insert = conn.CreateCommand();
        insert.Transaction = (SqliteTransaction)tx;
        insert.CommandText = "INSERT INTO items VALUES ($id, $name, $sku, $qty, $price, $ratio, $created, $active, $notes)";
        var p = new[] { "$id", "$name", "$sku", "$qty", "$price", "$ratio", "$created", "$active", "$notes" }
            .Select(n => insert.Parameters.Add(new SqliteParameter { ParameterName = n })).ToArray();
        var start = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        for (var i = 1; i <= TableRows; i++)
        {
            p[0].Value = i;
            p[1].Value = $"Item number {i} with a typical product name";
            p[2].Value = $"SKU-{i:D8}";
            p[3].Value = i % 997;
            p[4].Value = Math.Round(i * 1.37m % 10_000, 2);
            p[5].Value = i / 7.0;
            p[6].Value = start.AddMinutes(i * 13).ToString("yyyy-MM-ddTHH:mm:ss");
            p[7].Value = i % 3 == 0;
            p[8].Value = i % 4 == 0 ? DBNull.Value : $"Restocked from warehouse {i % 17}; check shelf {i % 101}.";
            await insert.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private static async Task<WebApplication> StartHostAsync(string connString)
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Bifrost:DisableAuth"] = "true",
            ["Bifrost:Path"] = GraphQlPath,
        }).Build();
        builder.Services.AddBifrostQL(o => o
            .BindConfiguration(config.GetSection("Bifrost"))
            .BindConnectionString(connString)
            .BindProvider("sqlite"));
        builder.Services.AddBifrostEngine();

        var app = builder.Build();
        app.UseWebSockets();
        app.UseBifrostQL();
        app.UseBifrostBinary(SocketPath, graphqlPath: GraphQlPath);
        await app.StartAsync();
        return app;
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? int.Parse(args[index + 1]) : fallback;
    }

    /// <summary>
    /// A multiplexing binary-transport client, shaped like the TypeScript one: every query goes
    /// out on one socket, one receive loop routes frames by request id, and chunked responses
    /// are reassembled and acknowledged per chunk.
    /// </summary>
    private sealed class BinaryBenchClient : IAsyncDisposable
    {
        private readonly ClientWebSocket _socket;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly ConcurrentDictionary<uint, TaskCompletionSource<byte[]>> _pending = new();
        private readonly ChunkReceiver _chunks = new();
        private readonly Task _receiveLoop;
        private uint _nextId;

        private BinaryBenchClient(ClientWebSocket socket)
        {
            _socket = socket;
            _receiveLoop = Task.Run(ReceiveLoopAsync);
        }

        public static async Task<BinaryBenchClient> ConnectAsync(Uri uri)
        {
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(uri, CancellationToken.None);
            return new BinaryBenchClient(socket);
        }

        public async Task<int> QueryAsync(string query)
        {
            var id = Interlocked.Increment(ref _nextId);
            var pending = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = pending;
            await SendAsync(new BifrostMessage { RequestId = id, Type = BifrostMessageType.Query, Query = query });
            var payload = await pending.Task;
            EnsureRows(payload);
            return payload.Length;
        }

        private async Task SendAsync(BifrostMessage message)
        {
            var bytes = message.ToBytes();
            await _sendLock.WaitAsync();
            try { await _socket.SendAsync(bytes, WebSocketMessageType.Binary, true, CancellationToken.None); }
            finally { _sendLock.Release(); }
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[8 * 1024 * 1024];
            try
            {
                while (_socket.State == WebSocketState.Open)
                {
                    var count = 0;
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, count, buffer.Length - count), CancellationToken.None);
                        count += result.Count;
                    } while (!result.EndOfMessage);
                    if (result.MessageType == WebSocketMessageType.Close) break;

                    var frame = BifrostMessage.FromBytes(buffer, 0, count);
                    if (frame.Type == BifrostMessageType.Chunk)
                    {
                        var assembled = _chunks.AddChunk(frame);
                        await SendAsync(ChunkReceiver.CreateAck(frame.RequestId, frame.ChunkSequence));
                        if (assembled is not null)
                            Complete(frame.RequestId, BifrostMessage.FromBytes(assembled));
                        continue;
                    }
                    Complete(frame.RequestId, frame);
                }
            }
            catch (Exception ex)
            {
                foreach (var pending in _pending.Values) pending.TrySetException(ex);
            }
        }

        private void Complete(uint id, BifrostMessage frame)
        {
            if (!_pending.TryRemove(id, out var pending)) return;
            if (frame.Type == BifrostMessageType.Error || frame.Errors.Count > 0)
                pending.TrySetException(new InvalidOperationException(string.Join("; ", frame.Errors)));
            else
                pending.TrySetResult(frame.Payload);
        }

        public async ValueTask DisposeAsync()
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
            await _receiveLoop;
            _socket.Dispose();
        }
    }
}
