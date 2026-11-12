using System;
using System.Data.Common;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Pins the redaction seam <see cref="BifrostExecutionError.FromDatabaseException"/>
/// and its conflict fingerprinting <see cref="BifrostExecutionError.IsUniqueViolation"/>.
/// The conflict match keys off distinctive driver PHRASES, not bare words: a bare
/// "UNIQUE"/"duplicate" occurs in ordinary error text (a CHECK constraint name, a
/// column/table name, an echoed value), so a single-word test would misclassify
/// unrelated failures as 409-class conflicts.
/// </summary>
public class FromDatabaseExceptionTests
{
    private sealed class FakeDbException : DbException
    {
        private readonly string? _sqlState;

        public FakeDbException(string message, string? sqlState = null) : base(message) => _sqlState = sqlState;

        public override string? SqlState => _sqlState;
    }

    [Theory]
    [InlineData("22P02")] // invalid_text_representation: "not-a-uuid" for a uuid, "x" for an int
    [InlineData("22007")] // invalid_datetime_format
    [InlineData("22008")] // datetime_field_overflow
    [InlineData("22003")] // numeric_value_out_of_range
    public void FromDatabaseException_ReadsABadValue_AsTheCallersMistake(string sqlState)
    {
        var driverText = $"{sqlState}: invalid input syntax for type uuid: \"secret-value\"";

        var error = BifrostExecutionError.FromDatabaseException(
            new InvalidOperationException("wrapper", new FakeDbException(driverText, sqlState)));

        error.ErrorCode.Should().Be(BifrostExecutionError.InvalidValueCode);
        error.Message.Should().NotContain("secret-value").And.NotContain(sqlState);
    }

    [Theory]
    [InlineData("23514")] // check_violation: a constraint kind the classifier does not name
    [InlineData("42883")] // undefined_function, which is how an uncast uuid = text fails
    [InlineData(null)]
    public void FromDatabaseException_LeavesOtherFailures_AsDatabaseErrors(string? sqlState)
    {
        BifrostExecutionError.FromDatabaseException(new FakeDbException("failed", sqlState))
            .ErrorCode.Should().Be("DATABASE_ERROR");
    }

    [Theory]
    [InlineData("23505", BifrostExecutionError.ConflictCode, BifrostExecutionError.ConflictMessage)]
    [InlineData("23P01", BifrostExecutionError.ExclusionViolationCode, BifrostExecutionError.ExclusionViolationMessage)]
    [InlineData("23503", BifrostExecutionError.ForeignKeyViolationCode, BifrostExecutionError.ForeignKeyViolationMessage)]
    public void FromDatabaseException_ConstraintViolation_IsCodedBySqlState_AndNamesTheConstraint(
        string sqlState, string code, string message)
    {
        var driver = new FakeDbException($"{sqlState}: violates \"orders_rule\" Key (email)=(secret@x.io)", sqlState);
        driver.Data["ConstraintName"] = "orders_rule";

        var error = BifrostExecutionError.FromDatabaseException(new InvalidOperationException("wrapper", driver));

        error.ErrorCode.Should().Be(code);
        error.Message.Should().Be(message);
        error.ConstraintName.Should().Be("orders_rule");
    }

    [Fact]
    public void FromDatabaseException_ConstraintViolation_WithNoReportedName_HasNoConstraint()
    {
        var error = BifrostExecutionError.FromDatabaseException(new FakeDbException("failed", "23P01"));

        error.ErrorCode.Should().Be(BifrostExecutionError.ExclusionViolationCode);
        error.ConstraintName.Should().BeNull();
    }

    [Theory]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: users.email'")]     // SQLite
    [InlineData("Violation of UNIQUE KEY constraint 'IX'. Cannot insert duplicate key in object 'dbo.users'.")] // SQL Server
    [InlineData("Duplicate entry 'a@b.com' for key 'users.email'")]              // MySQL
    [InlineData("duplicate key value violates unique constraint \"users_email_key\"")] // PostgreSQL
    [InlineData("ERROR: 23505: duplicate key")]                                   // PostgreSQL SQLSTATE
    public void IsUniqueViolation_MatchesRealDriverPhrases(string message)
    {
        BifrostExecutionError.IsUniqueViolation(new FakeDbException(message)).Should().BeTrue();
    }

