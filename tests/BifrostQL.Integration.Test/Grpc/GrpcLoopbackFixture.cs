using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Model;
using BifrostQL.Server;
using BifrostQL.Server.Grpc;
using BifrostQL.Sqlite;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace BifrostQL.Integration.Test.Grpc
{
    /// <summary>
    /// A black-box loopback fixture for the assembled gRPC front door: a seeded in-memory SQLite
    /// database behind the full BifrostQL endpoint stack plus the opt-in gRPC adapter, hosted in-process
    /// on a <see cref="TestServer"/> and driven by a real <see cref="GrpcChannel"/> over the HTTP/2 test
    /// handler. Unlike the server-project unit tests, this fixture lives OUTSIDE the server assembly and
    /// uses only the public adapter API (<c>AddBifrostGrpc</c>/<c>MapBifrostGrpc</c>) plus the public
    /// schema-generation types to build its client-side contract — so it proves an external caller,
    /// holding only what reflection yields, can drive the surface with no compiled stubs. No production
    /// network dependency: everything is in-proc SQLite + the in-memory HTTP/2 handler.
    /// </summary>
    internal sealed class GrpcLoopbackFixture : IAsyncDisposable
    {
        public const string EndpointPath = "/graphql";
        public const string IdentityHeader = "x-bifrost-test-identity";

        private readonly string _connString;
        private SqliteConnection _keepAlive = null!;
        private IHost _host = null!;
        private GrpcChannel _channel = null!;

        public GrpcContract Contract { get; private set; } = null!;

        private GrpcLoopbackFixture(string connName)
            => _connString = $"Data Source=grpc_loopback_{connName};Mode=Memory;Cache=Shared";

        public static async Task<GrpcLoopbackFixture> StartAsync(
            string connName, string[] metadataRules, string[] seedSql, bool enableWrites = false)
        {
            var fixture = new GrpcLoopbackFixture(connName);
            await fixture.InitializeAsync(metadataRules, seedSql, enableWrites);
            return fixture;
        }

        private async Task InitializeAsync(string[] metadataRules, string[] seedSql, bool enableWrites)
        {
            _keepAlive = new SqliteConnection(_connString);
            await _keepAlive.OpenAsync();
            foreach (var sql in seedSql)
            {
                await using var cmd = new SqliteCommand(sql, _keepAlive);
                await cmd.ExecuteNonQueryAsync();
            }

            DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
            var builder = new HostBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddBifrostEndpoints(o =>
                    {
                        o.AddEndpoint(e =>
                        {
                            e.ConnectionString = _connString;
                            e.Provider = "sqlite";
                            e.Path = EndpointPath;
                            e.Metadata = metadataRules;
                            e.DisableAuth = true;
                        });
                    });
                    // Test identity projection: a user|tenant|roles header → the same user-context keys
                    // the pipeline reads. No header → EMPTY context, the real fail-closed path.
                    services.AddSingleton<IBifrostAuthContextFactory, HeaderIdentityFactory>();
                    services.AddBifrostGrpc(o =>
                    {
                        o.Endpoint = EndpointPath;
                        o.EnableWrites = enableWrites;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapBifrostGrpc());
                });
            });
            _host = await builder.StartAsync();

            var handler = _host.GetTestServer().CreateHandler();
            _channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler });

            var executor = _host.Services.GetRequiredService<IQueryIntentExecutor>();
            var model = await executor.GetModelAsync(EndpointPath);
            var visible = GrpcSchemaVisibility.ProjectAll(model);
            var manifest = GrpcFieldNumberManifest.Empty().Reconcile(visible);
            Contract = GrpcSchemaGenerator.BuildContract(visible, manifest, enableWrites);
        }

        public GrpcChannel Channel => _channel;
        public CallInvoker Invoker => _channel.CreateCallInvoker();

        public static Metadata Identity(string? user, string? tenant = null, string? roles = null)
        {
            var metadata = new Metadata();
            if (user is not null)
                metadata.Add(IdentityHeader, $"{user}|{tenant ?? string.Empty}|{roles ?? string.Empty}");
            return metadata;
        }

        public async ValueTask DisposeAsync()
        {
            _channel?.Dispose();
            if (_host is not null)
            {
                await _host.StopAsync();
                _host.Dispose();
            }
            if (_keepAlive is not null)
                await _keepAlive.DisposeAsync();
        }
    }

    /// <summary>Projects a <c>user|tenant|roles</c> gRPC identity header into the pipeline's user-context keys.</summary>
    internal sealed class HeaderIdentityFactory : IBifrostAuthContextFactory
    {
        public IDictionary<string, object?> CreateUserContext(HttpContext context)
        {
            var raw = context.Request.Headers[GrpcLoopbackFixture.IdentityHeader].ToString();
            if (string.IsNullOrEmpty(raw))
                return new Dictionary<string, object?>(); // fail-closed: no identity → empty

            var parts = raw.Split('|');
            var subject = parts[0];
            if (string.IsNullOrEmpty(subject))
                throw new InvalidOperationException("Authenticated principal has no subject claim.");

            var ctx = new Dictionary<string, object?> { ["user_id"] = subject };
            if (parts.Length > 1 && parts[1].Length > 0)
                ctx["tenant_id"] = parts[1];
            if (parts.Length > 2 && parts[2].Length > 0)
                ctx["roles"] = parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries);
            return ctx;
        }

        public IDictionary<string, object?> CreateUserContext(HttpContext context, IDictionary<string, object?> existing)
            => CreateUserContext(context);
    }

    /// <summary>
    /// A minimal external gRPC client that encodes Get/List/Stream requests and decodes responses
    /// straight from the public <see cref="GrpcContract"/> — no compiled stubs. The client-side inverse
    /// of the server's message codec, proving the wire round-trips for a caller outside the server
    /// assembly.
    /// </summary>
    internal sealed class GrpcWireClient
    {
        private const uint WireLengthDelimited = 2;
        private const string ServiceName = "bifrostql.BifrostQuery";
        private static readonly Marshaller<byte[]> Bytes = Marshallers.Create(v => v, b => b);

        private readonly CallInvoker _invoker;
        private readonly GrpcContract _contract;

        public GrpcWireClient(CallInvoker invoker, GrpcContract contract)
        {
            _invoker = invoker;
            _contract = contract;
        }

        private GrpcMessage Message(string name) => _contract.Messages.Single(m => m.Name == name);

        public async Task<IReadOnlyDictionary<string, object?>?> GetAsync(
            string table, IReadOnlyDictionary<string, object?> key, Metadata? headers = null)
        {
            var request = Encode(Message($"Get{table}Request"), key);
            var method = new Method<byte[], byte[]>(MethodType.Unary, ServiceName, $"Get{table}", Bytes, Bytes);
            try
            {
                var response = await _invoker.AsyncUnaryCall(method, null, new CallOptions(headers), request);
                var nested = ReadNested(response, 1);
                return nested.Count == 0 ? null : DecodeRow(Message($"{table}Row"), nested[0]);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
            {
                return null; // missing == out-of-scope, no oracle
            }
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
            string table, Metadata? headers = null, string? filter = null, string? orderBy = null, int? pageSize = null)
        {
            var request = EncodeRead($"List{table}Request", filter, orderBy, pageSize);
            var method = new Method<byte[], byte[]>(MethodType.Unary, ServiceName, $"List{table}", Bytes, Bytes);
            var response = await _invoker.AsyncUnaryCall(method, null, new CallOptions(headers), request);
            var rowMessage = Message($"{table}Row");
            return ReadNested(response, 1).Select(b => DecodeRow(rowMessage, b)).ToList();
        }

        public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> StreamAsync(
            string table, Metadata? headers = null, CancellationToken cancellationToken = default,
            string? filter = null, string? orderBy = null, int? pageSize = null)
        {
            var request = EncodeRead($"Stream{table}Request", filter, orderBy, pageSize);
            var method = new Method<byte[], byte[]>(MethodType.ServerStreaming, ServiceName, $"Stream{table}", Bytes, Bytes);
            using var call = _invoker.AsyncServerStreamingCall(method, null, new CallOptions(headers, cancellationToken: cancellationToken), request);
            var rowMessage = Message($"{table}Row");
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var msg in call.ResponseStream.ReadAllAsync(cancellationToken))
                rows.Add(DecodeRow(rowMessage, msg));
            return rows;
        }

        private byte[] EncodeRead(string messageName, string? filter, string? orderBy, int? pageSize)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (filter is not null) values["filter"] = filter;
            if (orderBy is not null) values["order_by"] = orderBy;
            if (pageSize is not null) values["page_size"] = pageSize;
            return Encode(Message(messageName), values);
        }

        private static byte[] Encode(GrpcMessage message, IReadOnlyDictionary<string, object?> values)
        {
            using var buffer = new MemoryStream();
            using (var output = new CodedOutputStream(buffer, leaveOpen: true))
            {
                foreach (var field in message.Fields)
                {
                    if (!values.TryGetValue(field.Name, out var value) || value is null)
                        continue;
                    if (field.Scalar == GrpcScalarKind.Timestamp)
                    {
                        WriteTag(output, field.Number, WireLengthDelimited);
                        output.WriteMessage(Timestamp.FromDateTime(DateTime.SpecifyKind((DateTime)value, DateTimeKind.Utc)));
                        continue;
                    }
                    WriteScalar(output, field.Number, field.Scalar!.Value, value);
                }
            }
            return buffer.ToArray();
        }

        private static void WriteScalar(CodedOutputStream output, int number, GrpcScalarKind kind, object value)
        {
            switch (kind)
            {
                case GrpcScalarKind.Int32: WriteTag(output, number, 0); output.WriteInt32(Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Int64: WriteTag(output, number, 0); output.WriteInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Bool: WriteTag(output, number, 0); output.WriteBool(Convert.ToBoolean(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Double: WriteTag(output, number, 1); output.WriteDouble(Convert.ToDouble(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Float: WriteTag(output, number, 5); output.WriteFloat(Convert.ToSingle(value, CultureInfo.InvariantCulture)); break;
                case GrpcScalarKind.Bytes: WriteTag(output, number, WireLengthDelimited); output.WriteBytes(ByteString.CopyFrom((byte[])value)); break;
                default: WriteTag(output, number, WireLengthDelimited); output.WriteString(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty); break;
            }
        }

        public static IReadOnlyDictionary<string, object?> DecodeRow(GrpcMessage rowMessage, byte[] bytes)
        {
            var byNumber = rowMessage.Fields.ToDictionary(f => f.Number);
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var input = new CodedInputStream(bytes);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                var number = (int)(tag >> 3);
                if (!byNumber.TryGetValue(number, out var field)) { input.SkipLastField(); continue; }
                values[field.Name] = ReadScalar(input, field.Scalar!.Value);
            }
            return values;
        }

        private static List<byte[]> ReadNested(byte[] bytes, int fieldNumber)
        {
            var result = new List<byte[]>();
            var input = new CodedInputStream(bytes);
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if ((int)(tag >> 3) == fieldNumber && (tag & 7) == WireLengthDelimited)
                    result.Add(input.ReadBytes().ToByteArray());
                else
                    input.SkipLastField();
            }
            return result;
        }

        private static object? ReadScalar(CodedInputStream input, GrpcScalarKind kind) => kind switch
        {
            GrpcScalarKind.Int32 => input.ReadInt32(),
            GrpcScalarKind.Int64 => input.ReadInt64(),
            GrpcScalarKind.Bool => input.ReadBool(),
            GrpcScalarKind.Double => input.ReadDouble(),
            GrpcScalarKind.Float => input.ReadFloat(),
            GrpcScalarKind.Bytes => input.ReadBytes().ToByteArray(),
            GrpcScalarKind.Timestamp => ReadTimestamp(input),
            _ => input.ReadString(),
        };

        private static DateTime ReadTimestamp(CodedInputStream input)
        {
            var ts = new Timestamp();
            input.ReadMessage(ts);
            return ts.ToDateTime();
        }

        private static void WriteTag(CodedOutputStream output, int fieldNumber, uint wireType)
        {
            var tag = (uint)(fieldNumber << 3) | wireType;
            Span<byte> raw = stackalloc byte[5];
            var length = 0;
            while (tag >= 0x80) { raw[length++] = (byte)(tag | 0x80); tag >>= 7; }
            raw[length++] = (byte)tag;
            switch (length)
            {
                case 1: output.WriteRawTag(raw[0]); break;
                case 2: output.WriteRawTag(raw[0], raw[1]); break;
                case 3: output.WriteRawTag(raw[0], raw[1], raw[2]); break;
                case 4: output.WriteRawTag(raw[0], raw[1], raw[2], raw[3]); break;
                default: output.WriteRawTag(raw[0], raw[1], raw[2], raw[3], raw[4]); break;
            }
        }
    }
}
