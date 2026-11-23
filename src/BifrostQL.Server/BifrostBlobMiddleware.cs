using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>Options for the direct binary-link endpoint. Caps are part of the
    /// contract: an unbounded response is a defect, not a default.</summary>
    public sealed class BifrostBlobOptions
    {
        /// <summary>Base path. Default <c>/_blob</c>.</summary>
        public string Path { get; set; } = "/_blob";

        /// <summary>
        /// Overrides whether a caller must present an identity that projects to a non-empty
        /// Bifrost user context. Null (the default) inherits the requirement of the GraphQL
        /// endpoint blobs are read from (<see cref="Endpoint"/>) through <c>MountAuthRequirement</c>:
        /// the blob URL serves the same rows the endpoint does, so it carries the same gate, and it
        /// fails closed when the endpoint cannot be identified.
        /// </summary>
        public bool? RequireAuth { get; set; }

        /// <summary>
        /// The registered GraphQL endpoint whose cached model/connection blobs are
        /// read from. Null selects the single registered endpoint. With several registered
        /// and none named, or a path no endpoint is registered under, <c>UseBifrostBlobs</c>
        /// refuses to mount rather than guess.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// Upper bound on a served blob, in decoded bytes. Over the cap is an explicit 413,
        /// never a truncated body; HTTP Range only windows the wire. Default 32 MiB.
        ///
        /// <para>The stored size is measured on the database side first (BlobByteLengthProbe), so
        /// an oversized value is refused without being read into memory. A served value costs
        /// its base64 string plus one decoded array. A row rewritten between the probe and the
        /// read is caught by a second check on the decoded size before decoding.</para>
        /// </summary>
        public int MaxBlobBytes { get; set; } = 32 * 1024 * 1024;
    }

    /// <summary>
    /// Direct binary links for blob columns: <c>GET /_blob/{table}/{column}?k.&lt;pk&gt;=…</c>
    /// streams the column's bytes, so images and documents stored in the database are
    /// addressable as plain URLs alongside the base64 the GraphQL surface returns —
    /// both encodings share ONE read path and ONE security model.
    ///
    /// <para><b>Security model = the GraphQL surface's, by construction.</b> Identity
    /// projects through the shared <see cref="BifrostIdentityGate"/>; the read runs a
    /// <see cref="QueryIntent"/> through <see cref="IQueryIntentExecutor"/>, so tenant
    /// isolation, soft-delete, policy row scope and column read guards apply
    /// unskippably. Every not-found-shaped condition — absent row, NULL value,
    /// unknown or policy-hidden table/column, non-blob column, missing or malformed
    /// key parameter, pipeline denial — maps to the SAME constant 404 (invariant 10 /
    /// the S3 precedent): a denial is indistinguishable from absence, and no message
    /// ever carries model, driver, or transformer text (invariant 3).</para>
    ///
    /// <para><b>Windows and chunking.</b> The endpoint advertises
    /// <c>Accept-Ranges: bytes</c> and honors single-range requests with 206 partial
    /// content, so large files download in resumable windows — something the base64
    /// GraphQL encoding cannot offer. Inline rendering is restricted to a fixed
    /// magic-byte image allowlist (PNG/JPEG/GIF/WebP) served with
    /// <c>X-Content-Type-Options: nosniff</c>; everything else — PDFs included — is
    /// <c>application/octet-stream</c> with an attachment disposition, so a stored
    /// HTML/SVG payload can never execute on this origin.</para>
    /// </summary>
    public sealed class BifrostBlobMiddleware
    {
        private const string NotFoundBody = "Not found.";
        private static readonly HashSet<string> BinaryDbTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "binary", "varbinary", "image", "blob", "tinyblob", "mediumblob", "longblob", "bytea",
        };

        private readonly RequestDelegate _next;
        private readonly BifrostBlobOptions _options;
        private readonly ILogger<BifrostBlobMiddleware>? _logger;
        private readonly bool _requiresAuth;

        /// <param name="next">The next middleware.</param>
        /// <param name="options">Endpoint options.</param>
        /// <param name="requiresAuth">
        /// Whether a caller must carry a projected identity. <c>UseBifrostBlobs</c> derives it from
        /// <c>MountAuthRequirement</c> (or the explicit <see cref="BifrostBlobOptions.RequireAuth"/>
        /// override); it is required here so no construction path can default it.
        /// </param>
        /// <param name="logger">Server-side diagnostics.</param>
        public BifrostBlobMiddleware(
            RequestDelegate next, BifrostBlobOptions options, bool requiresAuth, ILogger<BifrostBlobMiddleware>? logger = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _requiresAuth = requiresAuth;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!context.Request.Path.StartsWithSegments(_options.Path, StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                await _next(context);
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                context.Response.Headers.Allow = "GET, HEAD";
                return;
            }

            // Identity through the SHARED gate — the same seam every other transport
            // gate uses; an unprojectable credential is refused even without RequireAuth.
            var (outcome, userContext) = await BifrostIdentityGate.ProjectAsync(context);
            if (BifrostIdentityGate.Refuses(outcome, _requiresAuth))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var segments = remaining.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            if (segments.Length != 2)
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }

            var executor = context.RequestServices.GetService<IQueryIntentExecutor>();
            if (executor is null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            IDbModel model;
            try
            {
                model = await executor.GetModelAsync(_options.Endpoint);
            }
            catch (Exception ex)
            {
                // The endpoint itself was checked at mount (UseBifrostBlobs), so what is left is
                // its model failing to load (database unreachable): a server condition, not a
                // data one — map it distinctly. The sink logs it at Error with the driver exception
                // attached; the detail is constant so the log never echoes the connection string.
                var error = BifrostErrorSink.ServerFault("The blob endpoint is temporarily unavailable.",
                    "The served model could not be loaded.",
                    $"{nameof(BifrostBlobMiddleware)}.{nameof(InvokeAsync)}", ex);
                await WriteTextAsync(context, StatusCodes.Status503ServiceUnavailable, error.Message);
                return;
            }

            try
            {
                await ServeAsync(context, executor, model, userContext, segments[0], segments[1]);
            }
            catch (BifrostExecutionError ex) when (ex.ErrorCode is BifrostExecutionError.AccessDeniedCode or BifrostExecutionError.NotFoundCode)
            {
                // Pipeline rejection (tenant scope, policy deny, …): the SAME 404 as a
                // row that does not exist — a denial must not be an existence oracle.
                // Matched by CONDITION CODE, never by type (invariant 10): SqlExecutionManager
                // wraps every driver fault as a BifrostExecutionError too, and a type match
                // read a database outage as "Not found." with nothing logged. Every other
                // code falls through to the logged 500 below.
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Blob read failed");
                await WriteTextAsync(context, StatusCodes.Status500InternalServerError, "Blob read failed.");
            }
        }

        private async Task ServeAsync(
            HttpContext context, IQueryIntentExecutor executor, IDbModel model,
            IDictionary<string, object?> userContext, string tableName, string columnName)
        {
            // GraphQL names are unique by construction, so this lookup cannot be
            // ambiguous; a DbName is accepted only when it matches exactly one table.
            var tables = model.Tables.Where(t =>
                string.Equals(t.GraphQlName, tableName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(t.DbName, tableName, StringComparison.OrdinalIgnoreCase)).ToList();
            var table = tables.Count == 1 ? tables[0]
                : tables.FirstOrDefault(t => string.Equals(t.GraphQlName, tableName, StringComparison.OrdinalIgnoreCase));

            // Resolve through the SAME read-visibility projection the introspection
            // surfaces use (invariant 4): a policy-denied table or column is null here
            // exactly as an unknown one is. This does not substitute for the pipeline
            // gate — the projection's null and the pipeline's denial produce the
            // IDENTICAL wire answer below and in the InvokeAsync catch, so gating the
            // shape checks on it only moves the denial earlier, never to a different
            // condition.
            var visible = table is null ? null : SchemaReadVisibility.ProjectTable(table, userContext);
            var column = visible?.Columns.FirstOrDefault(c =>
                string.Equals(c.GraphQlName, columnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.DbName, columnName, StringComparison.OrdinalIgnoreCase));

            // Unknown or read-denied table, unknown or unreadable column, a non-blob
            // column, and — below — a missing or malformed key all read as the SAME
            // 404 as an absent row, with no identifiers. These shape checks run BEFORE
            // the pipeline gate, so any answer that distinguishes them (a 400 naming
            // the key column, say) is an existence/enumeration oracle for a
            // read-denied caller (finding M12). This endpoint serves binary content;
            // it is not an introspection surface.
            if (visible is null || column is null || !BinaryDbTypes.Contains(BaseType(column.DataType)))
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }

            // EVERY primary-key column must be supplied (k.<graphQlName>=value) —
            // composite keys are addressed in full, never by a first-column guess. A
            // missing, duplicated or unparseable key is the same 404: naming the key
            // column or its type here would disclose the key shape before the
            // pipeline gate.
            var keyColumns = table!.KeyColumns.ToList();
            if (keyColumns.Count == 0)
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }
            var filter = new Dictionary<string, object?>();
            foreach (var key in keyColumns)
            {
                var raw = context.Request.Query[$"k.{key.GraphQlName}"];
                if (raw.Count != 1 || string.IsNullOrEmpty(raw[0]))
                {
                    await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                    return;
                }
                if (!TryConvertKey(raw[0]!, key.DataType, out var keyValue))
                {
                    await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                    return;
                }
                filter[key.GraphQlName] = new Dictionary<string, object?> { ["_eq"] = keyValue };
            }

            async Task<IReadOnlyDictionary<string, object?>?> ReadRowAsync(GqlObjectColumn selected)
            {
                var query = new GqlObjectQuery
                {
                    DbTable = table,
                    SchemaName = table.TableSchema,
                    TableName = table.DbName,
                    GraphQlName = table.GraphQlName,
                    Path = table.GraphQlName,
                    Filter = TableFilter.FromObject(filter, table),
                    Limit = 1,
                };
                query.ScalarColumns.Add(selected);
                var result = await executor.ExecuteAsync(new QueryIntent
                {
                    Query = query,
                    UserContext = userContext,
                    Endpoint = _options.Endpoint,
                }, context.RequestAborted);
                return result.Rows.Count == 1 ? result.Rows[0] : null;
            }

            // Measure first, on the database side: an oversized value is refused before a
            // single byte of it is read (BlobByteLengthProbe). NULL, masked and missing rows
            // are the same 404 the value read answers.
            var probe = await ReadRowAsync(BlobByteLengthProbe.Column(column));
            var storedSize = probe is null ? null : BlobByteLengthProbe.Read(probe);
            if (storedSize is null)
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }
            if (storedSize > _options.MaxBlobBytes)
            {
                await WriteTextAsync(context, StatusCodes.Status413PayloadTooLarge,
                    $"The value exceeds the {_options.MaxBlobBytes}-byte blob limit.");
                return;
            }

            var row = await ReadRowAsync(new GqlObjectColumn(column.DbName));
            object? value = null;
            if (row is not null && !row.TryGetValue(column.GraphQlName, out value))
                row.TryGetValue(column.DbName, out value);

            // The read seam materializes blobs as base64 (ReaderEnum.DbConvert);
            // byte[] is accepted for providers that hand bytes through untouched.
            // Backstop for a row rewritten between the probe and this read: the cap is
            // checked again on the decoded size BEFORE decoding.
            long? size = value switch
            {
                byte[] raw => raw.Length,
                string base64 => Base64DecodedLength(base64),
                _ => null,
            };
            if (size is null)
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }
            if (size > _options.MaxBlobBytes)
            {
                await WriteTextAsync(context, StatusCodes.Status413PayloadTooLarge,
                    $"The value exceeds the {_options.MaxBlobBytes}-byte blob limit.");
                return;
            }
            var bytes = value as byte[] ?? TryFromBase64((string)value!);
            if (bytes is null)
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }

            await WriteBlobAsync(context, bytes, table.GraphQlName, column.GraphQlName);
        }

        private static async Task WriteBlobAsync(HttpContext context, byte[] bytes, string tableName, string columnName)
        {
            var response = context.Response;
            var (mime, extension, inline) = Sniff(bytes);
            response.Headers["Accept-Ranges"] = "bytes";
            StoredBinaryContentHeaders.Apply(response, mime, $"{tableName}-{columnName}{extension}", inline);

            var (start, length, satisfiable, isPartial) = ResolveRange(context.Request.Headers.Range.ToString(), bytes.Length);
            if (!satisfiable)
            {
                response.StatusCode = StatusCodes.Status416RangeNotSatisfiable;
                response.Headers.ContentRange = $"bytes */{bytes.Length}";
                return;
            }
            if (isPartial)
            {
                response.StatusCode = StatusCodes.Status206PartialContent;
                response.Headers.ContentRange = $"bytes {start}-{start + length - 1}/{bytes.Length}";
            }
            response.ContentLength = length;

            if (HttpMethods.IsHead(context.Request.Method))
                return;
            await response.Body.WriteAsync(bytes.AsMemory((int)start, (int)length), context.RequestAborted);
        }

        /// <summary>
        /// Single-range parsing only (<c>bytes=a-b</c>, <c>bytes=a-</c>, <c>bytes=-n</c>), through
        /// the S3 adapter's <see cref="S3.S3RangeParser"/> so both binary surfaces share one Range
        /// grammar: every bound is digits only, parsed with <c>NumberStyles.None</c> and the
        /// invariant culture (invariant 5). A multi-range or malformed header is served as the
        /// FULL body (RFC 9110 permits ignoring Range); only a syntactically valid but
        /// unsatisfiable range (including a zero-length suffix) is a 416.
        /// </summary>
        internal static (long Start, long Length, bool Satisfiable, bool IsPartial) ResolveRange(string? header, long totalLength)
        {
            var range = S3.S3RangeParser.Parse(header, totalLength);
            return range.Kind switch
            {
                S3.S3RangeKind.Satisfiable => (range.Start, range.Length, true, true),
                S3.S3RangeKind.Unsatisfiable => (0, 0, false, false),
                _ => (0, totalLength, true, false),
            };
        }

        /// <summary>Magic-byte sniffing — never a column name or a stored mime string,
        /// so a mislabeled blob can only downgrade to an attachment, never render.</summary>
        internal static (string Mime, string Extension, bool Inline) Sniff(byte[] bytes)
        {
            static bool Ascii(byte[] b, int offset, string text)
            {
                if (b.Length < offset + text.Length) return false;
                for (var i = 0; i < text.Length; i++)
                    if (b[offset + i] != (byte)text[i]) return false;
                return true;
            }
            if (bytes.Length >= 4 && bytes[0] == 0x89 && Ascii(bytes, 1, "PNG")) return ("image/png", ".png", true);
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return ("image/jpeg", ".jpg", true);
            if (Ascii(bytes, 0, "GIF8")) return ("image/gif", ".gif", true);
            if (Ascii(bytes, 0, "RIFF") && Ascii(bytes, 8, "WEBP")) return ("image/webp", ".webp", true);
            if (Ascii(bytes, 0, "%PDF")) return ("application/pdf", ".pdf", false);
            return ("application/octet-stream", ".bin", false);
        }

        private static string BaseType(string dataType)
        {
            var open = dataType.IndexOf('(');
            return (open >= 0 ? dataType[..open] : dataType).Trim();
        }

        private static bool TryConvertKey(string raw, string dataType, out object? value)
        {
            value = null;
            switch (BaseType(dataType).ToLowerInvariant())
            {
                // Explicit style and invariant culture (invariant 5), the same grammar the pgwire,
                // RESP and LDAP adapters decode integer keys with.
                case "int" or "integer" or "bigint" or "smallint" or "tinyint":
                    if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) return false;
                    value = l; return true;
                case "decimal" or "numeric" or "money" or "smallmoney":
                    if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d)) return false;
                    value = d; return true;
                case "uniqueidentifier" or "uuid" or "guid":
                    if (!Guid.TryParse(raw, out var g)) return false;
                    value = g; return true;
                default:
                    value = raw; return true;
            }
        }

        /// <summary>
        /// The byte count <paramref name="base64"/> decodes to, from its length and padding
        /// alone, or null when the length cannot be canonical base64 (decoding would fail).
        /// </summary>
        internal static long? Base64DecodedLength(string base64)
        {
            if (base64.Length % 4 != 0)
                return null;
            var padding = base64.EndsWith("==", StringComparison.Ordinal) ? 2
                : base64.EndsWith('=') ? 1 : 0;
            return base64.Length / 4 * 3L - padding;
        }

        private static byte[]? TryFromBase64(string base64)
        {
            try { return Convert.FromBase64String(base64); }
            catch (FormatException) { return null; }
        }

        private static async Task WriteTextAsync(HttpContext context, int status, string body)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(body, context.RequestAborted);
        }
    }

    /// <summary>
    /// The shared safety headers for serving stored, caller-controlled bytes from
    /// this origin — used by the blob endpoint (<see cref="BifrostBlobMiddleware"/>)
    /// and the chat media route (<see cref="BifrostChatMiddleware"/>). The content
    /// type is always magic-byte-sniffed and pinned with <c>nosniff</c>; only
    /// verified-safe types may render inline, everything else is an attachment so
    /// stored markup can never execute on this origin.
    /// </summary>
    internal static class StoredBinaryContentHeaders
    {
        public static void Apply(HttpResponse response, string contentType, string fileName, bool inline)
        {
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.CacheControl = "private, no-store";
            response.ContentType = contentType;
            response.Headers.ContentDisposition =
                $"{(inline ? "inline" : "attachment")}; filename=\"{fileName}\"";
        }
    }

    public static class BifrostBlobExtensions
    {
        /// <summary>
        /// Mounts the direct binary-link endpoint (see <see cref="BifrostBlobMiddleware"/>). Mount it
        /// after <c>UseBifrostQL()</c> / <c>UseBifrostEndpoints()</c>: a mount ahead of the
        /// authentication middleware they add sees every caller as anonymous. The auth requirement
        /// is the served endpoint's (<c>MountAuthRequirement</c>) unless
        /// <see cref="BifrostBlobOptions.RequireAuth"/> overrides it. Refuses to mount when
        /// <see cref="BifrostBlobOptions.Endpoint"/> is null with several endpoints registered, or
        /// names no registered endpoint.
        /// </summary>
        public static IApplicationBuilder UseBifrostBlobs(this IApplicationBuilder app, Action<BifrostBlobOptions>? configure = null)
        {
            var options = new BifrostBlobOptions();
            configure?.Invoke(options);
            MountAuthRequirement.RequireOneServedEndpoint(app.ApplicationServices, options.Endpoint,
                nameof(UseBifrostBlobs), $"{nameof(BifrostBlobOptions)}.{nameof(BifrostBlobOptions.Endpoint)}");
            var requiresAuth = options.RequireAuth
                ?? MountAuthRequirement.Resolve(app.ApplicationServices, options.Endpoint);
            return app.UseMiddleware<BifrostBlobMiddleware>(options, requiresAuth);
        }
    }
}
