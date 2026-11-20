using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// Authorization lives in the mutation transformer chain, so an update or a delete must run
/// the chain before the pipeline answers by the request's shape. Otherwise a caller the chain
/// refuses learns the shape answer instead (<c>NO_UPDATE_COLUMNS</c> for a key-only update, a
/// no-op <c>0</c> for a keyless view's update or a delete naming no column), and a denied write
/// reads as one that ran. Every request below is schema-valid; a host transformer refuses the
/// table <c>guarded</c> and the view over it with its own code.
/// </summary>
public sealed class PostgresDenialFirstTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE guarded (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE open_rows (id SERIAL PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE guarded_loose (id SERIAL PRIMARY KEY, note TEXT);
        CREATE TABLE open_loose (id SERIAL PRIMARY KEY, note TEXT);
        CREATE VIEW guarded_view AS SELECT id, name FROM guarded;
        INSERT INTO guarded_loose (note) VALUES ('seed');
        INSERT INTO open_loose (note) VALUES ('seed');
        INSERT INTO guarded (name) VALUES ('seed');
        INSERT INTO open_rows (name) VALUES ('seed');
        """;

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_denial_first", Ddl, Array.Empty<string>(),
            bifrost: options => options.AddMutationTransformers(new IMutationTransformer[] { new GuardedRefusal() }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task DeniedKeyOnlyUpdate_AnswersTheTransformersRefusal_NotNoUpdateColumns()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded(update: { id: 1 }) }""");

        AssertRefused(reply);
        (await _host.CountAsync("SELECT count(*) FROM guarded WHERE name = 'seed'")).Should().Be(1);
    }

    [SkippableFact]
    public async Task DeniedUpdateOfAKeylessView_AnswersTheTransformersRefusal_NotZero()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded_view(update: { name: "changed" }) }""");

        AssertRefused(reply);
        (await _host.CountAsync("SELECT count(*) FROM guarded WHERE name = 'seed'")).Should().Be(1);
    }

    [SkippableFact]
    public async Task DeniedDeleteNamingNoColumn_AnswersTheTransformersRefusal_NotZero()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded_view(delete: { }) }""");

        AssertRefused(reply);
        (await _host.CountAsync("SELECT count(*) FROM guarded")).Should().Be(1);
    }

    [SkippableFact]
    public async Task DeniedBatchUpdateNamingOnlyTheKey_AnswersTheTransformersRefusal()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded_batch(actions: [{ update: { id: 1 } }]) }""");

        AssertRefused(reply);
    }

    [SkippableFact]
    public async Task DeniedEmptyUpsert_AnswersTheTransformersRefusal_NotZero()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded_loose(upsert: { }) }""");

        AssertRefused(reply);
        (await _host.CountAsync("SELECT count(*) FROM guarded")).Should().Be(1);
    }

    [SkippableFact]
    public async Task DeniedKeyOnlyUpsertOfAnExistingRow_AnswersTheTransformersRefusal_NotZero()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded_loose(upsert: { id: 1 }) }""");

        AssertRefused(reply);
        (await _host.CountAsync("SELECT count(*) FROM guarded WHERE name = 'seed'")).Should().Be(1);
    }

    [SkippableFact]
    public async Task DeniedBatchKeyOnlyUpsertOfAnExistingRow_AnswersTheTransformersRefusal()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded_loose_batch(actions: [{ upsert: { id: 1 } }]) }""");

        AssertRefused(reply);
    }

    [SkippableFact]
    public async Task DeniedBatchEmptyUpsert_AnswersTheTransformersRefusal()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { guarded_loose_batch(actions: [{ upsert: { } }]) }""");

        AssertRefused(reply);
    }

    [SkippableFact]
    public async Task AllowedKeyOnlyUpsertOfAnExistingRow_StillAnswersZero()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { open_loose(upsert: { id: 1 }) }""");

        reply.RootElement.TryGetProperty("errors", out _).Should().BeFalse(reply.RootElement.GetRawText());
        reply.RootElement.GetProperty("data").GetProperty("open_loose").GetInt32().Should().Be(0);
    }

    [SkippableFact]
    public async Task AllowedKeyOnlyUpdate_StillAnswersNoUpdateColumns()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { open_rows(update: { id: 1 }) }""");

        var error = PostgresGraphQlHost.FirstError(reply);
        error.GetProperty("extensions").GetProperty("code").GetString()
            .Should().Be(MutationArgumentBinder.NoUpdateColumnsCode);
    }

    private static void AssertRefused(System.Text.Json.JsonDocument reply)
    {
        var error = PostgresGraphQlHost.FirstError(reply);
        error.GetProperty("message").GetString().Should().Be(GuardedRefusal.Message);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be(GuardedRefusal.Code);
    }

    /// <summary>Refuses every update and delete of <c>guarded</c> and its view with its own code.</summary>
    private sealed class GuardedRefusal : IMutationTransformer
    {
        public const string Code = "GUARDED_FORBIDDEN";
        public const string Message = "This table is not writable by you.";

        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType != MutationType.Insert && table.DbName.StartsWith("guarded", StringComparison.Ordinal);

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
            => ValueTask.FromResult(new MutationTransformResult
            {
                MutationType = mutationType,
                Data = data,
                Errors = new[] { Message },
                ErrorCode = Code,
            });
    }
}
