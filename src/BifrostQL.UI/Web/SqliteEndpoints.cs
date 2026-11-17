using System.Data.Common;
using System.Net;
using BifrostQL.Core.Model;
using BifrostQL.Core.Utils;
using Microsoft.Data.Sqlite;

namespace BifrostQL.UI.Web
{
    /// <summary>
    /// SQLite file connections: <c>POST /api/sqlite/test</c> checks a file and
    /// <c>POST /api/sqlite/connect</c> opens it and binds it as the active
    /// connection. A SQLite connection string is a file path with no credential,
    /// so (like the quickstart self-bind) it may cross HTTP; every other provider
    /// still activates only through the vault.
    ///
    /// The connection string is built here, never on the client: the path is
    /// quoted by <see cref="SqliteConnectionStringBuilder"/>, and the file is
    /// opened <c>Mode=ReadWrite</c> unless the caller asked to create it, so a
    /// mistyped path is an error instead of a new empty database.
    ///
    /// Both routes answer loopback callers only. Under <c>--expose</c> a LAN peer
    /// could otherwise open, and through GraphQL read, any SQLite file the host
    /// process can reach.
    /// </summary>
    public static class SqliteEndpoints
    {
        public static void MapSqliteEndpoints(this WebApplication app, ConnectionState state)
        {
            app.MapPost("/api/sqlite/test", async (SqliteConnectRequest request, HttpContext http, CancellationToken ct) =>
            {
                if (!IsLoopback(http)) return LoopbackOnly();
                string connectionString;
                try { connectionString = BuildConnectionString(request); }
                catch (ArgumentException ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }

                var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
                if (request.CreateNew && !File.Exists(dataSource))
                    return Results.Ok(new { success = true, message = $"A new database will be created at {dataSource}" });

                try
                {
                    await using var conn = new SqliteConnection(connectionString);
                    await conn.OpenAsync(ct);
                    return Results.Ok(new { success = true, message = $"Opened {dataSource}" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = SecretScrubber.Scrub(ex.Message) ?? "" });
                }
            });

            app.MapPost("/api/sqlite/connect", async (SqliteConnectRequest request, HttpContext http, CancellationToken ct) =>
            {
                if (!IsLoopback(http)) return LoopbackOnly();
                string connectionString;
                try { connectionString = BuildConnectionString(request); }
                catch (ArgumentException ex) { return Results.BadRequest(new { success = false, error = ex.Message }); }

                try
                {
                    // Open first: a missing file (ReadWrite) fails here, before the
                    // current binding is replaced.
                    await using (var conn = new SqliteConnection(connectionString))
                        await conn.OpenAsync(ct);
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { success = false, error = SecretScrubber.Scrub(ex.Message) ?? "" });
                }

                await state.ActivateAsync(app.Services,
                    new ActiveConnection(connectionString, BifrostDbProvider.Sqlite), profileSchema: null, ct);
                var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
                return Results.Ok(new { success = true, database = Path.GetFileName(dataSource), connectionString });
            });
        }

        /// <summary>
        /// The one SQLite connection-string builder. Exactly one of a file path
        /// (form mode) or a raw connection string (raw mode, recent connections)
        /// is accepted. The raw string must parse as SQLite keywords; when it does
        /// not name a <c>Mode</c>, the same ReadWrite-unless-create rule applies.
        /// </summary>
        internal static string BuildConnectionString(SqliteConnectRequest request)
        {
            var hasPath = !string.IsNullOrWhiteSpace(request.FilePath);
            var hasRaw = !string.IsNullOrWhiteSpace(request.ConnectionString);
            if (hasPath == hasRaw)
                throw new ArgumentException("Send either a filePath or a connectionString.");

            var mode = request.CreateNew ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite;
            if (hasPath)
                return new SqliteConnectionStringBuilder { DataSource = request.FilePath, Mode = mode }.ToString();

            SqliteConnectionStringBuilder builder;
            try { builder = new SqliteConnectionStringBuilder(request.ConnectionString); }
            catch (ArgumentException) { throw new ArgumentException("The connection string is not a SQLite connection string."); }
            if (string.IsNullOrWhiteSpace(builder.DataSource))
                throw new ArgumentException("The connection string has no Data Source.");
            // The typed builder reports a default for every keyword; the generic one
            // holds only the keys actually written.
            if (!new DbConnectionStringBuilder { ConnectionString = request.ConnectionString }.ContainsKey("Mode"))
                builder.Mode = mode;
            return builder.ToString();
        }

        private static bool IsLoopback(HttpContext http) =>
            http.Connection.RemoteIpAddress is { } remote && IPAddress.IsLoopback(remote);

        private static IResult LoopbackOnly() =>
            Results.Json(new { success = false, error = "SQLite files can only be opened from this machine." }, statusCode: StatusCodes.Status403Forbidden);
    }
}
