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
        public FakeDbException(string message) : base(message) { }
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
