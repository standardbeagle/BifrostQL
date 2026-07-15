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
using BifrostQL.Core.Resolvers;
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
            // The change-history writer spans BOTH in-transaction phases: it reads the
            // before-image in the before-commit phase and writes the history row in the
            // after-write phase, where the write's result is known. One registration,
            // surfaced under both hook interfaces. It no-ops for tables without history
            // metadata.
            services.AddSingleton<BifrostQL.Core.Modules.History.HistoryMutationHook>();
            services.AddSingleton<IBeforeCommitMutationHook>(sp =>
                sp.GetRequiredService<BifrostQL.Core.Modules.History.HistoryMutationHook>());
            services.AddSingleton<IInTransactionMutationHook>(sp =>
                sp.GetRequiredService<BifrostQL.Core.Modules.History.HistoryMutationHook>());
            // Before-commit veto hooks: built from every registered IBeforeCommitMutationHook.
            services.AddSingleton<BeforeCommitMutationHooks>(sp => new BeforeCommitMutationHooks(
                sp.GetServices<IBeforeCommitMutationHook>().ToArray()));
            // After-write, in-transaction hooks: the CDC transactional-outbox writer is
            // the one built-in — it no-ops for tables without emit-events metadata and
            // writes its event on the mutation's own transaction, after the write, so it
            // can name the generated identity. A host/test may register additional hooks.
            services.AddSingleton<IInTransactionMutationHook, BifrostQL.Core.Modules.Cdc.OutboxMutationHook>();
            services.AddSingleton<InTransactionMutationHooks>(sp => new InTransactionMutationHooks(
                sp.GetServices<IInTransactionMutationHook>().ToArray()));

            // Field-level encryption: compose the envelope key manager from the host's
            // root-key provider + wrapped-DEK store. Resolution is LAZY — the factory
            // reads the dependencies when the manager is first requested, not at
            // AddBifrostQL time — so a host that registers them AFTER AddBifrostQL still
            // gets a working manager. When either is absent the factory returns null, and
            // EncryptOnWriteMutationTransformer fails closed (refuses to write plaintext).
            // We never fabricate a root key or an in-memory store here, since an in-memory
            // store would silently lose DEKs (and make data unrecoverable) on restart.
            services.TryAddSingleton<BifrostQL.Core.Crypto.EnvelopeKeyManager>(sp =>
            {
                var root = sp.GetService<BifrostQL.Core.Crypto.IRootKeyProvider>();
                var store = sp.GetService<BifrostQL.Core.Crypto.IDataEncryptionKeyStore>();
                // Null factory result ⇒ GetService returns null ⇒ the transformer fails
                // closed. (No dependencies configured = encryption not available.)
                return root is not null && store is not null
                    ? new BifrostQL.Core.Crypto.EnvelopeKeyManager(root, store)
                    : null!;
            });
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
        /// Registers the protocol-adapter intent entry points. Both executors resolve
        /// endpoint schemas from the same PathCache the HTTP middleware uses and route
        /// execution through the shared seams (SqlExecutionManager for reads,
        /// TableMutationPipeline for writes), so the security transformer pipelines
        /// (tenant isolation, soft-delete, policy, column read guards; policy/tenant/
        /// audit/concurrency on mutations) apply to intents exactly as they do to
        /// GraphQL requests — adapters have no way around them.
        /// </summary>
        public static void RegisterQueryIntentServices(IServiceCollection services)
        {
            services.AddSingleton<IQueryIntentExecutor>(sp => new QueryIntentExecutor(
                sp.GetRequiredService<PathCache<GraphQL.Inputs>>(),
                sp.GetRequiredService<IQueryTransformerService>(),
                sp.GetService<IQueryObservers>(),
                sp));
            services.AddSingleton<IMutationIntentExecutor>(sp => new MutationIntentExecutor(
                sp.GetRequiredService<PathCache<GraphQL.Inputs>>(),
                sp.GetRequiredService<IMutationTransformers>(),
                sp));
            // Chat module LLM seam. TryAdd so a host can substitute its own provider;
            // lazy singleton so the constructor's fail-fast api-key check fires when a
            // chat feature first resolves it — hosts that never use chat don't need
            // ANTHROPIC_API_KEY configured.
            services.TryAddSingleton<BifrostQL.Core.Modules.Chat.IChatCompletionService>(sp =>
                new BifrostQL.Core.Modules.Chat.AnthropicChatCompletionService(
                    BifrostQL.Core.Modules.Chat.ChatCompletionOptions.FromConfiguration(
                        sp.GetRequiredService<IConfiguration>()),
                    sp.GetService<ILogger<BifrostQL.Core.Modules.Chat.AnthropicChatCompletionService>>()));
        }

        /// <summary>
        /// Registers the built-in <see cref="BifrostQL.Core.Modules.Chat.ExploreChatConnector"/>,
        /// <see cref="BifrostQL.Core.Modules.Chat.MediaChatConnector"/>, and
        /// <see cref="BifrostQL.Core.Modules.Chat.PlanChatConnector"/>
        /// plus the chat-connector types (from <c>AddChatConnector&lt;T&gt;</c>) under
        /// <see cref="BifrostQL.Core.Modules.Chat.IChatConnector"/> and the
        /// <see cref="BifrostQL.Core.Modules.Chat.ChatConnectorRegistry"/> that collects
        /// every registered connector — including any a host registered directly as
        /// <c>IChatConnector</c> services — in priority order. The registry is registered
        /// unconditionally (an empty one is valid: the chat completion simply carries no
        /// tools) so <see cref="BifrostChatMiddleware"/> can always resolve it.
        /// </summary>
        public static void RegisterChatConnectorServices(IServiceCollection services, IReadOnlyList<Type> connectorTypes)
        {
            // The built-in explore and media connectors ship by default (mirroring
            // the built-in transformers): they expose tools only for tables carrying
            // their `chat-connector` type token, so hosts without bindings carry no
            // tools and pay nothing. The factories pin the intent-executor seam
            // explicitly; caps come from ChatConnectorOptions, overridable by
            // registering one first.
            services.TryAddSingleton<BifrostQL.Core.Modules.Chat.ChatConnectorOptions>();
            services.TryAddSingleton(sp => new BifrostQL.Core.Modules.Chat.ExploreChatConnector(
                sp.GetRequiredService<IQueryIntentExecutor>(),
                sp.GetRequiredService<BifrostQL.Core.Modules.Chat.ChatConnectorOptions>()));
            services.TryAddSingleton(sp => new BifrostQL.Core.Modules.Chat.MediaChatConnector(
                sp.GetRequiredService<IQueryIntentExecutor>(),
                sp.GetRequiredService<BifrostQL.Core.Modules.Chat.ChatConnectorOptions>()));
            // The plan connector's confirmation registry is in-process, per-node —
            // like the chat middleware's one-stream-per-conversation guard; a
            // confirmation must reach the node holding the parked stream.
            services.TryAddSingleton<BifrostQL.Core.Modules.Chat.ChatPlanConfirmationRegistry>();
            services.TryAddSingleton(sp => new BifrostQL.Core.Modules.Chat.PlanChatConnector(
                sp.GetRequiredService<IQueryIntentExecutor>(),
                sp.GetRequiredService<IMutationIntentExecutor>(),
                sp.GetRequiredService<BifrostQL.Core.Modules.Chat.ChatPlanConfirmationRegistry>(),
                sp.GetRequiredService<BifrostQL.Core.Modules.Chat.ChatConnectorOptions>(),
                logger: sp.GetService<ILogger<BifrostQL.Core.Modules.Chat.PlanChatConnector>>()));

            // De-duplicated against AddChatConnector<ExploreChatConnector> (or the
            // media/plan connectors): a double registration would define every
            // generated tool twice and fail the registry's collision gate on the
            // first chat request.
            var builtIns = new[]
            {
                typeof(BifrostQL.Core.Modules.Chat.ExploreChatConnector),
                typeof(BifrostQL.Core.Modules.Chat.MediaChatConnector),
                typeof(BifrostQL.Core.Modules.Chat.PlanChatConnector),
            };
            var types = new List<Type>(builtIns);
            types.AddRange(connectorTypes.Where(t => !builtIns.Contains(t)));
            foreach (var connectorType in types)
            {
                var type = connectorType;
                services.TryAddSingleton(type);
                services.AddSingleton(sp =>
                    (BifrostQL.Core.Modules.Chat.IChatConnector)sp.GetRequiredService(type));
            }
            services.AddSingleton(sp => new BifrostQL.Core.Modules.Chat.ChatConnectorRegistry(
                sp.GetServices<BifrostQL.Core.Modules.Chat.IChatConnector>()));
        }

        /// <summary>
        /// Registers the CDC outbox dispatcher (<see cref="BifrostQL.Core.Modules.Cdc.OutboxDispatcher"/>)
        /// and its <see cref="Microsoft.Extensions.Hosting.IHostedService"/> wrapper, so the host
        /// drains the transactional outbox to a registered
        /// <see cref="BifrostQL.Core.Modules.Cdc.IEventSink"/> in the background. The dispatcher
        /// resolves the model/connection from the same <see cref="Core.Schema.PathCache{T}"/> the
        /// GraphQL middleware uses. The <c>IEventSink</c> is optional: a host without one (or
        /// without any <c>outbox-table</c> metadata) starts and idles without polling, so a
        /// non-CDC host pays nothing. TryAdd so a host may register its own dispatcher first.
        /// </summary>
        public static void RegisterCdcDispatcherServices(IServiceCollection services)
        {
            // Opt-in NATS sink: registered BEFORE the webhook sink so that when a NATS URL is
            // configured it wins (TryAdd = first registration wins). The NatsConnection is built
            // LAZILY inside the factory, so a host that configures NO NATS URL opens no connection
            // at startup — the factory returns null! (like the webhook one) and the next
            // TryAddSingleton (webhook) applies. A host configuring NEITHER opens nothing.
            services.TryAddSingleton<BifrostQL.Core.Modules.Cdc.IEventSink>(sp =>
            {
                var url = sp.GetRequiredService<IConfiguration>()[
                    BifrostQL.Core.Modules.Cdc.NatsEventSink.UrlConfigKey];
                if (string.IsNullOrWhiteSpace(url))
                    return null!; // No NATS URL configured: fall through to the webhook registration.

                var connection = new NATS.Client.Core.NatsConnection(
                    new NATS.Client.Core.NatsOpts { Url = url });
                var publisher = new BifrostQL.Core.Modules.Cdc.NatsConnectionPublisher(connection);
                return new BifrostQL.Core.Modules.Cdc.NatsEventSink(
                    publisher,
                    logger: sp.GetService<ILogger<BifrostQL.Core.Modules.Cdc.NatsEventSink>>());
            });

            // Opt-in HTTP webhook sink: registered only when a webhook URL is configured, and
            // TryAdd so a host that registers its OWN IEventSink first always wins. Signing
            // secrets are resolved LAZILY from the model's `webhook-secret` metadata each
            // delivery (via the same PathCache the dispatcher uses), so rotating the
            // comma-separated value takes effect on the next delivery with no restart. Core
            // stays hosting-free: this is pure DI wiring in Server, mirroring the slice-4a
            // dispatcher/hosted-service split.
            services.TryAddSingleton<BifrostQL.Core.Modules.Cdc.IEventSink>(sp =>
            {
                var url = sp.GetRequiredService<IConfiguration>()[
                    BifrostQL.Core.Modules.Cdc.WebhookEventSink.EndpointConfigKey];
                if (string.IsNullOrWhiteSpace(url))
                    return null!; // No endpoint configured: the dispatcher idles (non-CDC host pays nothing).
                if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint))
                    throw new InvalidOperationException(
                        $"'{BifrostQL.Core.Modules.Cdc.WebhookEventSink.EndpointConfigKey}' is not an absolute URL: '{url}'.");

                var pathCache = sp.GetRequiredService<Core.Schema.PathCache<GraphQL.Inputs>>();
                return new BifrostQL.Core.Modules.Cdc.WebhookEventSink(
                    new System.Net.Http.HttpClient(),
                    endpoint,
                    activeSecrets: () =>
                    {
                        var inputs = pathCache.GetFirstValueAsync().GetAwaiter().GetResult();
                        var model = inputs is not null && inputs.TryGetValue("model", out var m)
                            ? m as BifrostQL.Core.Model.IDbModel
                            : null;
                        return BifrostQL.Core.Modules.Cdc.WebhookEventSink.ParseSecrets(
                            model?.GetMetadataValue(BifrostQL.Core.Model.MetadataKeys.Cdc.WebhookSecret));
                    },
                    logger: sp.GetService<ILogger<BifrostQL.Core.Modules.Cdc.WebhookEventSink>>());
            });

            services.TryAddSingleton(sp => new BifrostQL.Core.Modules.Cdc.OutboxDispatcher(
                sp.GetRequiredService<Core.Schema.PathCache<GraphQL.Inputs>>(),
                sp.GetService<BifrostQL.Core.Modules.Cdc.IEventSink>(),
                jitter: null,
                logger: sp.GetService<ILogger<BifrostQL.Core.Modules.Cdc.OutboxDispatcher>>()));
            services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
                new CdcOutboxHostedService(
                    sp.GetRequiredService<BifrostQL.Core.Modules.Cdc.OutboxDispatcher>()));
        }

        /// <summary>
        /// Registers each protocol adapter type as a singleton plus a dedicated
        /// <see cref="Microsoft.Extensions.Hosting.IHostedService"/> wrapper, so the host
        /// starts/stops every adapter with its own lifecycle. Adapter types resolve their
        /// dependencies (IQueryIntentExecutor, IBifrostAuthContextFactory, …) from DI.
        /// </summary>
        public static void RegisterProtocolAdapterServices(IServiceCollection services, IReadOnlyList<Type> adapterTypes)
        {
            foreach (var adapterType in adapterTypes)
            {
                var type = adapterType;
                services.TryAddSingleton(type);
                services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp =>
                    new ProtocolAdapterHostedService((IProtocolAdapter)sp.GetRequiredService(type)));
            }
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
            // Single identity seam for every transport gate (HTTP, binary WebSocket,
            // protocol frontend, workflow endpoints). TryAdd so a host can substitute
            // its own factory before AddBifrostQL runs.
            services.TryAddSingleton<IBifrostAuthContextFactory, BifrostAuthContextFactory>();

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

    /// <summary>
    /// Ties the CDC <see cref="BifrostQL.Core.Modules.Cdc.OutboxDispatcher"/>'s drain loop to
    /// the host lifecycle without the Core engine taking a hosting dependency (mirroring
    /// <see cref="ProtocolAdapterHostedService"/>). The dispatcher's <c>RunAsync</c> is
    /// launched in the background on start and cancelled on graceful shutdown; the drain loop
    /// itself is fail-safe and never throws to the host.
    /// </summary>
    internal sealed class CdcOutboxHostedService : Microsoft.Extensions.Hosting.IHostedService
    {
        private readonly BifrostQL.Core.Modules.Cdc.OutboxDispatcher _dispatcher;
        private readonly CancellationTokenSource _stopping = new();
        private Task? _loop;

        public CdcOutboxHostedService(BifrostQL.Core.Modules.Cdc.OutboxDispatcher dispatcher)
            => _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        public Task StartAsync(CancellationToken cancellationToken)
        {
            // Run the drain loop detached from startup so a slow first poll does not block
            // host start; the loop observes _stopping for graceful shutdown.
            _loop = _dispatcher.RunAsync(_stopping.Token);
            return Task.CompletedTask;
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _stopping.Cancel();
            if (_loop is not null)
            {
                // Await the loop's exit, but do not hang shutdown if it is mid-delay: give up
                // when the host's own shutdown token trips.
                var completed = await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken));
                if (completed == _loop)
                    await _loop; // observe any fault (RunAsync is fail-safe, so this is defensive)
            }
        }
    }
}
