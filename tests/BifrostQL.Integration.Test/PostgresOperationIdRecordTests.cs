using System.Net;
using System.Text.Json;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A write to a table with <c>operation-id: optional</c> that carries a caller operation id
/// (the <c>_operationId</c> argument or the <c>Idempotency-Key</c> header) records the id, the
/// caller's tenant, the table, the verb, the key it addressed, a hash of its arguments and the
/// field's result in the hidden store, on the write's own transaction. A write the database
/// refuses leaves neither a row nor a record; a write without an id records nothing.
/// </summary>
public sealed class PostgresOperationIdRecordTests : IAsyncLifetime
{
    private const string Ddl =
        """
        CREATE TABLE notes (
            id SERIAL PRIMARY KEY,
            title TEXT NOT NULL CONSTRAINT notes_title_allowed CHECK (title <> 'refused'));
        CREATE TABLE plain (
            id SERIAL PRIMARY KEY,
            title TEXT NOT NULL);
        CREATE TABLE operation_ids (
            tenant TEXT NOT NULL,
            caller TEXT NOT NULL DEFAULT '',
            operation_id TEXT NOT NULL,
            table_name TEXT NOT NULL,
            action TEXT NOT NULL,
            target_key TEXT NOT NULL,
            input_hash TEXT NOT NULL,
            result TEXT NULL,
            created_at TIMESTAMPTZ NOT NULL,
            PRIMARY KEY (tenant, caller, operation_id));
        INSERT INTO notes (title) VALUES ('first'), ('second'), ('third');
        """;

    private static readonly string[] Metadata =
    {
        ":root { operation-id-store: public.operation_ids }",
        "public.operation_ids { visibility: hidden }",
        "public.notes { operation-id: optional }",
    };

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync("bifrost_operation_id_record", Ddl, Metadata);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Insert_WithArgument_RecordsTheWriteAndItsGeneratedKey()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await _host!.SendAsync("""mutation { notes(insert: { title: "fourth" }, _operationId: "op-insert") }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var key = reply.RootElement.GetProperty("data").GetProperty("notes").GetRawText();
        key.Should().Be("4");
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE id = 4 AND title = 'fourth'")).Should().Be(1);

        var record = await SingleRecordAsync("op-insert");
        record["tenant"].Should().BeEmpty();
        record["table_name"].Should().Be("public.notes");
        record["action"].Should().Be("insert");
        record["target_key"].Should().Be("{}");
        record["input_hash"].Should().MatchRegex("^[0-9a-f]{64}$");
        record["result"].Should().Be("4");
        record["created_at"].Should().NotBeNull();
    }

    [SkippableFact]
    public async Task Update_WithHeader_RecordsUnderTheCallersTenant()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (status, reply) = await _host!.SendAsync(
            """mutation { notes(update: { id: 1, title: "edited" }) }""",
            ("X-Test-User", "u-1"), ("X-Test-Tenant", "tenant-7"), ("Idempotency-Key", "op-update"));

        status.Should().Be(HttpStatusCode.OK);
        PostgresGraphQlHost.AssertNoErrors(reply);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE id = 1 AND title = 'edited'")).Should().Be(1);

        var record = await SingleRecordAsync("op-update");
        record["tenant"].Should().Be("tenant-7");
        record["action"].Should().Be("update");
        record["target_key"].Should().Be("""{"id":1}""");
        record["result"].Should().Be(reply.RootElement.GetProperty("data").GetProperty("notes").GetRawText());
    }

    [SkippableFact]
    public async Task Upsert_RecordsTheUpsertVerb()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await _host!.SendAsync("""mutation { notes(upsert: { id: 2, title: "upserted" }, _operationId: "op-upsert") }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE id = 2 AND title = 'upserted'")).Should().Be(1);
        var record = await SingleRecordAsync("op-upsert");
        record["action"].Should().Be("upsert");
        record["target_key"].Should().Be("""{"id":2}""");
    }

    [SkippableFact]
    public async Task Delete_RecordsTheKeyItRemoved()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await _host!.SendAsync("""mutation { notes(delete: { id: 3 }, _operationId: "op-delete") }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE id = 3")).Should().Be(0);
        var record = await SingleRecordAsync("op-delete");
        record["action"].Should().Be("delete");
        record["target_key"].Should().Be("""{"id":3}""");
        record["result"].Should().Be(reply.RootElement.GetProperty("data").GetProperty("notes").GetRawText());
    }

    [SkippableFact]
    public async Task Insert_IgnoresPrimaryKeyArgument_WithAnIdAsWithout()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        // An insert never reads _primaryKey, so a stray (here mis-sized) one must not change
        // what an id does: the control runs the same insert without an id.
        var (_, control) = await _host!.SendAsync("""mutation { notes(insert: { title: "pk control" }, _primaryKey: ["1", "2"]) }""");
        PostgresGraphQlHost.AssertNoErrors(control);

        var (_, reply) = await _host.SendAsync("""mutation { notes(insert: { title: "pk ignored" }, _primaryKey: ["1", "2"], _operationId: "op-insert-pk") }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title = 'pk ignored'")).Should().Be(1);
        var record = await SingleRecordAsync("op-insert-pk");
        record["target_key"].Should().Be("{}");
    }

