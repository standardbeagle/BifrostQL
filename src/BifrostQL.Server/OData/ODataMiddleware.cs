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

            var result = await _reads.ExecuteAsync(
                new QueryIntent
                {
                    Query = read.Query,
                    UserContext = userContext,
                    Endpoint = _options.Endpoint,
                },
                context.RequestAborted);

            var json = ODataDocumentWriter.WriteEntityCollection(
                ServiceRoot(context), entity.Table.GraphQlName, read.ProjectedColumns,
                result.Rows, projected: options.Select is not null);

            await WriteBodyAsync(context, "application/json; charset=utf-8", json);
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
