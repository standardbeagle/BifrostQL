using BifrostQL.Core.Operations;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// <c>:root { mutation-forms: insert, update }</c> on a live host: a client request naming an
/// unlisted form (<c>save</c>, <c>delete</c>, <c>orders_batch</c>) fails GraphQL validation and
/// writes nothing, while a listed form still writes. A server operation's own delete and
/// <c>DeltaAsync</c> are not client forms and still run.
/// </summary>
public sealed class PostgresMutationFormsTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE orders (id SERIAL PRIMARY KEY, total INT NOT NULL);
        INSERT INTO orders (total) VALUES (10), (20);
        """;

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_mutation_forms", Ddl,
            new[] { ":root { mutation-forms: insert, update; operations: purgeOrder[clerk] }" },
            bifrost: options => options.AddOperation<PurgeOrder>());
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task UnlistedSave_FailsValidation_AndWritesNothing()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var save = await _host!.PostAsync("""mutation { orders(save: { total: 77 }) }""", "1");
        var delete = await _host.PostAsync("""mutation { orders(delete: { id: 1 }) }""", "1");
        var batch = await _host.PostAsync("""mutation { orders_batch(actions: [{ insert: { total: 78 } }]) }""", "1");

        Code(save).Should().Be("KNOWN_ARGUMENT_NAMES", save.RootElement.ToString());
        Code(delete).Should().Be("KNOWN_ARGUMENT_NAMES", delete.RootElement.ToString());
        Code(batch).Should().Be("FIELDS_ON_CORRECT_TYPE", batch.RootElement.ToString());
        (await _host.CountAsync("SELECT count(*) FROM orders")).Should().Be(2, "no unlisted form wrote");

        // Control: a listed form on the same host writes.
        var insert = await _host.PostAsync("""mutation { orders(insert: { total: 79 }) }""", "1");
        PostgresGraphQlHost.AssertNoErrors(insert);
        (await _host.CountAsync("SELECT count(*) FROM orders WHERE total = 79")).Should().Be(1);
    }

    [SkippableFact]
    public async Task OperationWrites_AreUnaffected()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { purgeOrder }""", "1", "clerk");

        PostgresGraphQlHost.AssertNoErrors(reply);
        (await _host.CountAsync("SELECT count(*) FROM orders WHERE total = 20")).Should().Be(0,
            "delete is not a client form here, but the operation's server-side delete still runs");
        (await _host.CountAsync("SELECT count(*) FROM orders WHERE total IN (30, 31)")).Should().Be(2,
            "delta is not a client form here, but the operation's DeltaAsync still writes");
    }

    private static string? Code(System.Text.Json.JsonDocument reply) =>
        PostgresGraphQlHost.FirstError(reply).GetProperty("extensions").GetProperty("code").GetString();

    private sealed class PurgeOrder : IBifrostOperation
    {
        public string Name => "purgeOrder";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => ": Int";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            await context.DeleteAsync("orders", new Dictionary<string, object?> { ["id"] = 2 });
            await context.DeltaAsync("orders", new BifrostDelta
            {
                Inserted = new IReadOnlyDictionary<string, object?>[]
                {
                    new Dictionary<string, object?> { ["total"] = 30 },
                    new Dictionary<string, object?> { ["total"] = 31 },
                },
            });
            return 1;
        }
    }
}
