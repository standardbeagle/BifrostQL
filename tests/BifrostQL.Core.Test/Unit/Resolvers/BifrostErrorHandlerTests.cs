using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace BifrostQL.Core.Test.Resolvers;

public class BifrostErrorHandlerTests
{
    private readonly BifrostErrorHandler _handler = new();

    [Fact]
    public void HandleError_BifrostExecutionError_ReturnsUserMessage()
    {
        var error = new BifrostExecutionError("Test error message");
        var context = ErrorContext.Connection("TestOperation");

        var result = _handler.HandleError(error, context);

        result.UserMessage.Should().Be("Test error message");
        result.ErrorCode.Should().Be("BIFROST_EXECUTION_ERROR");
    }

    [Fact]
    public void HandleError_BifrostExecutionError_WithConnectionMessage_IncludesSuggestions()
    {
        var error = new BifrostExecutionError("Database connection failed");
        var context = ErrorContext.Connection("TestOperation");

        var result = _handler.HandleError(error, context);

        result.UserMessage.Should().Be("Database connection failed");
        result.SuggestedActions.Should().NotBeNull();
        result.SuggestedActions.Should().Contain("Verify your connection string is correct");
    }

    [Fact]
    public void HandleError_SqlException_LoginFailed_ReturnsCorrectErrorCode()
    {
        // Create a mock SqlException with error number 18456
        var sqlError = CreateSqlException(18456, "Login failed for user 'sa'");
        var context = ErrorContext.Connection("TestConnection");

        var result = _handler.HandleError(sqlError, context);

        result.ErrorCode.Should().Be("DB_LOGIN_FAILED");
        result.UserMessage.Should().Contain("login failed");
        result.SuggestedActions.Should().NotBeNull();
    }

    [Fact]
    public void HandleError_SqlException_DatabaseNotFound_ReturnsCorrectErrorCode()
    {
        var sqlError = CreateSqlException(4060, "Cannot open database");
        var context = ErrorContext.Connection("TestConnection");

        var result = _handler.HandleError(sqlError, context);

        result.ErrorCode.Should().Be("DB_NOT_FOUND");
        result.UserMessage.Should().Contain("Cannot open database");
    }

    [Fact]
    public void HandleError_SqlException_NetworkError_ReturnsCorrectErrorCode()
    {
        var sqlError = CreateSqlException(53, "A network-related error");
        var context = ErrorContext.Connection("TestConnection");

        var result = _handler.HandleError(sqlError, context);

        result.ErrorCode.Should().Be("DB_CONNECTION_FAILED");
        result.SuggestedActions.Should().Contain(s => s.Contains("firewall"));
    }

    [Fact]
    public void HandleError_InvalidOperationException_ReturnsInvalidOperationCode()
    {
        var error = new InvalidOperationException("Invalid operation");
        var context = ErrorContext.Schema("LoadSchema");

        var result = _handler.HandleError(error, context);

        result.ErrorCode.Should().Be("INVALID_OPERATION");
    }

    [Fact]
    public void HandleError_GenericException_ReturnsInternalErrorCode()
    {
        var error = new Exception("Unexpected error");
        var context = ErrorContext.Query("users");

        var result = _handler.HandleError(error, context);

        result.ErrorCode.Should().Be("INTERNAL_ERROR");
        result.UserMessage.Should().Contain("unexpected error");
    }

    [Fact]
    public void BifrostExecutionError_ConnectionFailed_CreatesErrorWithSuggestions()
    {
        var error = BifrostExecutionError.ConnectionFailed("Could not connect");

        error.Message.Should().Be("Database connection failed: Could not connect");
        error.Extensions.Should().NotBeNull();
        error.Extensions.Should().ContainKey("errorCode");
        error.Extensions!["errorCode"].Should().Be("CONNECTION_FAILED");
    }

    [Fact]
    public void BifrostExecutionError_SchemaError_CreatesErrorWithTableName()
    {
        var error = BifrostExecutionError.SchemaError("Table not found", "users");

        error.Message.Should().Be("Schema error: Table not found");
        error.Extensions.Should().ContainKey("tableName");
        error.Extensions!["tableName"].Should().Be("users");
    }

    [Fact]
    public void BifrostExecutionError_QueryError_CreatesErrorWithPath()
    {
        var error = BifrostExecutionError.QueryError("Invalid field", "users.name");

        error.Message.Should().Be("Query error: Invalid field");
        error.ErrorPath.Should().NotBeNull();
        error.ErrorPath.Should().ContainSingle().Which.Should().Be("users.name");
    }

    [Fact]
    public void ErrorContext_Connection_CreatesCorrectContext()
    {
        var context = ErrorContext.Connection("OpenConnection");

        context.Operation.Should().Be("OpenConnection");
        context.TableName.Should().BeNull();
    }

    [Fact]
    public void ErrorContext_Query_CreatesCorrectContext()
    {
        var context = ErrorContext.Query("users", "users.data", "SELECT * FROM users");

        context.Operation.Should().Be("Query");
        context.TableName.Should().Be("users");
        context.QueryPath.Should().Be("users.data");
        context.Sql.Should().Be("SELECT * FROM users");
    }

    [Fact]
    public void ErrorContext_Mutation_CreatesCorrectContext()
    {
        var context = ErrorContext.Mutation("insert", "orders");

        context.Operation.Should().Be("insert");
        context.TableName.Should().Be("orders");
    }

    [Fact]
    public void ErrorContext_Schema_CreatesCorrectContext()
    {
        var context = ErrorContext.Schema("LoadTables", "users");

        context.Operation.Should().Be("LoadTables");
        context.TableName.Should().Be("users");
    }

    private static SqlException CreateSqlException(int number, string message)
    {
        // Use reflection to create a SqlException since it has no public constructors
        var sqlErrorType = typeof(SqlError);
        var sqlError = (SqlError)Activator.CreateInstance(
            sqlErrorType,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object[] { number, (byte)0, (byte)0, "server", message, "proc", 0, null },
            null)!;

        var errorCollectionType = typeof(SqlErrorCollection);
        var errorCollection = (SqlErrorCollection)Activator.CreateInstance(
            errorCollectionType,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            null,
            null)!;

        // Add error to collection using reflection
        var addMethod = errorCollectionType.GetMethod(
            "Add",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        addMethod?.Invoke(errorCollection, new object[] { sqlError });

        var exceptionType = typeof(SqlException);
        var exception = (SqlException)Activator.CreateInstance(
            exceptionType,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new object[] { message, errorCollection, null, Guid.NewGuid() },
            null)!;

        return exception;
    }
}
