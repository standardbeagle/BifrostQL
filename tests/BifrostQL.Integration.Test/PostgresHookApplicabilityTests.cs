using System.Collections.Concurrent;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A mutation hook's <c>AppliesTo</c> names the tables it acts on, and the set-based fast paths
/// already skip a hook for a table it does not apply to. The per-row path must agree: a hook is
/// called only for a table its <c>AppliesTo</c> accepts. These write through the production
/// registrar on PostgreSQL, one fact per hook phase, each with the applying table as its control.
/// </summary>
public sealed class PostgresHookApplicabilityTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE hooked (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE other (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
        INSERT INTO hooked (name) VALUES ('seed');
        INSERT INTO other (name) VALUES ('seed');
        """;

    private readonly ConcurrentQueue<string> _calls = new();
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync("bifrost_hook_applies", Ddl, Array.Empty<string>(), services =>
        {
            services.AddSingleton<IBeforeCommitMutationHook>(new HookedOnlyBeforeCommit(_calls));
            services.AddSingleton<IInTransactionMutationHook>(new HookedOnlyInTransaction(_calls));
        });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task BeforeCommitHook_IsNotCalled_ForATableItsAppliesToRefuses()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        PostgresGraphQlHost.AssertNoErrors(await _host!.PostAsync("""mutation { other(insert: { name: "a" }) }"""));
        PostgresGraphQlHost.AssertNoErrors(await _host.PostAsync("""mutation { other(update: { id: 1, name: "b" }) }"""));
        PostgresGraphQlHost.AssertNoErrors(await _host.PostAsync("""mutation { hooked(insert: { name: "c" }) }"""));

        _calls.Where(c => c.StartsWith("before:", StringComparison.Ordinal))
            .Should().Equal(new[] { "before:hooked" }, "only the table AppliesTo accepts reaches the hook");
    }

    [SkippableFact]
    public async Task InTransactionHook_IsNotCalled_ForATableItsAppliesToRefuses()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        PostgresGraphQlHost.AssertNoErrors(await _host!.PostAsync("""mutation { other(insert: { name: "a" }) }"""));
        PostgresGraphQlHost.AssertNoErrors(await _host.PostAsync("""mutation { other(update: { id: 1, name: "b" }) }"""));
        PostgresGraphQlHost.AssertNoErrors(await _host.PostAsync("""mutation { hooked(insert: { name: "c" }) }"""));

        _calls.Where(c => c.StartsWith("after:", StringComparison.Ordinal))
            .Should().Equal(new[] { "after:hooked" }, "only the table AppliesTo accepts reaches the hook");
    }

    private static bool IsHooked(IDbTable table) => string.Equals(table.DbName, "hooked", StringComparison.Ordinal);

    // Each records every call, whatever the table: the body does not repeat AppliesTo, so a
    // call for "other" can only come from a dispatcher that did not ask.
    private sealed class HookedOnlyBeforeCommit(ConcurrentQueue<string> calls) : IBeforeCommitMutationHook
    {
        public bool AppliesTo(IDbTable table) => IsHooked(table);

        public ValueTask<IReadOnlyList<string>> BeforeCommitAsync(MutationObserverContext context)
        {
            calls.Enqueue("before:" + context.Table.DbName);
            return ValueTask.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    private sealed class HookedOnlyInTransaction(ConcurrentQueue<string> calls) : IInTransactionMutationHook
    {
        public bool AppliesTo(IDbTable table) => IsHooked(table);

        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context)
        {
            calls.Enqueue("after:" + context.Table.DbName);
            return ValueTask.CompletedTask;
        }
    }
}
