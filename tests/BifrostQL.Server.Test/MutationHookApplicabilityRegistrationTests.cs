using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Server.Test;

/// <summary>
/// The registration side of finding H5. <c>AddBifrostQL</c> registers the change-history,
/// approval, deferred-delta and CDC-outbox hooks UNCONDITIONALLY — each no-ops for a table
/// without its metadata — so "is any hook registered?" is true in every host and can never be
/// the question a fast-path gate asks. The gate's real question is per-table applicability,
/// and this test pins it at the tier where the registration happens.
/// </summary>
public sealed class MutationHookApplicabilityRegistrationTests : IAsyncLifetime
{
    private string _connectionString = null!;
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _connectionString = $"Data Source=bifrost_hook_applies_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _keepAlive = new SqliteConnection(_connectionString);
        await _keepAlive.OpenAsync();
        var ddl = new SqliteCommand(
            @"CREATE TABLE Orders (
                Id INTEGER PRIMARY KEY,
                Status TEXT NOT NULL
            );
            CREATE TABLE __history (
                id              INTEGER PRIMARY KEY,
                entity          TEXT NOT NULL,
                entity_id       TEXT NOT NULL,
                op              TEXT NOT NULL,
                actor           TEXT NULL,
                changed_at      TEXT NOT NULL,
                before          TEXT NULL,
                after           TEXT NULL,
                changed_columns TEXT NULL
            );
            CREATE TABLE __outbox (
                id            INTEGER PRIMARY KEY,
                aggregate     TEXT NOT NULL,
                op            TEXT NOT NULL,
                payload       TEXT NOT NULL,
                tenant        TEXT NULL,
                created_at    TEXT NOT NULL DEFAULT (datetime('now')),
                dispatched_at TEXT NULL,
                attempts      INTEGER NOT NULL DEFAULT 0,
                dead          INTEGER NOT NULL DEFAULT 0
            );", _keepAlive);
        await ddl.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private ServiceProvider BuildProductionServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bifrost:DisableAuth"] = "true",
                ["Bifrost:Path"] = "/graphql",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBifrostQL(o => o
            .BindConfiguration(config.GetSection("Bifrost"))
            .BindConnectionString(_connectionString)
            .BindProvider("sqlite"));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Loads the model with <paramref name="metadataRules"/> applied AT LOAD TIME. Every module
    /// config is parsed and cached per table instance during the load, so metadata poked into a
    /// table afterwards would be invisible to the very hooks under test.
    /// </summary>
    private async Task<IDbTable> LoadTableAsync(params string[] metadataRules)
    {
        var model = await new DbModelLoader(
            new SqliteDbConnFactory(_connectionString), new MetadataLoader(metadataRules)).LoadAsync();
        return model.GetTableFromDbName("Orders");
    }

    [Fact]
    public async Task ProductionContainer_RegistersHooks_ButNoneAppliesToAPlainTable()
    {
        await using var provider = BuildProductionServices();
        var table = await LoadTableAsync();

        provider.GetServices<IBeforeCommitMutationHook>().Should().NotBeEmpty(
            "history + approval are registered in every host");
        provider.GetServices<IInTransactionMutationHook>().Should().NotBeEmpty(
            "history + deferred-delta + CDC outbox are registered in every host");

        provider.GetRequiredService<BeforeCommitMutationHooks>().AnyApplies(table).Should().BeFalse();
        provider.GetRequiredService<InTransactionMutationHooks>().AnyApplies(table).Should().BeFalse();
    }

    [Fact]
    public async Task ProductionContainer_HistoryTable_AppliesToBothPhases()
    {
        await using var provider = BuildProductionServices();
        var table = await LoadTableAsync(
            "main.Orders { history: enabled }",
            ":root { history-table: main.__history }");

        provider.GetRequiredService<BeforeCommitMutationHooks>().AnyApplies(table).Should().BeTrue();
        provider.GetRequiredService<InTransactionMutationHooks>().AnyApplies(table).Should().BeTrue();
    }

    [Fact]
    public async Task ProductionContainer_ApprovalTable_AppliesToTheBeforeCommitPhase()
    {
        await using var provider = BuildProductionServices();
        var table = await LoadTableAsync("main.Orders { approval: enabled; approver-role: approver }");

        provider.GetRequiredService<BeforeCommitMutationHooks>().AnyApplies(table).Should().BeTrue();
    }

    [Fact]
    public async Task ProductionContainer_CdcTable_AppliesToTheInTransactionPhase()
    {
        await using var provider = BuildProductionServices();
        var table = await LoadTableAsync(
            "main.Orders { emit-events: insert,update,delete }",
            ":root { outbox-table: main.__outbox }");

        provider.GetRequiredService<InTransactionMutationHooks>().AnyApplies(table).Should().BeTrue();
    }

    /// <summary>
    /// Fail-closed: a hook that does not declare per-table applicability (any third-party
    /// <see cref="IInTransactionMutationHook"/>) keeps the per-row slow path for EVERY table.
    /// The fast path is an optimization; guessing that an unknown hook is inapplicable would
    /// silently skip it.
    /// </summary>
    [Fact]
    public async Task UndeclaredThirdPartyHook_AppliesToEveryTable()
    {
        var table = await LoadTableAsync();
        var hooks = new InTransactionMutationHooks(new IInTransactionMutationHook[] { new UndeclaredHook() });

        hooks.AnyApplies(table).Should().BeTrue();
    }

    private sealed class UndeclaredHook : IInTransactionMutationHook
    {
        public ValueTask AfterWriteInTransactionAsync(MutationObserverContext context) => ValueTask.CompletedTask;
    }
}
