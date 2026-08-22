using System.Net;
using System.Net.Sockets;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server;
using BifrostQL.Server.Resp;
using BifrostQL.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Benchmarks;

/// <summary>
/// The SAME seeded SQLite database served two ways — the RESP (Redis-protocol)
/// adapter on a real loopback socket, and GraphQL through the real HTTP
/// middleware on the in-process TestServer — measuring equivalent operations:
/// single-row read, batched read, single- and two-column updates. Both paths
/// run the full intent pipeline (transformers unskippable), so the numbers
/// compare protocol + engine cost over identical SQL work.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[HideColumns(Column.Error, Column.StdDev)]
public class RespVsGraphQLBenchmarks
{
    private const int Ops = 200;
    private const string ConnString = "Data Source=bench_resp_gql;Mode=Memory;Cache=Shared";

    private SqliteConnection _keepAlive = null!;
    private IHost _host = null!;
    private HttpClient _graphql = null!;
    private TcpListener _listener = null!;
    private CancellationTokenSource _cts = null!;
    private RespClient _resp = null!;
    private int _cursor;

    [GlobalSetup]
    public void Setup()
    {
        _keepAlive = new SqliteConnection(ConnString);
        _keepAlive.Open();
        Exec("CREATE TABLE users (id INTEGER PRIMARY KEY, name TEXT NOT NULL, email TEXT NOT NULL, status TEXT NOT NULL, balance REAL NOT NULL)");
        var seed = new StringBuilder("INSERT INTO users (id, name, email, status, balance) VALUES ");
        for (var i = 1; i <= 1000; i++)
            seed.Append($"({i},'User {i}','user{i}@example.com','active',{i * 3.5}),");
        seed.Length -= 1;
        Exec(seed.ToString());
        Exec("CREATE TABLE pairs (a INTEGER NOT NULL, b INTEGER NOT NULL, v TEXT NOT NULL, PRIMARY KEY (a, b))");
        var pairSeed = new StringBuilder("INSERT INTO pairs (a, b, v) VALUES ");
        for (var a = 1; a <= 10; a++)
            for (var b = 1; b <= 10; b++)
                pairSeed.Append($"({a},{b},'v{a}-{b}'),");
        pairSeed.Length -= 1;
        Exec(pairSeed.ToString());

        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
        _host = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services => services.AddBifrostEndpoints(o => o.AddEndpoint(e =>
            {
                e.ConnectionString = ConnString;
                e.Provider = "sqlite";
                e.Path = "/graphql";
                e.Metadata = Array.Empty<string>();
                e.DisableAuth = true;
            })));
            web.Configure(_ => { });
        }).Start();
        _graphql = _host.GetTestClient();

        var options = new RespWireOptions { RequireAuthentication = false, EnableWrites = true };
        var handlerServices = new ServiceCollection()
            .AddSingleton(_host.Services.GetRequiredService<IQueryIntentExecutor>())
            .AddSingleton(_host.Services.GetRequiredService<IMutationIntentExecutor>())
            .AddSingleton(options)
            .BuildServiceProvider();
        var handler = new RespConnectionHandler(
            new NullRespCredentialStore(), BifrostAuthContextFactory.Instance, handlerServices, options, new IRespCommandHandler[]
            {
                new RespGetCommandHandler(), new RespMGetCommandHandler(), new RespExistsCommandHandler(),
                new RespTypeCommandHandler(), new RespHGetAllCommandHandler(), new RespHGetCommandHandler(),
                new RespScanCommandHandler(), new RespSetCommandHandler(), new RespHSetCommandHandler(),
                new RespDelCommandHandler(),
            });

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient socket;
                try { socket = await _listener.AcceptTcpClientAsync(ct); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    try { await handler.HandleConnectionAsync(socket.GetStream(), ct); }
                    catch { /* contained */ }
                    finally { socket.Close(); }
                }, ct);
            }
        }, ct);

        _resp = RespClient.Connect(((IPEndPoint)_listener.LocalEndpoint).Port);
        // Warm both paths once so lazy model load is out of the measurement.
        _ = _resp.Command("GET", "users:1");
        _ = Gql("{ users(filter: { id: { _eq: 1 } }) { data { id name } } }");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _resp.Dispose();
        _cts.Cancel();
        _listener.Stop();
        _host.Dispose();
        _keepAlive.Dispose();
    }

    private void Exec(string sql)
    {
        using var cmd = new SqliteCommand(sql, _keepAlive);
        cmd.ExecuteNonQuery();
    }

    private string Gql(string query)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new { query });
        using var response = _graphql.PostAsync("/graphql",
            new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (text.Contains("\"errors\"")) throw new InvalidOperationException(text);
        return text;
    }

    private int NextId() => (_cursor = (_cursor + 1) % 1000) + 1;

    // ---- single-row read -----------------------------------------------------

    [Benchmark(OperationsPerInvoke = Ops, Description = "RESP GET users:<id>")]
    public int Resp_Get()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++) n += _resp.Command("GET", $"users:{NextId()}").Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = Ops, Description = "GraphQL single-row by pk")]
    public int Gql_Get()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
            n += Gql($"{{ users(filter: {{ id: {{ _eq: {NextId()} }} }}) {{ data {{ id name email status balance }} }} }}").Length;
        return n;
    }

    // ---- batched read ----------------------------------------------------------

    [Benchmark(OperationsPerInvoke = Ops, Description = "RESP MGET 20 keys (single pk)")]
    public int Resp_Mget20()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
        {
            var args = new string[21];
            args[0] = "MGET";
            for (var k = 0; k < 20; k++) args[k + 1] = $"users:{NextId()}";
            n += _resp.Command(args).Length;
        }
        return n;
    }

    [Benchmark(OperationsPerInvoke = Ops, Description = "GraphQL _in 20 ids")]
    public int Gql_In20()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
        {
            var ids = string.Join(",", Enumerable.Range(0, 20).Select(_ => NextId()));
            n += Gql($"{{ users(filter: {{ id: {{ _in: [{ids}] }} }}) {{ data {{ id name email status balance }} }} }}").Length;
        }
        return n;
    }

    [Benchmark(OperationsPerInvoke = Ops, Description = "RESP MGET 10 keys (composite pk)")]
    public int Resp_MgetComposite10()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
        {
            var args = new string[11];
            args[0] = "MGET";
            for (var k = 0; k < 10; k++) args[k + 1] = $"pairs:{k % 10 + 1}:{(i + k) % 10 + 1}";
            n += _resp.Command(args).Length;
        }
        return n;
    }

    // ---- writes ---------------------------------------------------------------

    [Benchmark(OperationsPerInvoke = Ops, Description = "RESP SET single-column update")]
    public int Resp_Set()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
            n += _resp.Command("SET", $"users:{NextId()}", $"{{\"status\":\"active-{i}\"}}").Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = Ops, Description = "GraphQL update single column")]
    public int Gql_Update()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
            n += Gql($"mutation {{ users(update: {{ id: {NextId()}, status: \"active-{i}\" }}) }}").Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = Ops, Description = "RESP HSET two fields")]
    public int Resp_Hset()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
            n += _resp.Command("HSET", $"users:{NextId()}", "status", $"s{i}", "name", $"User {i}").Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = Ops, Description = "GraphQL update two columns")]
    public int Gql_UpdateTwo()
    {
        var n = 0;
        for (var i = 0; i < Ops; i++)
            n += Gql($"mutation {{ users(update: {{ id: {NextId()}, status: \"s{i}\", name: \"User {i}\" }}) }}").Length;
        return n;
    }

    /// <summary>No-credential store for the RequireAuthentication=false bench posture.</summary>
    private sealed class NullRespCredentialStore : IRespCredentialStore
    {
        public Task<RespLogin?> FindAsync(string username, CancellationToken cancellationToken) =>
            Task.FromResult<RespLogin?>(null);
    }

    /// <summary>
    /// Minimal blocking RESP client — array-of-bulk-strings out, one reply in.
    /// Deliberately dependency-free so the measurement is the SERVER, not a
    /// client library's pipelining.
    /// </summary>
    private sealed class RespClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly NetworkStream _stream;
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _len;
        private int _pos;

        private RespClient(TcpClient tcp) { _tcp = tcp; _stream = tcp.GetStream(); }

        public static RespClient Connect(int port)
        {
            var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            tcp.NoDelay = true;
            return new RespClient(tcp);
        }

        public string Command(params string[] args)
        {
            var request = new StringBuilder().Append('*').Append(args.Length).Append("\r\n");
            foreach (var arg in args)
                request.Append('$').Append(Encoding.UTF8.GetByteCount(arg)).Append("\r\n").Append(arg).Append("\r\n");
            var bytes = Encoding.UTF8.GetBytes(request.ToString());
            _stream.Write(bytes, 0, bytes.Length);
            return ReadReply();
        }

        private string ReadReply()
        {
            var type = ReadByte();
            var line = ReadLine();
            switch (type)
            {
                case '+': case ':': return line;
                case '-': throw new InvalidOperationException($"RESP error: {line}");
                case '$':
                {
                    var length = int.Parse(line);
                    if (length < 0) return "";
                    var payload = ReadExact(length);
                    ReadLine();
                    return payload;
                }
                case '*':
                {
                    var count = int.Parse(line);
                    var total = new StringBuilder();
                    for (var i = 0; i < count; i++) total.Append(ReadReply());
                    return total.ToString();
                }
                default: throw new InvalidOperationException($"Unexpected RESP type byte '{(char)type}'.");
            }
        }

        private int ReadByte()
        {
            if (_pos >= _len) Fill();
            return _buffer[_pos++];
        }

        private void Fill()
        {
            _len = _stream.Read(_buffer, 0, _buffer.Length);
            _pos = 0;
            if (_len <= 0) throw new EndOfStreamException();
        }

        private string ReadLine()
        {
            var sb = new StringBuilder();
            for (;;)
            {
                var b = ReadByte();
                if (b == '\r') { ReadByte(); return sb.ToString(); }
                sb.Append((char)b);
            }
        }

        private string ReadExact(int length)
        {
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++) sb.Append((char)ReadByte());
            return sb.ToString();
        }

        public void Dispose() { _stream.Dispose(); _tcp.Dispose(); }
    }
}
