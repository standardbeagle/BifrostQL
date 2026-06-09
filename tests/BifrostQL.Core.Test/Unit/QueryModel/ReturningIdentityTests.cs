using BifrostQL.MySql;
using BifrostQL.Ngsql;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests for <see cref="ISqlDialect.ReturningIdentityClauseFor"/> — the table-PK-aware
/// RETURNING clause that lets non-sequence primary keys (uuid, client-supplied) round-trip
/// their real value on insert instead of depending on the session's last-inserted-identity.
/// </summary>
public sealed class ReturningIdentityTests
{
    [Fact]
    public void Postgres_SingleColumnKey_ReturnsRealKeyColumn()
    {
        // A uuid (or any single-column) PK should RETURNING its own column, so the insert
        // never relies on lastval() — which is undefined for non-sequence keys.
        var clause = PostgresDialect.Instance.ReturningIdentityClauseFor(new[] { "company_id" });

        clause.Should().Be(" RETURNING \"company_id\" AS ID");
    }

    [Fact]
    public void Postgres_RespectsTableSpecificKeyName()
    {
        // Proves the clause is built from the actual key column, not a hardcoded `id`
        // (the original assumption that forced the lastval() fallback).
        var clause = PostgresDialect.Instance.ReturningIdentityClauseFor(new[] { "user_uuid" });

        clause.Should().Be(" RETURNING \"user_uuid\" AS ID");
    }

    [Fact]
    public void Postgres_CompositeKey_FallsBackToLastval()
    {
        // A composite key can't project into the single scalar identity the caller reads,
        // so it returns null and the caller falls back to SELECT lastval().
        PostgresDialect.Instance.ReturningIdentityClauseFor(new[] { "a", "b" }).Should().BeNull();
    }

    [Fact]
    public void Postgres_NoKey_FallsBackToLastval()
    {
        PostgresDialect.Instance.ReturningIdentityClauseFor(System.Array.Empty<string>()).Should().BeNull();
    }

    [Fact]
    public void Sqlite_DelegatesToStaticRowidClause()
    {
        // SQLite does not override, so the table-aware call delegates to the static
        // " RETURNING rowid AS ID" — unchanged behavior regardless of key columns.
        var clause = SqliteDialect.Instance.ReturningIdentityClauseFor(new[] { "anything" });

        clause.Should().Be(SqliteDialect.Instance.ReturningIdentityClause);
        clause.Should().Be(" RETURNING rowid AS ID");
    }

    [Fact]
    public void Postgres_StaticClause_StillNull()
    {
        // The table-agnostic property remains null: only the table-aware path opts in.
        PostgresDialect.Instance.ReturningIdentityClause.Should().BeNull();
    }

    [Fact]
    public void MySql_DelegatesToStaticNull()
    {
        // MySQL has no RETURNING; the table-aware call delegates to the null static clause,
        // so the caller keeps using LAST_INSERT_ID().
        MySqlDialect.Instance.ReturningIdentityClauseFor(new[] { "id" }).Should().BeNull();
    }

    [Theory]
    [InlineData("id")]
    [InlineData("company_uuid")]
    public void SqlServer_DelegatesToStaticNull_RegardlessOfKey(string keyColumn)
    {
        // SQL Server keeps its OUTPUT/SCOPE_IDENTITY path: the table-aware call delegates
        // to the null static clause for any key shape (serial or non-serial), so this
        // RETURNING fix does not alter SQL Server behavior.
        SqlServerDialect.Instance
            .ReturningIdentityClauseFor(new[] { keyColumn }).Should().BeNull();
    }
}