    [Theory]
    // A CHECK constraint whose NAME contains "unique" — not a uniqueness violation.
    [InlineData("CHECK constraint 'chk_unique_flag' failed")]
    // A driver echoing a column/value containing the bare word — not a conflict.
    [InlineData("Column 'UNIQUE_CODE' cannot be null")]
    [InlineData("Invalid value 'duplicate' for column 'status'")]
    // An ordinary not-found / syntax error.
    [InlineData("no such table: ghost")]
    public void IsUniqueViolation_DoesNotMatchBareWordsOrUnrelatedFailures(string message)
    {
        BifrostExecutionError.IsUniqueViolation(new FakeDbException(message)).Should().BeFalse(
            "only distinctive driver phrases, never a bare UNIQUE/duplicate substring, mark a conflict");
    }

    [Fact]
    public void IsUniqueViolation_WalksTheInnerExceptionChain()
    {
        var inner = new FakeDbException("UNIQUE constraint failed: t.c");
        var outer = new InvalidOperationException("write failed", inner);

        BifrostExecutionError.IsUniqueViolation(outer).Should().BeTrue();
    }

    [Fact]
    public void FromDatabaseException_UniqueViolation_MapsToStableConflict()
    {
        var ex = new FakeDbException("UNIQUE constraint failed: users.email");

        var result = BifrostExecutionError.FromDatabaseException(ex);

        result.ErrorCode.Should().Be("CONFLICT");
        result.Message.Should().Be(BifrostExecutionError.ConflictMessage);
        result.Message.Should().NotContain("users.email", "the conflict message carries no schema detail");
        result.InnerException.Should().BeSameAs(ex);
    }

    [Fact]
    public void FromDatabaseException_GenericDbError_IsRedacted_ByDefault()
    {
        var ex = new FakeDbException("connection to server at 10.0.0.5 failed: password authentication failed for user 'svc'");

        var result = BifrostExecutionError.FromDatabaseException(ex);

        result.ErrorCode.Should().Be("DATABASE_ERROR");
        result.Message.Should().NotContain("10.0.0.5").And.NotContain("svc",
            "raw driver detail (host, login) must not reach the client by default");
        result.InnerException.Should().BeSameAs(ex, "the original is retained for server-side logging");
    }

    [Fact]
    public void FromDatabaseException_AlreadyBifrostError_PassesThroughUnchanged()
    {
        var authored = new BifrostExecutionError("Tenant scope denied") { ErrorCode = BifrostExecutionError.AccessDeniedCode };

        BifrostExecutionError.FromDatabaseException(authored).Should().BeSameAs(authored);
    }

    [Fact]
    public void FromDatabaseException_RevealsRawText_WhenExposeEnvVarSet()
    {
        // The documented local-debugging escape hatch: BIFROST_EXPOSE_DB_ERRORS=1 puts
        // the raw driver text on the wire. Re-homed here after the dead
        // BifrostErrorHandler (and its test file, which held the only coverage of this
        // path) was deleted — a regression that silently broke the diagnostic reveal
        // would otherwise stay green.
        var previous = Environment.GetEnvironmentVariable(BifrostExecutionError.ExposeDbErrorsEnvVar);
        Environment.SetEnvironmentVariable(BifrostExecutionError.ExposeDbErrorsEnvVar, "1");
        try
        {
            var ex = new FakeDbException("no such table: ghost_42");

            var result = BifrostExecutionError.FromDatabaseException(ex);

            result.Message.Should().Contain("no such table: ghost_42",
                "the opt-in env var reveals the raw driver text for local debugging");
        }
        finally
        {
            Environment.SetEnvironmentVariable(BifrostExecutionError.ExposeDbErrorsEnvVar, previous);
        }
    }
}
