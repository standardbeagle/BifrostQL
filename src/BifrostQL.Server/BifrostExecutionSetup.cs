using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using GraphQL;
using GraphQL.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// The per-request state both GraphQL transports hand to GraphQL.NET: the active profile's
    /// model and schema, and the execution extensions carrying the profile-filtered
    /// <see cref="SqlExecutionManager"/>.
    /// </summary>
    internal sealed record BifrostExecutionSetup(
        IDbModel Model,
        ISchema Schema,
        BifrostProfile ActiveProfile,
        Inputs Extensions);

    /// <summary>
    /// The one copy of the pipeline glue that runs before a GraphQL document executes:
    /// connection pre-check, profile resolution, endpoint load, per-profile model/schema,
    /// profile-filtered transformers and observers, correlation id and extension merge. The
    /// HTTP <see cref="BifrostDocumentExecutor"/> and the protocol-independent
    /// <see cref="BifrostEngine"/> both call it and keep only their wire mapping. Two copies
    /// had drifted: the engine let schema-load and profile-build faults escape to the host as a
    /// raw 500 (driver and validator text on a developer exception page), skipped the
    /// connection pre-check and fell back to the base model where HTTP refused.
    /// </summary>
    internal static class BifrostExecutionPipeline
    {
        internal const string NoConnectionMessage = "No database connection configured. Set a connection string first.";
        internal const string UnknownEndpointMessage = "No BifrostQL endpoint is configured for this path.";
        internal const string ConnectionFailedMessage = "Database connection failed.";
        internal const string ProfileLoadFailedMessage = "The requested profile failed to load.";

        /// <summary>
        /// Prepares execution, or returns the wire-safe error text for the condition that stopped
        /// it. Every refusal text is a constant (invariant 3); the detail goes to the log only.
        /// </summary>
        /// <param name="services">The request's services.</param>
        /// <param name="httpContext">The request, for profile resolution; null off HTTP.</param>
        /// <param name="loadEndpoint">Loads the target endpoint's shared extensions.</param>
        /// <param name="userContext">The caller's user context; stamped with correlation id and profile.</param>
        /// <param name="correlationId">The id stamped when the context carries none.</param>
        /// <param name="mergeWithModel">Runs with the profile model in hand, before the stamps.</param>
        internal static async Task<(BifrostExecutionSetup? Setup, string? Error)> PrepareAsync(
            IServiceProvider services,
            HttpContext? httpContext,
            Func<Task<Inputs>> loadEndpoint,
            IDictionary<string, object?> userContext,
            string correlationId,
            Action<IDbModel>? mergeWithModel = null)
        {
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(BifrostExecutionPipeline));

            var setupOptions = services.GetService<BifrostSetupOptions>();
            if (setupOptions != null && !setupOptions.HasConnectionString)
                return (null, NoConnectionMessage);

            // An unspecified profile resolves to the system default profile (raw base schema, no
            // opt-in modules); a named profile resolves through the registry, which also enforces
            // its auth/role requirements.
            var profileResult = BifrostProfileResolver.Resolve(
                services.GetService<BifrostProfileRegistry>(), httpContext, logger);
            if (profileResult.HasError)
                return (null, profileResult.ErrorMessage!);
            var profileName = profileResult.ProfileName;
            var activeProfile = profileResult.ActiveProfile;

            Inputs sharedExtensions;
            try
            {
                sharedExtensions = await loadEndpoint();
            }
            catch (UnknownBifrostEndpointException ex)
            {
                // An unmatched path in a multi-endpoint deployment. Never fall back to another
                // database's schema.
                logger?.LogWarning("No BifrostQL endpoint configured for request path: {Message}", ex.Message);
                return (null, UnknownEndpointMessage);
            }
            catch (Exception ex)
            {
                var innerMessage = ex.InnerException?.Message ?? ex.Message;
                logger?.LogError(ex, "Schema resolution failed: {Message}", innerMessage);
                // The raw connection/driver message can expose host names and infrastructure
                // detail; it stays server-side unless BIFROST_EXPOSE_DB_ERRORS opts in locally.
                var expose = Environment.GetEnvironmentVariable(BifrostExecutionError.ExposeDbErrorsEnvVar);
                var reveal = string.Equals(expose, "1", StringComparison.Ordinal)
                    || string.Equals(expose, "true", StringComparison.OrdinalIgnoreCase);
                return (null, reveal ? $"Database connection failed: {innerMessage}" : ConnectionFailedMessage);
            }

            var profileCache = (ProfileModelCache)(sharedExtensions["profileModelCache"]
                ?? throw new InvalidDataException("profileModelCache not configured"));
            IDbModel model;
            ISchema schema;
            try
            {
                (model, schema) = profileCache.GetFor(profileName);
            }
            catch (Exception ex)
            {
                // A named profile is validated lazily on its first request; an invalid one throws
                // ModelConfigValidator's aggregate here, which names tables, columns and metadata
                // values. A distinct condition with its own constant (invariant 10).
                logger?.LogError(ex, "Profile model load failed for profile '{Profile}'", profileName);
                return (null, ProfileLoadFailedMessage);
            }

            mergeWithModel?.Invoke(model);
            if (!userContext.ContainsKey("_correlationId"))
                userContext["_correlationId"] = correlationId;
            // Downstream resolvers read the active profile from the user context.
            userContext[BifrostProfile.UserContextKey] = activeProfile;

            // Always filter transformers/observers by the active profile: the empty default
            // profile drops every opt-in module, while security and data-integrity modules stay.
            var transformerService = new QueryTransformerService(
                BifrostProfileRegistry.FilterBy(services.GetRequiredService<IFilterTransformers>(), activeProfile));
            var observers = services.GetService<IQueryObservers>();
            if (observers != null)
                observers = BifrostProfileRegistry.FilterBy(observers, activeProfile);

            // "model" is overridden with the per-profile model so request-time readers
            // (IBifrostContext, resolvers) see the model the schema was built from.
            var extensions = new Dictionary<string, object?>();
            foreach (var (key, value) in sharedExtensions)
                extensions[key] = value;
            extensions["model"] = model;
            extensions["tableReaderFactory"] = new SqlExecutionManager(model, schema, transformerService, observers,
                services.GetService<BifrostQL.Core.Observers.EngineMetrics>());

            return (new BifrostExecutionSetup(model, schema, activeProfile, new Inputs(extensions)), null);
        }
    }
}
