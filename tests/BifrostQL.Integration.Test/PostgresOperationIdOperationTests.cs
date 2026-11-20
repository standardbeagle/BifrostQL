using BifrostQL.Core.Operations;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A mutation server operation is recorded under the request's <c>Idempotency-Key</c> like a
/// generated write, as <c>:root { operation-id: ... }</c> sets: the operation runs once, a retry
/// answers its recorded result (an object included) without running it, the same key with other
/// arguments is refused, a failed run records nothing, and under <c>required</c> an operation
/// without a key never runs. Writes inside a recorded operation count as recorded, so they reach
/// an <c>operation-id: required</c> table.
/// </summary>
public sealed class PostgresOperationIdOperationTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE timers (
            id SERIAL PRIMARY KEY,
            label TEXT NOT NULL CONSTRAINT timers_label_allowed CHECK (label <> 'refused'));
        CREATE TABLE operation_ids (
            tenant TEXT NOT NULL,
            caller TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            field_name TEXT NOT NULL,
            table_name TEXT NOT NULL,
            action TEXT NOT NULL,
            target_key TEXT NOT NULL,
            input_hash TEXT NOT NULL,
            result TEXT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (tenant, caller, operation_id, field_name));
        """;

    private static readonly string[] Metadata =
    {
        ":root { operations: startTimer[writer]; operation-id: required; operation-id-store: public.operation_ids }",
        "public.operation_ids { visibility: hidden }",
        "public.timers { operation-id: required }",
    };

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_operation_id_operation", Ddl, Metadata, bifrost: options => options.AddOperation<StartTimer>());
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private const string Start = """mutation { startTimer(label: "focus") { id label } }""";

    [SkippableFact]
    public async Task RepeatedKey_AnswersTheFirstResult_AndRunsTheOperationOnce()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, first) = await SendAsync(Start, "op-start");
        var (_, retry) = await SendAsync(Start, "op-start");

        PostgresGraphQlHost.AssertNoErrors(first);
        PostgresGraphQlHost.AssertNoErrors(retry);
        first.RootElement.GetProperty("data").GetRawText().Should().Be("""{"startTimer":{"id":1,"label":"focus"}}""");
        retry.RootElement.GetProperty("data").GetRawText().Should().Be(first.RootElement.GetProperty("data").GetRawText());
        (await _host!.CountAsync("SELECT count(*) FROM timers")).Should().Be(1);
        var record = (await _host.RowsAsync("SELECT * FROM operation_ids")).Should().ContainSingle().Subject;
        record["field_name"].Should().Be("startTimer");
        record["table_name"].Should().Be("operation:startTimer");
        record["action"].Should().Be("operation");
    }

    [SkippableFact]
    public async Task ConcurrentRepeats_RunOnce_AndAllAnswerTheFirstResult()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var replies = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => SendAsync(Start, "op-raced")));

        foreach (var (_, reply) in replies)
        {
            PostgresGraphQlHost.AssertNoErrors(reply);
            reply.RootElement.GetProperty("data").GetRawText()
                .Should().Be(replies[0].Body.RootElement.GetProperty("data").GetRawText());
        }
        (await _host!.CountAsync("SELECT count(*) FROM timers")).Should().Be(1);
    }

    [SkippableFact]
    public async Task SameKey_OtherArguments_IsRefusedAsReused()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        PostgresGraphQlHost.AssertNoErrors((await SendAsync(Start, "op-args")).Body);
        var (_, other) = await SendAsync("""mutation { startTimer(label: "other") { id label } }""", "op-args");

        PostgresGraphQlHost.FirstError(other).GetProperty("extensions").GetProperty("code").GetString()
            .Should().Be("OPERATION_ID_REUSED");
        (await _host!.CountAsync("SELECT count(*) FROM timers")).Should().Be(1);
    }

    [SkippableFact]
    public async Task FailedOperation_RecordsNoKey_SoTheRetryRuns()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, failed) = await SendAsync("""mutation { startTimer(label: "refused") { id } }""", "op-fail");
        PostgresGraphQlHost.FirstError(failed);
        (await _host!.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);

        var (_, retry) = await SendAsync("""mutation { startTimer(label: "refused", fix: true) { id } }""", "op-fail");

        PostgresGraphQlHost.AssertNoErrors(retry);
        (await _host.CountAsync("SELECT count(*) FROM timers WHERE label = 'fixed'")).Should().Be(1);
    }

    [SkippableFact]
    public async Task RequiredMode_OperationWithoutAKey_IsRefused_BeforeItRuns()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await SendAsync(Start, key: null);

        PostgresGraphQlHost.FirstError(reply).GetProperty("extensions").GetProperty("code").GetString()
            .Should().Be("OPERATION_ID_REQUIRED");
        // The operation inserts a timer first thing, so no timer means it never ran.
        (await _host!.CountAsync("SELECT count(*) FROM timers")).Should().Be(0);
    }

    private Task<(System.Net.HttpStatusCode Status, System.Text.Json.JsonDocument Body)> SendAsync(string query, string? key)
        => _host!.SendAsync(query, key is null
            ? new[] { ("X-Test-User", "u-1"), ("X-Test-Role", "writer") }
            : new[] { ("X-Test-User", "u-1"), ("X-Test-Role", "writer"), ("Idempotency-Key", key) });

    /// <summary>Inserts a timer and returns it as an object; <c>fix</c> rewrites a refused label.</summary>
    private sealed class StartTimer : IBifrostOperation
    {
        public string Name => "startTimer";
        public BifrostOperationKind Kind => BifrostOperationKind.Mutation;
        public string Signature => "(label: String!, fix: Boolean): Timer";
        public string TypeDefinitions => "type Timer { id: Int! label: String! }";

        public async ValueTask<object?> ExecuteAsync(BifrostOperationContext context)
        {
            var label = context.GetArgument<bool?>("fix") == true ? "fixed" : context.GetArgument<string>("label")!;
            var id = await context.InsertAsync("timers", new Dictionary<string, object?> { ["label"] = label });
            return new Dictionary<string, object?> { ["id"] = id, ["label"] = label };
        }
    }
}
