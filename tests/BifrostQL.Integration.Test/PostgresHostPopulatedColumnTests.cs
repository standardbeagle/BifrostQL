using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A <c>NOT NULL</c> column marked <c>populate: host</c> need not be sent: the insert passes
/// GraphQL validation, reaches the host's transformer, and the transformer stamps it. Where no
/// transformer stamps it, the database's constraint refuses the row; the schema keeps the
/// constraint instead of being weakened to nullable.
/// </summary>
public sealed class PostgresHostPopulatedColumnTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE time_entries (id SERIAL PRIMARY KEY, person_id INT NOT NULL, minutes INT NOT NULL);
        CREATE TABLE unstamped (id SERIAL PRIMARY KEY, person_id INT NOT NULL, minutes INT NOT NULL);
        """;

    private static readonly string[] Metadata =
    {
        "public.time_entries.person_id { populate: host }",
        "public.unstamped.person_id { populate: host }",
    };

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_host_populated", Ddl, Metadata,
            bifrost: options => options.AddMutationTransformers(new IMutationTransformer[] { new StampPerson() }));
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task InsertWithoutAHostPopulatedColumn_ReachesTheTransformer_ThatStampsIt()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { time_entries(insert: { minutes: 30 }) }""", "7");

        PostgresGraphQlHost.AssertNoErrors(reply);
        (await _host.CountAsync("SELECT count(*) FROM time_entries WHERE person_id = 7 AND minutes = 30")).Should().Be(1);
    }

    [SkippableFact]
    public async Task InsertWithoutAHostPopulatedColumn_NoTransformerStampsIt_TheConstraintRefusesTheRow()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { unstamped(insert: { minutes: 30 }) }""", "7");

        var error = PostgresGraphQlHost.FirstError(reply);
        // The input type let the column be omitted: the refusal is the database's, not the
        // argument validation's (ARGUMENTS_OF_CORRECT_TYPE, "Missing required field").
        error.GetProperty("extensions").GetProperty("code").GetString().Should().NotBe("ARGUMENTS_OF_CORRECT_TYPE");
        error.GetProperty("message").GetString().Should().NotContain("Missing required field");
        (await _host.CountAsync("SELECT count(*) FROM unstamped")).Should().Be(0);
    }

    /// <summary>Stamps <c>time_entries.person_id</c> from the caller on insert.</summary>
    private sealed class StampPerson : IMutationTransformer
    {
        public int Priority => 200;

        public bool AppliesTo(IDbTable table, MutationType mutationType, MutationTransformContext context)
            => mutationType == MutationType.Insert && table.DbName == "time_entries";

        public ValueTask<MutationTransformResult> TransformAsync(
            IDbTable table, MutationType mutationType, Dictionary<string, object?> data, MutationTransformContext context)
        {
            data["person_id"] = int.Parse(PolicyIdentity.FromUserContext(context.UserContext).Id, System.Globalization.CultureInfo.InvariantCulture);
            return ValueTask.FromResult(new MutationTransformResult { MutationType = mutationType, Data = data });
        }
    }
}
