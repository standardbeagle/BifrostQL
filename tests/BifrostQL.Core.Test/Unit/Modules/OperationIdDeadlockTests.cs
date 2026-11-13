using System.Data.Common;
using BifrostQL.Core.Modules.OperationId;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Which claim failures <see cref="OperationIdExecution.IsDeadlock"/> treats as the database
/// choosing the transaction as a deadlock victim, and so retries once. The decision reads the
/// driver's code, never its text: an error that merely mentions a deadlock is not retried, and a
/// victim is retried whatever its message says. The codes are the ones the pinned drivers report
/// for a real deadlock on the test containers: MySqlConnector SqlState <c>40001</c> (error 1213),
/// Npgsql SqlState <c>40P01</c>, and Microsoft.Data.SqlClient, which reports no SqlState, error
/// 1205 in <c>Data["HelpLink.EvtID"]</c>.
/// </summary>
public sealed class OperationIdDeadlockTests
{
    private sealed class DriverError : DbException
    {
        private readonly string? _sqlState;

        public DriverError(string message, string? sqlState) : base(message) => _sqlState = sqlState;

        public override string? SqlState => _sqlState;
    }

    private static DriverError SqlServerError(string message, string eventId)
    {
        var error = new DriverError(message, sqlState: null);
        error.Data["HelpLink.EvtID"] = eventId;
        return error;
    }

    [Theory]
    [InlineData("Deadlock found when trying to get lock; try restarting transaction")]
    [InlineData("deadlock detected")]
    [InlineData("The row was deadlocked by a client-supplied note.")]
    public void Deadlock_text_on_a_non_database_error_is_not_a_deadlock(string message)
    {
        OperationIdExecution.IsDeadlock(new InvalidOperationException(message)).Should().BeFalse(
            "only a driver error can say the database chose this transaction as a victim");
        OperationIdExecution.IsDeadlock(new Exception("wrapped", new InvalidOperationException(message)))
            .Should().BeFalse("an inner non-driver error is not a victim either");
    }

    [Fact]
    public void Deadlock_text_on_a_database_error_with_another_code_is_not_a_deadlock()
    {
        OperationIdExecution.IsDeadlock(new DriverError("deadlock in a unique key", "23505")).Should().BeFalse();
        OperationIdExecution.IsDeadlock(new DriverError("deadlock", sqlState: null)).Should().BeFalse(
            "a driver error with no deadlock code is not retried for its text");
        OperationIdExecution.IsDeadlock(SqlServerError("was deadlocked", "2627")).Should().BeFalse(
            "SQL Server error 2627 is a key violation, not a victim");
    }

    [Theory]
    [InlineData("40001")]
    [InlineData("40P01")]
    public void Database_error_with_a_deadlock_sqlstate_is_a_deadlock_without_deadlock_text(string sqlState)
    {
        var victim = new DriverError("transaction rolled back", sqlState);

        OperationIdExecution.IsDeadlock(victim).Should().BeTrue();
        OperationIdExecution.IsDeadlock(new InvalidOperationException("claim failed", victim)).Should().BeTrue(
            "the victim is found down the inner-exception chain");
    }

    [Fact]
    public void Sql_server_error_1205_is_a_deadlock_without_deadlock_text()
    {
        OperationIdExecution.IsDeadlock(SqlServerError("transaction rolled back", "1205")).Should().BeTrue();
    }
}
