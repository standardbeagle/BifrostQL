using System.Text;
using BifrostQL.Core.SavedObjects;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
    /// <summary>
    /// Options for the <c>/_saved-objects</c> CRUD endpoint. A parallel pipeline to the
    /// read-only app-metadata overlay (deliberately not merged — see AGENTS.md), sharing
    /// its camelCase-JSON conventions but adding write verbs.
    /// </summary>
    public sealed class BifrostSavedObjectsOptions
    {
        public bool Enabled { get; set; } = true;

        /// <summary>Base path for the endpoint. Default <c>/_saved-objects</c>.</summary>
        public string Path { get; set; } = "/_saved-objects";

        /// <summary>
        /// Overrides whether a caller must present an identity that projects to a non-empty
        /// Bifrost user context (otherwise 401). Null (the default) inherits the requirement from
        /// <c>MountAuthRequirement</c>: the single registered GraphQL endpoint's (required unless
        /// it sets <c>DisableAuth</c>), and required whenever no endpoint, or more than one, is
        /// registered.
        ///
        /// <para>This endpoint carries PUT and DELETE. Anonymous callers share ONE owner partition,
        /// so <c>false</c> (or inheriting from an anonymous endpoint) is a choice for a
        /// trusted-loopback desktop host. Set <c>true</c> to keep the endpoint closed in front of
        /// an anonymous GraphQL endpoint.</para>
        /// </summary>
        public bool? RequireAuth { get; set; }

        /// <summary>
        /// Largest PUT body read, in received bytes (default 512 KiB: twice the 256 KB definition
        /// cap, leaving room for the envelope). A larger body is refused with 413 before it is
        /// buffered, so the definition cap is not the first bound a request meets.
        /// </summary>
        public int MaxRequestBodyBytes { get; set; } = 2 * SavedObjectJson.DefaultMaxDefinitionBytes;
    }

    /// <summary>
    /// Serves REST CRUD for user-authored saved objects:
    /// <list type="bullet">
    /// <item><c>GET  /_saved-objects</c> — list all (optional <c>?type=</c>)</item>
    /// <item><c>GET  /_saved-objects/{type}</c> — list one type</item>
    /// <item><c>GET  /_saved-objects/{type}/{id}</c> — fetch one (404 if absent)</item>
    /// <item><c>PUT  /_saved-objects/{type}/{id}</c> — create/update (409 on stale version, 400 on invalid)</item>
    /// <item><c>DELETE /_saved-objects/{type}/{id}</c> — delete (204)</item>
    /// </list>
    /// Resolves <see cref="ISavedObjectStore"/> from request services, so the deployment
    /// chooses the file- or DB-backed impl at composition time.
    ///
    /// <para>Every route is scoped to the caller's OWNER partition (<see cref="SavedObjectOwner"/>),
    /// derived from the identity <see cref="BifrostIdentityGate"/> projected. There is no route,
    /// parameter or header by which a caller can name a different owner. The endpoint previously
    /// had no owner dimension at all: any accepted caller could list, read, overwrite and delete
    /// every other caller's objects.</para>
    /// </summary>
    public sealed class BifrostSavedObjectsMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly BifrostSavedObjectsOptions _options;
        private readonly bool _requiresAuth;

        /// <param name="next">The next middleware.</param>
        /// <param name="options">Endpoint options.</param>
        /// <param name="requiresAuth">
        /// Whether a caller must carry a projected identity. <c>UseBifrostSavedObjects</c> derives it
        /// from <c>MountAuthRequirement</c> (or the explicit <see cref="BifrostSavedObjectsOptions.RequireAuth"/>
        /// override); it is required here so no construction path can default it.
        /// </param>
        public BifrostSavedObjectsMiddleware(RequestDelegate next, BifrostSavedObjectsOptions options, bool requiresAuth)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _requiresAuth = requiresAuth;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_options.Enabled || !context.Request.Path.StartsWithSegments(_options.Path, StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                await _next(context);
                return;
            }

            // Identity through the SHARED gate — the same seam every other transport gate uses.
            // An IsAuthenticated flag on its own is not an identity: a subject-less principal, or
            // one from an OIDC issuer with no registered claim mapper, projects to an EMPTY context
            // (or throws), and either must be a 401 here exactly as it is on the GraphQL, binary,
            // gRPC and MCP gates. A projection FAULT is refused even when the mount does not require auth —
            // opting out of authentication is not opting into accepting a broken credential.
            var (outcome, userContext) = await BifrostIdentityGate.ProjectAsync(context);
            if (BifrostIdentityGate.Refuses(outcome, _requiresAuth))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // Every store operation below is scoped to this owner. It is derived from the projected
            // identity and NEVER from the request body, a header, or the path -- a client-supplied
            // owner is an impersonation parameter, not isolation. A projected identity that yields
            // no stable owner key is refused rather than treated as a shared/global owner, which
            // would merge every such caller into one bucket. The anonymous partition is reachable
            // only when the mount does not require auth (an anonymous endpoint, or RequireAuth=false).
            var owner = outcome == BifrostIdentityOutcome.Projected
                ? SavedObjectOwner.FromUserContext(userContext)
                : SavedObjectOwner.Anonymous;
            if (owner == null)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var store = context.RequestServices.GetService(typeof(ISavedObjectStore)) as ISavedObjectStore;
            if (store == null)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }

            var segments = remaining.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();

            try
            {
                await DispatchAsync(context, store, owner, segments);
            }
            catch (SavedObjectValidationException ex)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (SavedObjectVersionConflictException ex)
            {
                await WriteError(context, StatusCodes.Status409Conflict, ex.Message);
            }
            catch (SavedObjectQuotaExceededException ex)
            {
                await WriteError(context, StatusCodes.Status507InsufficientStorage, ex.Message);
            }
            catch (RequestBodyTooLargeException)
            {
                await WriteError(context, StatusCodes.Status413PayloadTooLarge,
                    $"The request body exceeds the {_options.MaxRequestBodyBytes}-byte limit.");
            }
        }

        private async Task DispatchAsync(HttpContext context, ISavedObjectStore store, string owner, string[] segments)
        {
            var method = context.Request.Method;

            if (HttpMethods.IsGet(method))
            {
                if (segments.Length == 0)
                {
                    var type = context.Request.Query.TryGetValue("type", out var t) && !string.IsNullOrEmpty(t)
                        ? SavedObjectJson.ParseType(t!)
                        : (SavedObjectType?)null;
                    await WriteJson(context, StatusCodes.Status200OK, SavedObjectJson.SerializeList(await store.ListAsync(owner, type, context.RequestAborted)));
                    return;
                }
                if (segments.Length == 1)
                {
                    var type = SavedObjectJson.ParseType(segments[0]);
                    await WriteJson(context, StatusCodes.Status200OK, SavedObjectJson.SerializeList(await store.ListAsync(owner, type, context.RequestAborted)));
                    return;
                }
                if (segments.Length == 2)
                {
                    var found = await store.GetAsync(owner, SavedObjectJson.ParseType(segments[0]), segments[1], context.RequestAborted);
                    if (found == null)
                    {
                        await WriteError(context, StatusCodes.Status404NotFound, $"Saved object '{segments[0]}/{segments[1]}' not found.");
                        return;
                    }
                    await WriteJson(context, StatusCodes.Status200OK, SavedObjectJson.Serialize(found));
                    return;
                }
            }
            else if (HttpMethods.IsPut(method) && segments.Length == 2)
            {
                var type = SavedObjectJson.ParseType(segments[0]);
                var body = await ReadBody(context, _options.MaxRequestBodyBytes);
                var incoming = SavedObjectJson.Deserialize(body);
                if (incoming.Type != type || !string.Equals(incoming.Id, segments[1], StringComparison.Ordinal))
                    throw new SavedObjectValidationException("Saved object 'type'/'id' in the body must match the URL path.");
                var stored = await store.PutAsync(owner, incoming, context.RequestAborted);
                await WriteJson(context, StatusCodes.Status200OK, SavedObjectJson.Serialize(stored));
                return;
            }
            else if (HttpMethods.IsDelete(method) && segments.Length == 2)
            {
                await store.DeleteAsync(owner, SavedObjectJson.ParseType(segments[0]), segments[1], context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await WriteError(context, StatusCodes.Status405MethodNotAllowed, $"Unsupported {method} on '{context.Request.Path}'.");
        }

        /// <summary>
        /// Reads the body up to <paramref name="maxBytes"/> received bytes, counting what
        /// arrives rather than trusting Content-Length, and throws past the cap.
        /// </summary>
        private static async Task<string> ReadBody(HttpContext context, int maxBytes)
        {
            if (context.Request.ContentLength > maxBytes)
                throw new RequestBodyTooLargeException();
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                    throw new RequestBodyTooLargeException();
                buffer.Write(chunk, 0, read);
            }
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private sealed class RequestBodyTooLargeException : Exception
        {
        }

        private static async Task WriteJson(HttpContext context, int status, string json)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), context.RequestAborted);
        }

        private static async Task WriteError(HttpContext context, int status, string message)
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var json = System.Text.Json.JsonSerializer.Serialize(new { error = message });
            await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(json), context.RequestAborted);
        }
    }
}
