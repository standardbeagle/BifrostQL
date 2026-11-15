using System;
using System.Data.Common;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Pins the redaction seam <see cref="BifrostExecutionError.FromDatabaseException"/>
/// and its conflict check <see cref="BifrostExecutionError.IsUniqueViolation"/>, which reads
/// each driver's duplicate-key code and never the message: drivers echo the caller's values,
/// so any phrase test misclassifies an unrelated failure as a 409-class conflict. The real
/// engines are pinned by <c>ConstraintViolationClassificationTests</c>.
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

    private static FakeDbException MySqlError(int serverErrorCode, string message = "failed")
    {
        var error = new FakeDbException(message, "23000");
        error.Data["Server Error Code"] = serverErrorCode;
        return error;
    }

    private static FakeDbException SqlServerError(string errorNumber, string message = "failed")
    {
        var error = new FakeDbException(message, sqlState: null);
        error.Data["HelpLink.EvtID"] = errorNumber;
        return error;
    }

    [Fact]
    public void IsUniqueViolation_ReadsEachDriversDuplicateKeyCode_WithoutItsText()
    {
        BifrostExecutionError.IsUniqueViolation(new FakeDbException("failed", "23505")).Should().BeTrue("PostgreSQL 23505");
        BifrostExecutionError.IsUniqueViolation(MySqlError(1062)).Should().BeTrue("MySQL 23000 with error 1062");
        BifrostExecutionError.IsUniqueViolation(SqlServerError("2627")).Should().BeTrue("SQL Server 2627, a constraint");
        BifrostExecutionError.IsUniqueViolation(SqlServerError("2601")).Should().BeTrue("SQL Server 2601, a unique index");
    }

    [Fact]
    public void IsUniqueViolation_RefusesOtherCodesThatShareTheState()
    {
        BifrostExecutionError.IsUniqueViolation(MySqlError(1452, "Duplicate entry")).Should().BeFalse(
            "MySQL reports a foreign-key failure (1452) under the same 23000");
        BifrostExecutionError.IsUniqueViolation(MySqlError(1048)).Should().BeFalse("1048 is a not-null failure");
        BifrostExecutionError.IsUniqueViolation(SqlServerError("547", "Cannot insert duplicate key")).Should().BeFalse(
            "SQL Server 547 is a foreign-key or check failure");
        BifrostExecutionError.IsUniqueViolation(new FakeDbException("23505", "23503")).Should().BeFalse(
            "a foreign-key SqlState is not a conflict, whatever the text says");
    }

    [Theory]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: users.email'")]
    [InlineData("Violation of UNIQUE KEY constraint 'IX'. Cannot insert duplicate key in object 'dbo.users'.")]
    [InlineData("Duplicate entry 'a@b.com' for key 'users.email'")]
    [InlineData("duplicate key value violates unique constraint \"users_email_key\"")]
    [InlineData("ERROR: 23505: duplicate key")]
    public void IsUniqueViolation_IgnoresTheDriverPhrases_WithoutTheCode(string message)
    {
        BifrostExecutionError.IsUniqueViolation(new FakeDbException(message)).Should().BeFalse(
            "drivers echo the caller's values, so the text alone never marks a conflict");
        BifrostExecutionError.IsUniqueViolation(new InvalidOperationException(message)).Should().BeFalse(
            "a non-driver error is never a conflict");
    }

    [Fact]
    public void IsUniqueViolation_WalksTheInnerExceptionChain()
    {
        var outer = new InvalidOperationException("write failed", MySqlError(1062));

        BifrostExecutionError.IsUniqueViolation(outer).Should().BeTrue();
    }

    [Fact]
    public void FromDatabaseException_UniqueViolation_MapsToStableConflict()
    {
        var ex = SqlServerError("2627", "Violation of UNIQUE KEY constraint 'UQ_users_email'.");

        var result = BifrostExecutionError.FromDatabaseException(ex);

        result.ErrorCode.Should().Be("CONFLICT");
        result.Message.Should().Be(BifrostExecutionError.ConflictMessage);
        result.Message.Should().NotContain("UQ_users_email", "the conflict message carries no schema detail");
        result.InnerException.Should().BeSameAs(ex);
    }

    [Fact]
    public void FromDatabaseException_AnEchoedPhrase_StaysTheErrorItIs()
    {
        var echo = new FakeDbException("invalid input syntax for type integer: \"duplicate key\"", "22P02");

        BifrostExecutionError.FromDatabaseException(echo).ErrorCode.Should().Be(BifrostExecutionError.InvalidValueCode,
            "the phrase is the caller's value, so the bad value, not a conflict, is the error");
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
