using System.Collections.Concurrent;
using System.Security.Claims;
using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Schema;
using FluentAssertions;
using GraphQL;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The grant resolver and the policy gate's model load are asynchronous, and no projection
/// may block a thread waiting for them. Each fact runs its entry point on a single-threaded
/// <see cref="SynchronizationContext"/> whose one thread is the only place the async work can
/// resume (it awaits <see cref="Task.Yield"/>, which posts its continuation back to that
/// context). An implementation that blocks the thread on the work (<c>.GetAwaiter().GetResult()</c>,
/// <c>.Result</c>, <c>.Wait()</c>) starves its own continuation and never finishes, so the
/// fact fails with <see cref="TimeoutException"/>. An implementation that awaits finishes in
/// microseconds.
/// </summary>
public sealed class NonBlockingAuthProjectionTests
{
    private static readonly TimeSpan DeadlockBudget = TimeSpan.FromSeconds(5);
    private static readonly string[] Grants = { "payments.create", "rates.view" };

    // ---- grant resolver: HTTP gate ----

    [Fact]
    public async Task HttpGate_AsyncGrantResolver_DeliversGrants_WithoutBlockingTheThread()
    {
        var context = new DefaultHttpContext
        {
            User = Principal("user-1"),
            RequestServices = ServicesWithYieldingResolver().BuildServiceProvider(),
        };

        // GetBifrostUserContextAsync is the public HTTP-mount entry; it projects through
        // BifrostIdentityGate.ProjectAsync, the one funnel every HTTP mount uses.
        var userContext = await SingleThreadedContext.RunAsync(() => context.GetBifrostUserContextAsync())
            .WaitAsync(DeadlockBudget);

        PolicyIdentity.ExtractPermissions(userContext).Should().Contain(Grants);
    }

    // ---- grant resolver: raw-wire adapter funnel (pgwire / RESP / LDAP) ----

    [Fact]
    public async Task AdapterProjection_AsyncGrantResolver_DeliversGrants_WithoutBlockingTheThread()
    {
        var root = ServicesWithYieldingResolver()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var userContext = await SingleThreadedContext.RunAsync(() => AdapterIdentityProjection.TryProjectAsync(
                BifrostAuthContextFactory.Instance, root, Principal("user-1"), NullLogger.Instance, "pgwire",
                CancellationToken.None).AsTask())
            .WaitAsync(DeadlockBudget);

        userContext.Should().NotBeNull("the login projects to a real identity");
        PolicyIdentity.ExtractPermissions(userContext!).Should().Contain(Grants);
    }

    // ---- policy gate: cold model load ----

    [Fact]
    public async Task PolicyGateAccessor_ColdModelLoad_BuildsTheGate_WithoutBlockingTheThread()
    {
        var loads = 0;
        var models = new PathCache<Inputs>();
        models.AddLoader("/graphql", async () =>
        {
            await Task.Yield();
            Interlocked.Increment(ref loads);
            return new Inputs(new Dictionary<string, object?> { ["model"] = PaymentsModel() });
        });
        var http = new DefaultHttpContext
        {
            User = Principal("user-1"),
            RequestServices = ServicesWithYieldingResolver().BuildServiceProvider(),
        };
        http.Request.Path = "/payments";
        var accessor = new PolicyGateAccessor(new FixedHttpContextAccessor(http), models, http.RequestServices,
            new ScopeSentinel(), new RootScopeSentinel(http.RequestServices));

        var gate = await SingleThreadedContext.RunAsync(() => accessor.GetAsync().AsTask())
            .WaitAsync(DeadlockBudget);

        gate.Should().NotBeSameAs(PolicyGate.Refused, "an identified caller gets a real gate");
        gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should()
            .BeTrue("the create grant reached the gate through the awaited resolver");
        loads.Should().Be(1, "the model load is cold and runs exactly once");
        (await accessor.GetAsync()).Should().BeSameAs(gate, "the gate is memoized for the request");
        loads.Should().Be(1);
    }

    [Fact]
    public async Task PolicyGateAccessor_AnonymousCaller_GetsRefusedGate_WithoutLoadingTheModel()
    {
        var loads = 0;
        var models = new PathCache<Inputs>();
        models.AddLoader("/graphql", () =>
        {
            Interlocked.Increment(ref loads);
            return Task.FromResult(new Inputs(new Dictionary<string, object?> { ["model"] = PaymentsModel() }));
        });
        var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        var accessor = new PolicyGateAccessor(new FixedHttpContextAccessor(http), models, http.RequestServices,
            new ScopeSentinel(), new RootScopeSentinel(http.RequestServices));

        (await accessor.GetAsync()).Should().BeSameAs(PolicyGate.Refused);
        loads.Should().Be(0, "identity is decided first, and a refused caller never costs a schema load");
    }

    // ---- policy gate: one shared build per request ----

