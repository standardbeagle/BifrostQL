using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using GraphQL;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.ComputedColumns;
using BifrostQL.Core.Modules.Eav;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Core.Workflows;
using BifrostQL.Server.Logging;
using Microsoft.Extensions.Logging;

namespace BifrostQL.Server
{
    /// <summary>
    /// Shared DI wiring for the single-database (<see cref="BifrostSetupOptions"/>) and
    /// multi-database (<see cref="BifrostMultiDbOptions"/>) hosts. Both host option classes
    /// share near-verbatim service registration (workflow services, filter/mutation/observer
    /// composition, computed-column providers, the GraphQL pipeline, and cookie + OIDC auth);
    /// centralizing it here means adding one transformer type or tuning one guard is edited in
    /// exactly one place rather than in both ConfigureServices paths.
    /// </summary>
    internal static class BifrostServiceRegistrar
    {
        /// <summary>
        /// Registers the workflow executor, trigger host, scheduler, and the mutation/state
        /// observers that fan work into them. The workflow executor runs sidecar workflow
        /// endpoints through the same GraphQL pipeline as a direct request, so policy and
        /// tenant-filter still apply. See the Workflow Mutations guide.
        /// </summary>
        public static void RegisterWorkflowServices(IServiceCollection services)
        {
            services.AddSingleton<IBifrostWorkflowExecutor>(sp => new BifrostWorkflowExecutor(
                sp.GetRequiredService<IDocumentExecuter>(),
                sp.GetRequiredService<PathCache<Inputs>>(),
                sp));
            services.AddSingleton<IReadOnlyDictionary<string, WorkflowDefinition>>(
                new Dictionary<string, WorkflowDefinition>(StringComparer.OrdinalIgnoreCase));
            services.AddSingleton<IWorkflowDataExecutor>(sp => sp.GetRequiredService<IBifrostWorkflowExecutor>());
            services.AddSingleton<IWorkflowRunner>(sp => new WorkflowRunner(
                sp.GetRequiredService<IReadOnlyDictionary<string, WorkflowDefinition>>(),
                sp.GetRequiredService<IWorkflowDataExecutor>()));
            services.AddSingleton<WorkflowTriggerHost>();
            services.AddSingleton<WorkflowScheduler>();
            services.AddSingleton<MutationObservers>(sp => new MutationObservers(
                new IMutationObserver[] { sp.GetRequiredService<WorkflowTriggerHost>() }));
            // Before-commit veto hooks: built from every registered
            // IBeforeCommitMutationHook so a host/test can register one. There are
            // no built-in hooks, so the collection is empty unless the host adds one.
            services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
                sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
            services.AddSingleton<StateTransitionObservers>(sp => new StateTransitionObservers(
                new IStateTransitionObserver[]
                {
                    new StateTransitionAuditObserver(sp.GetRequiredService<IBifrostWorkflowExecutor>()),
                    sp.GetRequiredService<WorkflowTriggerHost>(),
                }));
        }

        /// <summary>
        /// Registers the filter/mutation transformer and query observer collections, composed
        /// with the always-on built-in transformers (see
        /// <see cref="BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers"/> and
        /// <see cref="BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers"/>).
        /// The generic <c>AddXTransformer&lt;T&gt;</c> types are added to DI and appended to the
        /// collection/factory overloads at build time.
        /// </summary>
        public static void RegisterTransformerServices(
            IServiceCollection services,
            IReadOnlyCollection<IFilterTransformer> filterTransformers,
            Func<IServiceProvider, IReadOnlyCollection<IFilterTransformer>>? filterTransformerLoader,
            IReadOnlyList<Type> filterTransformerTypes,
            IReadOnlyCollection<IMutationTransformer> mutationTransformers,
            Func<IServiceProvider, IReadOnlyCollection<IMutationTransformer>>? mutationTransformerLoader,
            IReadOnlyList<Type> mutationTransformerTypes,
            IReadOnlyCollection<IQueryObserver> queryObservers,
            Func<IServiceProvider, IReadOnlyCollection<IQueryObserver>>? queryObserverLoader,
            IReadOnlyList<Type> queryObserverTypes)
        {
            foreach (var t in filterTransformerTypes) services.TryAddSingleton(t);
            services.AddSingleton<IFilterTransformers>(sp => new FilterTransformersWrap
            {
                Transformers = BifrostServiceCollectionExtensions.WithBuiltInFilterTransformers(
                    BifrostServiceCollectionExtensions.ResolveTransformers(
                        filterTransformerLoader != null ? filterTransformerLoader(sp) : filterTransformers,
                        filterTransformerTypes, sp))
            });

            foreach (var t in mutationTransformerTypes) services.TryAddSingleton(t);
            services.AddSingleton<IMutationTransformers>(sp => new MutationTransformersWrap
            {
                Transformers = BifrostServiceCollectionExtensions.WithBuiltInMutationTransformers(
                    BifrostServiceCollectionExtensions.ResolveTransformers(
                        mutationTransformerLoader != null ? mutationTransformerLoader(sp) : mutationTransformers,
                        mutationTransformerTypes, sp), sp)
            });

            foreach (var t in queryObserverTypes) services.TryAddSingleton(t);
            services.AddSingleton<IQueryObservers>(sp =>
            {
                var userObservers = BifrostServiceCollectionExtensions.ResolveTransformers(
                    queryObserverLoader != null ? queryObserverLoader(sp) : queryObservers,
                    queryObserverTypes, sp);
                var loggingConfig = sp.GetRequiredService<BifrostLoggingConfiguration>();
                var allObservers = new List<IQueryObserver>(userObservers);
                if (loggingConfig.EnableQueryLogging)
                {
                    allObservers.Insert(0, new QueryLoggingObserver(
                        sp.GetRequiredService<ILogger<QueryLoggingObserver>>(), loggingConfig));
                }
                return new QueryObserversWrap
                {
                    Observers = allObservers,
                    OnError = (ex, observer, phase) =>
                        sp.GetRequiredService<ILogger<QueryObserversWrap>>()
                          .LogError(ex, "Observer {Observer} failed at {Phase}", observer.GetType().Name, phase),
                };
            });
        }

