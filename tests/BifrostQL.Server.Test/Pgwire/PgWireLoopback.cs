using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using BifrostQL.Core.Resolvers;
using BifrostQL.Server.Auth;
using BifrostQL.Server.Pgwire;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Drives a single SQL statement through a real, loopback pgwire front door — SSL-less
    /// cleartext handshake to ReadyForQuery, then a simple Query ('Q') — against a supplied
    /// <see cref="IQueryIntentExecutor"/>. This is the shared wire runner for slice-6
    /// security-conformance and integration tests: the query travels the ACTUAL pgwire
    /// protocol path (its handler, real <see cref="PgSubsetQueryTranslator"/>, real
    /// <see cref="PgCatalogResponder"/>, real encoders), and the executor it reaches is the
    /// real transformer-pipeline executor — so tenant isolation, soft-delete, policy guards
    /// and parameterization are exercised on the wire exactly as production would.
    ///
    /// <para>The authenticated identity is delivered the only way the wire can: a credential
    /// whose login maps to <paramref name="principal"/>, projected through
    /// <see cref="BifrostAuthContextFactory"/> by the handler — never injected past it.</para>
    /// </summary>
    internal static class PgWireLoopback
    {
        private const string LoginUser = "u";
        private const string LoginSecret = "pw";

        /// <summary>
        /// Runs <paramref name="sql"/> as <paramref name="principal"/> and returns the fully
        /// decoded simple-query response cycle (RowDescription/DataRow*/CommandComplete or
        /// ErrorResponse, through ReadyForQuery). Does not throw on a server ErrorResponse —
        /// callers inspect <see cref="SimpleQueryResult.HasError"/>.
        /// </summary>
        public static async Task<SimpleQueryResult> RunAsync(
            IQueryIntentExecutor executor,
            ClaimsPrincipal principal,
            string sql,
            string? endpoint,
            TimeSpan timeout)
        {
            var services = new ServiceCollection()
                .AddSingleton(executor)
                .AddSingleton<IPgQueryTranslator, PgSubsetQueryTranslator>()
                .AddSingleton<IPgCatalogResponder, PgCatalogResponder>()
                .BuildServiceProvider();

            var store = new FakePgCredentialStore().Add(LoginUser, LoginSecret, principal);
            var options = new PgWireOptions { AuthMethod = PgAuthMethod.Cleartext, Endpoint = endpoint };

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var clientSocket = new TcpClient();
            var connectTask = clientSocket.ConnectAsync(IPAddress.Loopback, port);
            var serverSocket = await listener.AcceptTcpClientAsync();
            await connectTask;

            var handler = new PgConnectionHandler(store, BifrostAuthContextFactory.Instance, services, options);
            var serverTask = handler.HandleConnectionAsync(serverSocket.GetStream(), CancellationToken.None);

            try
            {
                var client = new PgHandshakeClient(clientSocket.GetStream());
                await client.SendStartupAsync(LoginUser);
                await client.DoCleartextAsync(LoginSecret);
                var handshake = await client.WaitForReadyOrErrorAsync().WaitAsync(timeout);
                handshake.ReadyForQuery.Should().BeTrue("the handshake must reach a ready session before queries run");

                await client.SendQueryAsync(sql);
                return await client.ReadQueryResultAsync().WaitAsync(timeout);
            }
            finally
            {
                clientSocket.Dispose();
                try { await serverTask.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch { /* teardown races on dispose are expected */ }
                serverSocket.Dispose();
                listener.Stop();
            }
        }

        /// <summary>
        /// Builds the SQL text for a <see cref="BifrostQL.AdapterConformance.ConformanceReadRequest"/>
        /// shape (columns + optional GraphQL-shape filter) so the conformance kit's read
        /// request travels the pgwire wire as a real SELECT. Only the <c>_eq</c> operator the
        /// kit exercises is supported; anything else fails fast rather than silently dropping
        /// a predicate.
        /// </summary>
        public static string BuildSelect(string table, IReadOnlyList<string> columns, IReadOnlyDictionary<string, object?>? filter)
        {
            var sql = $"SELECT {string.Join(", ", columns)} FROM {table}";
            if (filter is { Count: > 0 })
                sql += " WHERE " + string.Join(" AND ", filter.Select(kv => BuildPredicate(kv.Key, kv.Value)));
            return sql;
        }

        private static string BuildPredicate(string column, object? operatorDict)
        {
            if (operatorDict is not IReadOnlyDictionary<string, object?> ops || ops.Count != 1)
                throw new NotSupportedException(
                    $"pgwire conformance filter for '{column}' must be a single-operator object like {{ _eq: value }}.");
            var (op, value) = ops.Single();
            var sqlOp = op switch
            {
                "_eq" => "=",
                _ => throw new NotSupportedException($"pgwire conformance filter supports only _eq, got '{op}'."),
            };
            return $"{column} {sqlOp} {Literal(value)}";
        }

        private static string Literal(object? value) => value switch
        {
            null => "NULL",
            string s => "'" + s.Replace("'", "''") + "'",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
        };

        /// <summary>
        /// Decodes a simple-query result into the row shape the conformance kit and the
        /// integration tests consume: field name → text value (SQL NULL → null).
        /// </summary>
        public static IReadOnlyList<IReadOnlyDictionary<string, object?>> Decode(SimpleQueryResult result)
        {
            var names = result.Fields.Select(f => f.Name).ToList();
            return result.Rows
                .Select(row =>
                {
                    var record = new Dictionary<string, object?>(names.Count);
                    for (var i = 0; i < names.Count; i++)
                        record[names[i]] = i < row.Count ? row[i] : null;
                    return (IReadOnlyDictionary<string, object?>)record;
                })
                .ToList();
        }
    }

    /// <summary>
    /// The exception a pgwire read raises when the wire answers with an ErrorResponse. It
    /// carries the SQLSTATE and the client-facing wire message verbatim — the sanitized
    /// message for an execution fault, the curated message for a translation fault — so the
    /// conformance kit can assert the adapter surfaced the rejection rather than swallowing
    /// it or returning rows.
    /// </summary>
    internal sealed class PgWireQueryException : Exception
    {
        public PgWireQueryException(string sqlState, string message)
            : base($"pgwire query rejected [{sqlState}]: {message}")
        {
            SqlState = sqlState;
        }

        public string SqlState { get; }
    }
}
