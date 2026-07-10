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

        /// <summary>When true, unauthenticated callers get 401. Hosted deployments should enable it.</summary>
        public bool RequireAuth { get; set; } = false;
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
    /// </summary>
    public sealed class BifrostSavedObjectsMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly BifrostSavedObjectsOptions _options;

        public BifrostSavedObjectsMiddleware(RequestDelegate next, BifrostSavedObjectsOptions options)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task InvokeAsync(HttpContext context)
        {
            if (!_options.Enabled || !context.Request.Path.StartsWithSegments(_options.Path, StringComparison.OrdinalIgnoreCase, out var remaining))
            {
                await _next(context);
                return;
            }

            if (_options.RequireAuth && !(context.User?.Identity?.IsAuthenticated ?? false))
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
                await DispatchAsync(context, store, segments);
            }
            catch (SavedObjectValidationException ex)
            {
                await WriteError(context, StatusCodes.Status400BadRequest, ex.Message);
            }
            catch (SavedObjectVersionConflictException ex)
            {
                await WriteError(context, StatusCodes.Status409Conflict, ex.Message);
            }
        }

        private async Task DispatchAsync(HttpContext context, ISavedObjectStore store, string[] segments)
        {
            var method = context.Request.Method;

            if (HttpMethods.IsGet(method))
            {
                if (segments.Length == 0)
                {
                    var type = context.Request.Query.TryGetValue("type", out var t) && !string.IsNullOrEmpty(t)
                        ? SavedObjectJson.ParseType(t!)
                        : (SavedObjectType?)null;
                    await WriteJson(context, StatusCodes.Status200OK, SavedObjectJson.SerializeList(await store.ListAsync(type, context.RequestAborted)));
                    return;
                }
                if (segments.Length == 1)
                {
                    var type = SavedObjectJson.ParseType(segments[0]);
                    await WriteJson(context, StatusCodes.Status200OK, SavedObjectJson.SerializeList(await store.ListAsync(type, context.RequestAborted)));
                    return;
                }
                if (segments.Length == 2)
                {
                    var found = await store.GetAsync(SavedObjectJson.ParseType(segments[0]), segments[1], context.RequestAborted);
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
                var body = await ReadBody(context);
                var incoming = SavedObjectJson.Deserialize(body);
                if (incoming.Type != type || !string.Equals(incoming.Id, segments[1], StringComparison.Ordinal))
                    throw new SavedObjectValidationException("Saved object 'type'/'id' in the body must match the URL path.");
                var stored = await store.PutAsync(incoming, context.RequestAborted);
                await WriteJson(context, StatusCodes.Status200OK, SavedObjectJson.Serialize(stored));
                return;
            }
            else if (HttpMethods.IsDelete(method) && segments.Length == 2)
            {
                await store.DeleteAsync(SavedObjectJson.ParseType(segments[0]), segments[1], context.RequestAborted);
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return;
            }

            await WriteError(context, StatusCodes.Status405MethodNotAllowed, $"Unsupported {method} on '{context.Request.Path}'.");
        }

        private static async Task<string> ReadBody(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            return await reader.ReadToEndAsync(context.RequestAborted);
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