    [SkippableFact]
    public async Task TwoCallersWithoutATenant_KeepSeparateIdSpaces()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        // Neither caller has a tenant, so the tenant alone cannot separate them: one user's
        // id must not block (or, once replay lands, answer) another user's write.
        var (_, first) = await _host!.SendAsync(
            """mutation { notes(insert: { title: "from u-1" }, _operationId: "op-shared") }""", ("X-Test-User", "u-1"));
        var (_, second) = await _host.SendAsync(
            """mutation { notes(insert: { title: "from u-2" }, _operationId: "op-shared") }""", ("X-Test-User", "u-2"));

        PostgresGraphQlHost.AssertNoErrors(first);
        PostgresGraphQlHost.AssertNoErrors(second);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title IN ('from u-1', 'from u-2')")).Should().Be(2);
        var callers = (await _host.RowsAsync("SELECT caller FROM operation_ids WHERE operation_id = 'op-shared' ORDER BY caller"))
            .Select(r => r["caller"]).ToArray();
        callers.Should().Equal("u-1", "u-2");
    }

    [SkippableFact]
    public async Task WriteTheDatabaseRefuses_LeavesNoRecord()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await _host!.SendAsync("""mutation { notes(insert: { title: "refused" }, _operationId: "op-refused") }""");

        PostgresGraphQlHost.FirstError(reply);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title = 'refused'")).Should().Be(0);
        (await _host.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    [SkippableFact]
    public async Task WriteWithoutAnId_RunsAsBefore_AndRecordsNothing()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await _host!.SendAsync("""mutation { notes(insert: { title: "plain write" }) }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title = 'plain write'")).Should().Be(1);
        (await _host.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    [SkippableFact]
    public async Task ArgumentAndHeaderThatDiffer_AreRefused_WithoutWriting()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await _host!.SendAsync(
            """mutation { notes(insert: { title: "two ids" }, _operationId: "op-a") }""",
            ("Idempotency-Key", "op-b"));

        var error = PostgresGraphQlHost.FirstError(reply);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("INVALID_VALUE");
        error.GetProperty("message").GetString().Should().Be("The operation id argument and the Idempotency-Key header carry different values.");
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title = 'two ids'")).Should().Be(0);
        (await _host.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    [SkippableFact]
    public async Task TableThatDoesNotOptIn_HasNoArgument_AndIgnoresTheHeader()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, withArgument) = await _host!.SendAsync("""mutation { plain(insert: { title: "arg" }, _operationId: "op-plain") }""");
        PostgresGraphQlHost.FirstError(withArgument);

        var (_, withHeader) = await _host.SendAsync(
            """mutation { plain(insert: { title: "header" }) }""", ("Idempotency-Key", "op-plain"));
        PostgresGraphQlHost.AssertNoErrors(withHeader);

        (await _host.CountAsync("SELECT count(*) FROM plain WHERE title = 'header'")).Should().Be(1);
        (await _host.CountAsync("SELECT count(*) FROM plain WHERE title = 'arg'")).Should().Be(0);
        (await _host.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    [SkippableFact]
    public async Task RepeatedIdempotencyKeyHeader_IsABadRequest_WithoutWriting()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (status, _) = await _host!.SendAsync(
            """mutation { notes(insert: { title: "repeated header" }) }""",
            ("Idempotency-Key", "op-x"), ("Idempotency-Key", "op-y"));

        status.Should().Be(HttpStatusCode.BadRequest);
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title = 'repeated header'")).Should().Be(0);
        (await _host.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    [SkippableFact]
    public async Task BatchWithAnId_IsRefused_RatherThanRunUnrecorded()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var (_, reply) = await _host!.SendAsync(
            """mutation { notes_batch(actions: [{ insert: { title: "batched" } }], _operationId: "op-batch") }""");

        var error = PostgresGraphQlHost.FirstError(reply);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("OPERATION_ID_UNSUPPORTED");
        (await _host.CountAsync("SELECT count(*) FROM notes WHERE title = 'batched'")).Should().Be(0);
        (await _host.CountAsync("SELECT count(*) FROM operation_ids")).Should().Be(0);
    }

    private async Task<Dictionary<string, string?>> SingleRecordAsync(string operationId)
    {
        var rows = await _host!.RowsAsync("SELECT * FROM operation_ids");
        rows.Should().ContainSingle();
        rows[0]["operation_id"].Should().Be(operationId);
        return rows[0];
    }
}