    [Fact]
    public async Task PolicyGateAccessor_ConcurrentCallers_ShareOneBuild()
    {
        // The first caller is held inside the resolver, in the synchronous part of the build,
        // while a second caller asks for the gate. The memo must already be published by then,
        // so the second caller joins that build instead of starting another.
        var calls = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var services = new ServiceCollection().AddBifrostGrantResolver((identity, sp, ct) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.Set();
                release.Wait(DeadlockBudget);
            }
            return new ValueTask<IReadOnlyCollection<string>>(Grants);
        });
        var accessor = AccessorFor(services);

        var first = Task.Run(() => accessor.GetAsync().AsTask());
        entered.Wait(DeadlockBudget).Should().BeTrue("the first caller reaches the resolver");
        var second = accessor.GetAsync().AsTask();
        release.Set();
        var gates = await Task.WhenAll(first, second).WaitAsync(DeadlockBudget);

        calls.Should().Be(1, "concurrent callers in one request share one build");
        gates[1].Should().BeSameAs(gates[0]);
    }

    [Fact]
    public async Task PolicyGateAccessor_OneCallerCancels_TheOtherStillGetsTheGate()
    {
        var grants = new TaskCompletionSource<IReadOnlyCollection<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection().AddBifrostGrantResolver((identity, sp, ct) =>
            new ValueTask<IReadOnlyCollection<string>>(grants.Task));
        var accessor = AccessorFor(services);
        using var cancelling = new CancellationTokenSource();

        var cancelled = accessor.GetAsync(cancelling.Token).AsTask();
        var other = accessor.GetAsync().AsTask();
        cancelling.Cancel();
        grants.SetResult(Grants);

        var gate = await other.WaitAsync(DeadlockBudget);
        gate.CanAct("main.payments", PolicyAction.Create).Allowed.Should()
            .BeTrue("one caller's cancellation must not fault the shared build");
        await FluentActions.Awaiting(() => cancelled.WaitAsync(DeadlockBudget))
            .Should().ThrowAsync<OperationCanceledException>("the cancelling caller abandons its own wait");
    }

    private static PolicyGateAccessor AccessorFor(IServiceCollection services)
    {
        var models = new PathCache<Inputs>();
        models.AddLoader("/graphql", () =>
            Task.FromResult(new Inputs(new Dictionary<string, object?> { ["model"] = PaymentsModel() })));
        var http = new DefaultHttpContext
        {
            User = Principal("user-1"),
            RequestServices = services.BuildServiceProvider(),
        };
        http.Request.Path = "/payments";
        return new PolicyGateAccessor(new FixedHttpContextAccessor(http), models, http.RequestServices,
            new ScopeSentinel(), new RootScopeSentinel(http.RequestServices));
    }

    // ---- fixtures ----

    private static IServiceCollection ServicesWithYieldingResolver()
        => new ServiceCollection().AddBifrostGrantResolver(async (identity, sp, ct) =>
        {
            // Resumes only on the captured single-threaded context: a caller that blocks that
            // thread on this task never lets it finish.
            await Task.Yield();
            return (IReadOnlyCollection<string>)Grants;
        });

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, id), new Claim(ClaimTypes.Role, "clerk") }, "test"));

    private static IDbModel PaymentsModel()
    {
        var table = new DbTable
        {
            DbName = "payments",
            GraphQlName = "payments",
            NormalizedName = "payments",
            TableSchema = "main",
            ColumnLookup = new Dictionary<string, ColumnDto>(),
            GraphQlLookup = new Dictionary<string, ColumnDto>(),
            Metadata = new Dictionary<string, object?>
            {
                [MetadataKeys.Policy.Actions] = "read, create[payments.create]",
            },
        };
        return new DbModel
        {
            Tables = new IDbTable[] { table },
            Metadata = new Dictionary<string, object?>(),
        };
    }

    private sealed class FixedHttpContextAccessor : IHttpContextAccessor
    {
        public FixedHttpContextAccessor(HttpContext context) => HttpContext = context;
        public HttpContext? HttpContext { get; set; }
    }

    /// <summary>
    /// Runs an async function on one dedicated thread that owns a
    /// <see cref="SynchronizationContext"/>: every continuation the function's awaits post is
    /// queued back onto that thread. If that thread blocks, nothing else can drain the queue.
    /// </summary>
    private sealed class SingleThreadedContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state)
            => throw new NotSupportedException("A synchronous Send would deadlock the single thread.");

        public static Task<T> RunAsync<T>(Func<Task<T>> work)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                var context = new SingleThreadedContext();
                SetSynchronizationContext(context);
                context.Post(_ => Start(work, completion, context), null);
                foreach (var (callback, state) in context._queue.GetConsumingEnumerable())
                    callback(state);
            })
            {
                IsBackground = true,
                Name = nameof(SingleThreadedContext),
            };
            thread.Start();
            return completion.Task;
        }

        private static async void Start<T>(Func<Task<T>> work, TaskCompletionSource<T> completion, SingleThreadedContext context)
        {
            try
            {
                completion.SetResult(await work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                context._queue.CompleteAdding();
            }
        }
    }
}
