using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace BifrostQL.Server
{
    /// <summary>
    /// Configuration for the BifrostQL info/status endpoint and server headers.
    /// </summary>
    public sealed class BifrostInfoOptions
    {
        /// <summary>
        /// Whether the info endpoint is enabled. Default: true.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// The path for the info endpoint. Default: "/_info".
        /// </summary>
        public string Path { get; set; } = "/_info";

        /// <summary>
        /// Whether authentication is required to access the info endpoint. Default: false.
        /// </summary>
        public bool RequireAuth { get; set; }

        /// <summary>
        /// Whether to include the X-BifrostQL-Schema-Hash header on responses. Default: true.
        /// </summary>
        public bool IncludeSchemaHash { get; set; } = true;
    }

    /// <summary>
    /// JSON response model for the info endpoint.
    /// </summary>
    public sealed class BifrostInfoResponse
    {
        [JsonPropertyName("version")]
        public string Version { get; init; } = "";

        [JsonPropertyName("databaseType")]
        public string DatabaseType { get; init; } = "";

        [JsonPropertyName("uptime")]
        public string Uptime { get; init; } = "";

        [JsonPropertyName("schemaCacheStatus")]
        public string SchemaCacheStatus { get; init; } = "";

        [JsonPropertyName("enabledModules")]
        public IReadOnlyList<string> EnabledModules { get; init; } = Array.Empty<string>();

        [JsonPropertyName("schemaHash")]
        public string? SchemaHash { get; init; }

        [JsonPropertyName("serverStartTime")]
        public DateTimeOffset ServerStartTime { get; init; }
    }

    /// <summary>
    /// Tracks server start time for uptime calculation.
    /// Registered as a singleton in DI.
    /// </summary>
    public sealed class BifrostServerClock
    {
        public DateTimeOffset StartTime { get; } = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Middleware that adds BifrostQL identification headers to all responses.
    /// </summary>
    public sealed class BifrostHeaderMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly string _serverHeader;

        public BifrostHeaderMiddleware(RequestDelegate next)
        {
            _next = next;
            _serverHeader = $"BifrostQL/{GetVersion()}";
        }

        public async Task InvokeAsync(HttpContext context)
        {
            context.Response.Headers["Server"] = _serverHeader;
            await _next(context);
        }

        public static string GetVersion()
        {
            return typeof(BifrostHeaderMiddleware).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
                ?? typeof(BifrostHeaderMiddleware).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }
    }

    /// <summary>
    /// Middleware that handles the BifrostQL info/status endpoint.
    /// </summary>
    public sealed class BifrostInfoMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly BifrostInfoOptions _options;

        public BifrostInfoMiddleware(RequestDelegate next, BifrostInfoOptions options)
        {
            _next = next;
            _options = options;
        }

        public async Task InvokeAsync(HttpContext context, BifrostServerClock clock)
        {
            if (!_options.Enabled
                || !HttpMethods.IsGet(context.Request.Method)
                || !string.Equals(context.Request.Path.Value, _options.Path, StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            if (_options.RequireAuth && !(context.User?.Identity?.IsAuthenticated ?? false))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var modules = CollectEnabledModules(context.RequestServices);
            var schemaHash = _options.IncludeSchemaHash ? ComputeSchemaHash(context.RequestServices) : null;

            var response = new BifrostInfoResponse
            {
                Version = BifrostHeaderMiddleware.GetVersion(),
                DatabaseType = DetectDatabaseType(context.RequestServices),
                Uptime = FormatUptime(DateTimeOffset.UtcNow - clock.StartTime),
                SchemaCacheStatus = DetectSchemaCacheStatus(context.RequestServices),
                EnabledModules = modules,
                SchemaHash = schemaHash,
                ServerStartTime = clock.StartTime,
            };

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await JsonSerializer.SerializeAsync(context.Response.Body, response);
        }

        private static string FormatUptime(TimeSpan uptime)
        {
            if (uptime.TotalDays >= 1)
                return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
            if (uptime.TotalHours >= 1)
                return $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
            return $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";
        }

        private static IReadOnlyList<string> CollectEnabledModules(IServiceProvider services)
        {
            var names = new List<string>();

            var mutationModules = services.GetService<Core.Modules.IMutationModules>();
            if (mutationModules != null)
            {
                foreach (var m in mutationModules)
                    names.Add(m.GetType().Name);
            }

            var filterTransformers = services.GetService<Core.Modules.IFilterTransformers>();
            if (filterTransformers != null)
            {
                foreach (var t in filterTransformers)
                    names.Add(t.GetType().Name);
            }

            var mutationTransformers = services.GetService<Core.Modules.IMutationTransformers>();
            if (mutationTransformers != null)
            {
                foreach (var t in mutationTransformers)
                    names.Add(t.GetType().Name);
            }

            var queryObservers = services.GetService<Core.Modules.IQueryObservers>();
            if (queryObservers != null)
            {
                foreach (var o in queryObservers)
                    names.Add(o.GetType().Name);
            }

            return names;
        }

        private static string DetectDatabaseType(IServiceProvider services)
        {
            var cache = services.GetService<Core.Schema.PathCache<GraphQL.Inputs>>();
            if (cache != null)
            {
                var firstValue = cache.GetFirstValue();
                if (firstValue != null && firstValue.TryGetValue("connFactory", out var factoryObj) && factoryObj is Core.Model.IDbConnFactory factory)
                {
                    return factory.Dialect.GetType().Name.Replace("Dialect", "");
                }
            }

            return "Unknown";
        }

        private static string DetectSchemaCacheStatus(IServiceProvider services)
        {
            var cache = services.GetService<Core.Schema.PathCache<GraphQL.Inputs>>();
            return cache != null ? "active" : "none";
        }

        private static string? ComputeSchemaHash(IServiceProvider services)
        {
            var cache = services.GetService<Core.Schema.PathCache<GraphQL.Inputs>>();
            if (cache == null)
                return null;

            var schema = services.GetService<GraphQL.Types.ISchema>();
            if (schema == null)
                return null;

            var schemaDescription = schema.Description ?? schema.GetType().FullName ?? "schema";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(schemaDescription));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
