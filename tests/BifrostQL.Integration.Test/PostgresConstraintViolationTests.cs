using System.Text.Json;
using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// A write the database refuses for a constraint reaches the caller with a code per kind of
/// constraint (PostgreSQL SQLSTATE 23505 unique, 23P01 exclusion, 23503 foreign key) and the
/// constraint's name in <c>extensions.constraint</c>, so a client can say which rule it broke.
/// Neither the message nor the extensions carry the statement or the refused value.
/// </summary>
public sealed class PostgresConstraintViolationTests : IAsyncLifetime
{
    // Distinctive values, so a leak of either into the error is visible.
    private const string RoomName = "room-7f3e1c";
    private const int MissingRoom = 990173;

    // The wire contract, spelled out rather than read from the constants that produce it.
    private const string ExclusionMessage = "The change conflicts with an existing record.";
    private const string ForeignKeyMessage = "The change refers to a record that does not exist, or removes one that is still referenced.";

    private const string Ddl =
        $"""
        CREATE TABLE rooms (
            id SERIAL PRIMARY KEY,
            name TEXT NOT NULL CONSTRAINT rooms_name_unique UNIQUE);
        CREATE TABLE bookings (
            id SERIAL PRIMARY KEY,
            room_id INT NOT NULL CONSTRAINT bookings_room_exists REFERENCES rooms (id),
            starts_at INT NOT NULL,
            ends_at INT NOT NULL,
            CONSTRAINT bookings_no_overlap EXCLUDE USING gist (int4range(starts_at, ends_at) WITH &&));
        INSERT INTO rooms (name) VALUES ('{RoomName}');
        INSERT INTO bookings (room_id, starts_at, ends_at) VALUES (1, 10, 20);
        """;

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync("bifrost_constraint_codes", Ddl, Array.Empty<string>());
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task UniqueViolation_IsConflict_NamingTheConstraint()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync($$"""mutation { rooms(insert: { name: "{{RoomName}}" }) }""");

        AssertConstraintError(reply, "CONFLICT", "A conflicting record already exists.", "rooms_name_unique");
        (await _host.CountAsync("SELECT count(*) FROM rooms")).Should().Be(1);
    }

    [SkippableFact]
    public async Task ExclusionViolation_HasItsOwnCode_NamingTheConstraint()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync("""mutation { bookings(insert: { room_id: 1, starts_at: 15, ends_at: 25 }) }""");

        AssertConstraintError(reply, "EXCLUSION_VIOLATION", ExclusionMessage, "bookings_no_overlap");
        (await _host.CountAsync("SELECT count(*) FROM bookings")).Should().Be(1);
    }

    [SkippableFact]
    public async Task ForeignKeyViolation_HasItsOwnCode_NamingTheConstraint()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        var reply = await _host!.PostAsync($$"""mutation { bookings(insert: { room_id: {{MissingRoom}}, starts_at: 40, ends_at: 50 }) }""");

        AssertConstraintError(reply, "FOREIGN_KEY_VIOLATION", ForeignKeyMessage, "bookings_room_exists");
        (await _host.CountAsync("SELECT count(*) FROM bookings")).Should().Be(1);
    }

    /// <summary>
    /// Control for the absence half: a constraint the classifier does not name (a CHECK
    /// violation, SQLSTATE 23514) keeps the generic database error and carries no constraint.
    /// </summary>
    [SkippableFact]
    public async Task OtherIntegrityViolation_StaysDatabaseError_WithNoConstraint()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);
        await using (var conn = new Npgsql.NpgsqlConnection(_host!.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new Npgsql.NpgsqlCommand(
                "ALTER TABLE rooms ADD CONSTRAINT rooms_name_short CHECK (length(name) < 40)", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var reply = await _host.PostAsync($$"""mutation { rooms(insert: { name: "{{new string('x', 50)}}" }) }""");

        var error = PostgresGraphQlHost.FirstError(reply);
        error.GetProperty("extensions").GetProperty("code").GetString().Should().Be("DATABASE_ERROR");
        error.GetProperty("extensions").TryGetProperty("constraint", out _).Should().BeFalse();
    }

    private static void AssertConstraintError(JsonDocument reply, string code, string message, string constraint)
    {
        var error = PostgresGraphQlHost.FirstError(reply);
        error.GetProperty("message").GetString().Should().Be(message);
        var extensions = error.GetProperty("extensions");
        extensions.GetProperty("code").GetString().Should().Be(code);
        extensions.GetProperty("constraint").GetString().Should().Be(constraint);

        var text = error.GetRawText();
        text.Should().NotContain(RoomName, "the refused value stays off the wire");
        text.Should().NotContain(MissingRoom.ToString(System.Globalization.CultureInfo.InvariantCulture), "the refused value stays off the wire");
        text.Should().NotContainAny(new[] { "INSERT", "insert into", "VALUES" }, "the statement stays off the wire");
    }
}
