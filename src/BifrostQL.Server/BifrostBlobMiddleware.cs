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
        /// Same rationale as the saved-objects endpoint: this serves row data, so a
        /// caller must present an identity that projects to a non-empty Bifrost user
        /// context. Clearing it is a deliberate trusted-loopback desktop choice.
        /// </summary>
        public bool RequireAuth { get; set; } = true;

        /// <summary>
        /// The registered GraphQL endpoint whose cached model/connection blobs are
        /// read from. Null selects the single registered endpoint; with multiple
        /// endpoints it is required and misconfiguration fails closed (503), never a
        /// silent first-endpoint fallback.
        /// </summary>
        public string? Endpoint { get; set; }

        /// <summary>
        /// Upper bound on a served blob. The read seam materializes the column value
        /// in memory (there is no DB-side windowed blob read yet), so this bounds
        /// SERVER memory per request; HTTP Range only windows the wire. Over the cap
        /// is an explicit 413, never a truncated body. Default 32 MiB.
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
    /// non-blob or unknown column, pipeline denial — maps to the SAME constant 404
    /// (invariant 10 / the S3 precedent): a denial is indistinguishable from absence,
    /// and no message ever carries model, driver, or transformer text (invariant 3).</para>
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

        public BifrostBlobMiddleware(RequestDelegate next, BifrostBlobOptions options, ILogger<BifrostBlobMiddleware>? logger = null)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
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
            var outcome = BifrostIdentityGate.Project(context, out var userContext);
            if (outcome == BifrostIdentityOutcome.Unprojectable
                || (_options.RequireAuth && outcome != BifrostIdentityOutcome.Projected))
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
                // Endpoint resolution failing is a DEPLOYMENT condition (multiple
                // endpoints with none configured, unknown endpoint path), not a data
                // condition — map it distinctly, detail server-side only.
                _logger?.LogError(ex, "Blob endpoint could not resolve its GraphQL endpoint");
                await WriteTextAsync(context, StatusCodes.Status503ServiceUnavailable,
                    "The blob endpoint is not configured for this deployment.");
                return;
            }

            try
            {
                await ServeAsync(context, executor, model, userContext, segments[0], segments[1]);
            }
            catch (BifrostExecutionError)
            {
                // Pipeline rejection (tenant scope, policy deny, …): the SAME 404 as a
                // row that does not exist — a denial must not be an existence oracle.
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
            var column = table?.Columns.FirstOrDefault(c =>
                string.Equals(c.GraphQlName, columnName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.DbName, columnName, StringComparison.OrdinalIgnoreCase));

            // Unknown table, unknown column, and a column that is not a blob all read
            // as the same 404 as an absent row — this endpoint serves binary content,
            // it is not an introspection surface.
            if (table is null || column is null || !BinaryDbTypes.Contains(BaseType(column.DataType)))
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }

            // EVERY primary-key column must be supplied (k.<graphQlName>=value) —
            // composite keys are addressed in full, never by a first-column guess.
            var keyColumns = table.KeyColumns.ToList();
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
                    await WriteTextAsync(context, StatusCodes.Status400BadRequest,
                        $"Missing key parameter 'k.{key.GraphQlName}'.");
                    return;
                }
                if (!TryConvertKey(raw[0]!, key.DataType, out var keyValue))
                {
                    await WriteTextAsync(context, StatusCodes.Status400BadRequest,
                        $"Key parameter 'k.{key.GraphQlName}' is not a valid {BaseType(key.DataType)} value.");
                    return;
                }
                filter[key.GraphQlName] = new Dictionary<string, object?> { ["_eq"] = keyValue };
            }

            var query = new GqlObjectQuery
            {
                DbTable = table,
                SchemaName = table.TableSchema,
                TableName = table.DbName,
                GraphQlName = table.GraphQlName,
                Path = table.GraphQlName,
                Filter = TableFilter.FromObject(filter, table.DbName),
                Limit = 1,
            };
            query.ScalarColumns.Add(new GqlObjectColumn(column.DbName));

            var result = await executor.ExecuteAsync(new QueryIntent
            {
                Query = query,
                UserContext = userContext,
                Endpoint = _options.Endpoint,
            }, context.RequestAborted);

            var row = result.Rows.Count == 1 ? result.Rows[0] : null;
            object? value = null;
            if (row is not null && !row.TryGetValue(column.GraphQlName, out value))
                row.TryGetValue(column.DbName, out value);

            // The read seam materializes blobs as base64 (ReaderEnum.DbConvert);
            // byte[] is accepted for providers that hand bytes through untouched.
            var bytes = value switch
            {
                byte[] raw => raw,
                string base64 => TryFromBase64(base64),
                _ => null,
            };
            if (bytes is null)
            {
                await WriteTextAsync(context, StatusCodes.Status404NotFound, NotFoundBody);
                return;
            }
            if (bytes.Length > _options.MaxBlobBytes)
            {
                await WriteTextAsync(context, StatusCodes.Status413PayloadTooLarge,
                    $"The value exceeds the {_options.MaxBlobBytes}-byte blob limit.");
                return;
            }

            await WriteBlobAsync(context, bytes, table.GraphQlName, column.GraphQlName);
        }

        private static async Task WriteBlobAsync(HttpContext context, byte[] bytes, string tableName, string columnName)
        {
            var response = context.Response;
            var (mime, extension, inline) = Sniff(bytes);
            response.Headers["Accept-Ranges"] = "bytes";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers.CacheControl = "private, no-store";
            response.ContentType = mime;
            // Only magic-byte-verified image types render inline; everything else is
            // an attachment so stored markup can never execute on this origin.
            var fileName = $"{tableName}-{columnName}{extension}";
            response.Headers.ContentDisposition = $"{(inline ? "inline" : "attachment")}; filename=\"{fileName}\"";

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
        /// Single-range parsing only (<c>bytes=a-b</c>, <c>bytes=a-</c>, <c>bytes=-n</c>).
        /// A multi-range or malformed header is served as the FULL body (RFC 9110
        /// permits ignoring Range); only a syntactically valid but unsatisfiable
        /// range is a 416.
        /// </summary>
        internal static (long Start, long Length, bool Satisfiable, bool IsPartial) ResolveRange(string? header, long totalLength)
        {
            var full = (0L, totalLength, true, false);
            if (string.IsNullOrEmpty(header) || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return full;
            var spec = header["bytes=".Length..];
            if (spec.Contains(',')) return full;
            var dash = spec.IndexOf('-');
            if (dash < 0) return full;
            var fromText = spec[..dash].Trim();
            var toText = spec[(dash + 1)..].Trim();

            if (fromText.Length == 0)
            {
                // suffix range: last N bytes
                if (!long.TryParse(toText, out var suffix) || suffix <= 0) return full;
                if (totalLength == 0) return (0, 0, false, false);
                var length = Math.Min(suffix, totalLength);
                return (totalLength - length, length, true, true);
            }
            if (!long.TryParse(fromText, out var start) || start < 0) return full;
            if (start >= totalLength) return (0, 0, false, false);
            if (toText.Length == 0) return (start, totalLength - start, true, true);
            if (!long.TryParse(toText, out var end) || end < start) return full;
            return (start, Math.Min(end, totalLength - 1) - start + 1, true, true);
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
                case "int" or "integer" or "bigint" or "smallint" or "tinyint":
                    if (!long.TryParse(raw, out var l)) return false;
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

    public static class BifrostBlobExtensions
    {
        /// <summary>Mounts the direct binary-link endpoint (see <see cref="BifrostBlobMiddleware"/>).</summary>
        public static IApplicationBuilder UseBifrostBlobs(this IApplicationBuilder app, Action<BifrostBlobOptions>? configure = null)
        {
            var options = new BifrostBlobOptions();
            configure?.Invoke(options);
            return app.UseMiddleware<BifrostBlobMiddleware>(options);
        }
    }
}
