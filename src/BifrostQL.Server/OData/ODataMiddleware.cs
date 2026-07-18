using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BifrostQL.Core.Resolvers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server.OData
{
    /// <summary>
    /// The opt-in OData v4 HTTP front door. Authenticates the request via
    /// <see cref="ODataAuthenticator"/> (Bearer principal or configured Basic credentials) and
    /// maps every failure to a deterministic OData JSON error envelope.
    ///
    /// <para>This slice serves the two discovery documents: the service document (OData JSON, at
    /// the endpoint root) and <c>$metadata</c> (CSDL/EDMX XML). Both are generated from the cached
    /// <see cref="Core.Model.IDbModel"/> resolved through <see cref="IQueryIntentExecutor"/> and
    /// filtered to only the tables/columns/navigations the authenticated caller may READ, using
    /// the same policy gate the query path enforces (fail-closed;
    /// .claude/rules/protocol-adapter-security.md invariant 4). Entity reads and query options are
    /// deferred to later slices, so any other authenticated path answers <c>501 NotImplemented</c>.
    /// The adapter owns only HTTP/OData parsing + encoding; identity comes from the shared factory
    /// and the model from <c>IQueryIntentExecutor</c>, both injected from DI.</para>
    ///
    /// <para>Only <see cref="ODataProtocolException"/> — a deliberately user-facing type with a
    /// curated message — is mapped onto the wire verbatim; every other exception maps to a
    /// generic sanitized InternalError with detail logged server-side only
    /// (.claude/rules/protocol-adapter-security.md invariants 1 and 3).</para>
    /// </summary>
    public sealed class ODataMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ODataOptions _options;
        private readonly ODataAuthenticator _authenticator;
        private readonly IQueryIntentExecutor _reads;
        private readonly ILogger<ODataMiddleware> _logger;
        private readonly byte[] _tokenSecret;

        public ODataMiddleware(
            RequestDelegate next,
            ODataOptions options,
            ODataAuthenticator authenticator,
            IQueryIntentExecutor reads,
            ILogger<ODataMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _authenticator = authenticator ?? throw new ArgumentNullException(nameof(authenticator));
            _reads = reads ?? throw new ArgumentNullException(nameof(reads));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            // A configured secret keeps continuation tokens valid across restarts and across a
            // horizontally-scaled fleet; absent one we generate a per-instance random key so tokens
            // are still integrity-protected — they simply do not survive a restart or resolve on
            // another instance. The trade-off is logged, never silent (mirrors the S3 adapter).
            if (!string.IsNullOrEmpty(_options.ContinuationTokenSecret))
            {
                _tokenSecret = Encoding.UTF8.GetBytes(_options.ContinuationTokenSecret);
            }
            else
            {
                _tokenSecret = RandomNumberGenerator.GetBytes(32);
                _logger.LogWarning(
                    "No OData ContinuationTokenSecret configured; using a per-instance random key. " +
                    "In-flight continuation tokens will not survive a restart or resolve on another instance.");
            }
        }

        public async Task InvokeAsync(HttpContext context)
        {
            try
            {
                // Cancellation is honored before any auth work so a client abort short-circuits
                // the credential comparison cost.
                context.RequestAborted.ThrowIfCancellationRequested();

                // Authenticate first — every request is gated before any operation is dispatched.
                var userContext = await _authenticator.AuthenticateAsync(context, context.RequestAborted);

                await DispatchAsync(context, userContext);
            }
            catch (ODataProtocolException ex)
            {
                await WriteErrorAsync(context, ex);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Client went away; nothing to write.
            }
            catch (Exception ex)
            {
                // Never forward an internal exception message onto the wire (invariant 3):
                // log full detail server-side, return a generic sanitized envelope.
                _logger.LogError(ex, "Unhandled error in OData endpoint.");
                await WriteErrorAsync(context, ODataProtocolException.InternalError());
            }
        }

        /// <summary>
        /// Routes an authenticated request to the discovery document it asks for. The endpoint
        /// root serves the service document; <c>$metadata</c> serves the EDMX; anything else is a
        /// clean 501 (entity reads/query options are later slices). The path is relative to the
        /// mounted route prefix (the prefix lives on <see cref="HttpRequest.PathBase"/>).
        /// </summary>
        private async Task DispatchAsync(HttpContext context, IDictionary<string, object?> userContext)
        {
            var path = context.Request.Path.Value ?? string.Empty;

            if (IsRoot(path))
            {
                await WriteServiceDocumentAsync(context, userContext);
                return;
            }

            if (string.Equals(path.TrimStart('/'), "$metadata", StringComparison.Ordinal))
            {
                await WriteMetadataAsync(context, userContext);
                return;
            }

            await WriteEntityCollectionAsync(context, userContext, path);
        }

        /// <summary>
        /// Serves an entity-set collection read. The path's single segment names the entity set; a
        /// key predicate (<c>Orders(1)</c>) or a navigation segment is a later slice and answers a
        /// clean 501. The entity set is resolved against the identity-filtered projection, so an
        /// unknown OR unauthorized set is a 404 alike (no existence oracle). The read crosses
        /// <see cref="IQueryIntentExecutor"/> with NO adapter-built predicate — tenant/soft-delete
        /// scope is ANDed on by the pipeline — and the resolved rows are serialized as the OData v4
        /// collection payload.
        /// </summary>
        private async Task WriteEntityCollectionAsync(
            HttpContext context, IDictionary<string, object?> userContext, string path)
        {
            var name = path.Trim('/');
            if (name.Length == 0 || name.Contains('/') || name.Contains('('))
                throw ODataProtocolException.NotImplemented();

            var model = await _reads.GetModelAsync(_options.Endpoint);
            var entities = ODataModelVisibility.Project(model, userContext);
            var entity = ResolveEntitySet(entities, name);

            var options = ODataReadOptions.FromQuery(context.Request.Query);
            // Translate $filter against the SAME identity-filtered projection: an unknown/read-denied
            // property is a 400 and never interpolated; literals become bound parameters. The result
            // is a TableFilter the pipeline AND-composes with tenant/soft-delete scope.
            var filter = ODataFilterTranslator.Translate(entity, options.Filter, model.TypeMapper);
            var read = ODataEntityReadTranslator.Translate(
                entity, options, _options.DefaultPageSize, _options.MaxPageSize, filter);

            // The effective (clamped) page size is what the translator resolved onto Limit; the
            // continuation token binds THIS value, so a caller replaying a token with a different
            // $top re-derives a different binding and is rejected.
            var pageSize = read.Query.Limit ?? _options.DefaultPageSize;

            // Bind the token to the caller-visible query shape and identity re-derived from THIS
            // request — never trusting a persisted fingerprint. A token minted for another set,
            // filter, order, page size, or identity fails the MAC check on replay.
            var binding = new ODataPageBinding(
                entity.Table.GraphQlName,
                ODataContinuationToken.QueryShapeHash(options.Filter, options.Select, options.OrderBy),
                pageSize,
                ODataContinuationToken.FingerprintIdentity(userContext));

            // The resume offset: from the server-signed $skiptoken when present (re-validated against
            // the live binding), otherwise the $skip the translator resolved. A tampered/expired/
            // cross-context token throws a clean OData 400 here — never a silent wrong page.
            var offset = options.SkipToken is not null
                ? ODataContinuationToken.Decode(
                    options.SkipToken, binding, _tokenSecret, DateTimeOffset.UtcNow, _options.ContinuationTokenTtl)
                : (read.Query.Offset ?? 0);

            // Over-fetch one row past the page to detect whether a next page exists without a
            // second round-trip; the extra row is trimmed from the response.
            read.Query.Offset = offset > 0 ? offset : null;
            read.Query.Limit = pageSize + 1;
            // $count reports the pipeline-filtered total through the SAME intent (COUNT(*) with the
            // same tenant/soft-delete/$filter predicate) — never a side-channel query.
            read.Query.IncludeResult = options.Count;

            var result = await _reads.ExecuteAsync(
                new QueryIntent
                {
                    Query = read.Query,
                    UserContext = userContext,
                    Endpoint = _options.Endpoint,
                },
                context.RequestAborted);

            var hasMore = result.Rows.Count > pageSize;
            var pageRows = hasMore
                ? (IReadOnlyList<IReadOnlyDictionary<string, object?>>)result.Rows.Take(pageSize).ToList()
                : result.Rows;

            string? nextLink = null;
            if (hasMore)
            {
                var token = ODataContinuationToken.Issue(offset + pageSize, DateTimeOffset.UtcNow, binding, _tokenSecret);
                nextLink = BuildNextLink(ServiceRoot(context), entity.Table.GraphQlName, options, token);
            }

            var json = ODataDocumentWriter.WriteEntityCollection(
                ServiceRoot(context), entity.Table.GraphQlName, read.ProjectedColumns,
                pageRows, projected: options.Select is not null,
                count: options.Count ? result.TotalCount : null,
                nextLink: nextLink);

            await WriteBodyAsync(context, "application/json; charset=utf-8", json);
        }

        /// <summary>
        /// Builds the <c>@odata.nextLink</c>: the entity-set URL with the caller's valid query
        /// options preserved (so the continuation request re-derives the identical binding) and the
        /// server-signed <c>$skiptoken</c> appended. The prior <c>$skip</c>/<c>$skiptoken</c> are
        /// dropped — the token is now the sole, validated offset source. Every value is
        /// percent-encoded; no request text is interpolated unescaped.
        /// </summary>
        private static string BuildNextLink(
            string serviceRoot, string entitySetName, ODataReadOptions options, string token)
        {
            var parts = new List<string>();
            void Add(string key, string? value)
            {
                // The keys are fixed OData literals ($filter, $skiptoken, …) — left verbatim per the
                // OData URL convention; only the (untrusted) values are percent-encoded.
                if (value is not null)
                    parts.Add(key + "=" + Uri.EscapeDataString(value));
            }

            Add("$filter", options.Filter);
            Add("$select", options.Select);
            Add("$orderby", options.OrderBy);
            Add("$top", options.Top);
            if (options.Count)
                Add("$count", "true");
            Add("$skiptoken", token);

            return $"{serviceRoot}/{entitySetName}?{string.Join("&", parts)}";
        }

        /// <summary>
        /// Resolves the entity-set path segment against the caller's visible entities by EDM name
        /// (case-insensitively). Absent (unknown or unauthorized) → 404; a name matching more than
        /// one visible set → a clean ambiguity 400 rather than an arbitrary pick.
        /// </summary>
        private static ODataEntity ResolveEntitySet(IReadOnlyList<ODataEntity> entities, string name)
        {
            var matches = entities
                .Where(e => string.Equals(e.Table.GraphQlName, name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
                throw ODataProtocolException.NotFound();
            if (matches.Count > 1)
                throw ODataProtocolException.BadRequest($"The entity set name '{name}' is ambiguous.");
            return matches[0];
        }

        private static bool IsRoot(string path)
            => path.Length == 0 || path == "/";

        private async Task WriteServiceDocumentAsync(HttpContext context, IDictionary<string, object?> userContext)
        {
            var entities = await ProjectAsync(userContext);
            var serviceRoot = ServiceRoot(context);
            var json = ODataDocumentWriter.WriteServiceDocument(entities, serviceRoot);

            await WriteBodyAsync(context, "application/json; charset=utf-8", json);
        }

        private async Task WriteMetadataAsync(HttpContext context, IDictionary<string, object?> userContext)
        {
            var entities = await ProjectAsync(userContext);
            var model = await _reads.GetModelAsync(_options.Endpoint);
            var xml = ODataDocumentWriter.WriteMetadata(entities, model.TypeMapper);

            await WriteBodyAsync(context, "application/xml; charset=utf-8", xml);
        }

        private async Task<IReadOnlyList<ODataEntity>> ProjectAsync(IDictionary<string, object?> userContext)
        {
            var model = await _reads.GetModelAsync(_options.Endpoint);
            return ODataModelVisibility.Project(model, userContext);
        }

        /// <summary>
        /// The service root the <c>@odata.context</c> pointer is built from: scheme + host + the
        /// mounted route prefix (carried on <see cref="HttpRequest.PathBase"/>), with no trailing
        /// slash.
        /// </summary>
        private static string ServiceRoot(HttpContext context)
        {
            var request = context.Request;
            var prefix = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;
            return $"{request.Scheme}://{request.Host}{prefix}".TrimEnd('/');
        }

        private static async Task WriteBodyAsync(HttpContext context, string contentType, string body)
        {
            var response = context.Response;
            if (response.HasStarted)
                return;

            response.StatusCode = 200;
            response.ContentType = contentType;
            var bytes = Encoding.UTF8.GetBytes(body);
            await response.Body.WriteAsync(bytes.AsMemory(), context.RequestAborted);
        }

        private async Task WriteErrorAsync(HttpContext context, ODataProtocolException ex)
        {
            var response = context.Response;
            if (response.HasStarted)
                return;

            response.StatusCode = ex.HttpStatus;
            response.ContentType = "application/json; charset=utf-8";

            // A 401 must carry a challenge so an interactive client knows how to authenticate.
            if (ex.HttpStatus == 401)
                response.Headers.WWWAuthenticate = $"Basic realm=\"{_options.Realm}\", Bearer";

            // OData v4 error format: {"error":{"code":"...","message":"..."}}.
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartObject("error");
                writer.WriteString("code", ex.Code);
                writer.WriteString("message", ex.Message);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            await response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), context.RequestAborted);
        }
    }
}