        /// <summary>
        /// Registers the query transformer service and the built-in computed-column providers
        /// (local/S3 file folders, state-machine transitions, EAV metadata).
        /// </summary>
        public static void RegisterComputedColumnServices(IServiceCollection services)
        {
            services.AddSingleton<IQueryTransformerService, QueryTransformerService>();
            services.AddSingleton<IComputedColumnProvider, LocalFileFolderComputedColumnProvider>();
            services.AddSingleton<IComputedColumnProvider, S3FileFolderComputedColumnProvider>();
            services.AddSingleton<IComputedColumnProvider>(_ => new StateMachineTransitionsProvider());
            services.AddSingleton<IComputedColumnProvider, EavMetaProvider>();
            services.AddSingleton<IComputedColumnProviders>(sp => new ComputedColumnProviders(sp.GetServices<IComputedColumnProvider>()));
        }

        /// <summary>
        /// Registers the GraphQL pipeline (System.Text.Json, the bounded depth/complexity
        /// analyzer, error logging) plus, when auth is enabled and JWT settings are bound, the
        /// cookie + OIDC authentication handlers. The complexity guard is applied even when
        /// unconfigured (secure defaults) to bound unauthenticated DoS from nested joins/aggregates.
        /// </summary>
        public static void RegisterGraphQlAndAuth(
            IServiceCollection services,
            bool isAuthEnabled,
            IConfigurationSection? jwtConfig,
            int maxDepth,
            int? maxComplexity,
            IConfigurationSection? loggingConfig)
        {
            services.AddGraphQL(b => b
                    .AddSystemTextJson()
                    .AddComplexityAnalyzer(c => GraphQlComplexityLimits.Apply(c, maxDepth, maxComplexity))
                    .AddBifrostErrorLogging(options =>
                    {
                        options.EnableConsole = loggingConfig?.GetValue("EnableConsole", true) ?? true;
                        options.EnableFile = loggingConfig?.GetValue("EnableFile", true) ?? true;
                        options.MinimumLevel = loggingConfig?.GetValue("MinimumLevel", LogLevel.Information) ?? LogLevel.Information;
                        options.LogFilePath = loggingConfig?.GetValue<string>("FilePath");
                        options.EnableQueryLogging = loggingConfig?.GetValue("EnableQueryLogging", true) ?? true;
                        options.SlowQueryThresholdMs = loggingConfig?.GetValue("SlowQueryThresholdMs", 1000) ?? 1000;
                        options.LogSql = loggingConfig?.GetValue("LogSql", false) ?? false;
                    })
            );

            if (isAuthEnabled && jwtConfig is not null)
            {
                var scopes = new HashSet<string>() { "openid" };
                foreach (var scope in (jwtConfig["Scopes"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    scopes.Add(scope);
                }
                services
                    .AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                        options.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                    })
                    .AddCookie()
                    .AddOpenIdConnect("oauth2", options =>
                    {
                        options.Authority = jwtConfig["Authority"];
                        options.ClientId = jwtConfig["ClientId"];
                        options.ClientSecret = jwtConfig["ClientSecret"];
                        options.ResponseType = OpenIdConnectResponseType.Code;
                        options.Scope.Clear();
                        foreach (var scope in scopes)
                        {
                            options.Scope.Add(scope);
                        }
                        if (!string.IsNullOrWhiteSpace(jwtConfig["Callback"]))
                            options.CallbackPath = new PathString(jwtConfig["Callback"]);
                        if (!string.IsNullOrWhiteSpace(jwtConfig["ClaimsIssuer"]))
                            options.ClaimsIssuer = jwtConfig["ClaimsIssuer"];
                        options.GetClaimsFromUserInfoEndpoint = true;
                        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                        {
                            NameClaimType = ClaimTypes.NameIdentifier,
                        };
                    });
            }
        }
    }
}
