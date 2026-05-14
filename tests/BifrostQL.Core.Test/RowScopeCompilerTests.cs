using BifrostQL.Core.Auth;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// RED/GREEN TDD coverage for <see cref="RowScopeCompiler"/>, which compiles a
/// table's verbatim row-scope policy expression (sub-task 1) into a
/// <see cref="TableFilter"/> for the query path (sub-task 2).
///
/// Expression grammar (mirrors the example in sub-task 1's collector test
/// <c>"tenant_id = {tenant_id}"</c>): a single <c>column = {context-key}</c>
/// term. The <c>{context-key}</c> placeholder is resolved against the
/// per-request user context.
/// </summary>
public class RowScopeCompilerTests
{
    private static readonly IDictionary<string, object?> EmptyContext =
        new Dictionary<string, object?>();

    [Fact]
    public void Compile_EqualityExpression_BuildsEqualityFilter()
    {
        var context = new Dictionary<string, object?> { ["tenant_id"] = 42 };

        var filter = RowScopeCompiler.Compile(
            "tenant_id = {tenant_id}", "Orders", context);

        filter.TableName.Should().Be("Orders");
        filter.ColumnName.Should().Be("tenant_id");
        filter.Next.Should().NotBeNull();
        filter.Next!.RelationName.Should().Be("_eq");
        filter.Next.Value.Should().Be(42);
    }

    [Fact]
    public void Compile_ToleratesIrregularWhitespace()
    {
        var context = new Dictionary<string, object?> { ["org_id"] = "acme" };

        var filter = RowScopeCompiler.Compile(
            "   department_id   =   { org_id }   ", "Employees", context);

        filter.ColumnName.Should().Be("department_id");
        filter.Next!.Value.Should().Be("acme");
    }

    [Fact]
    public void Compile_MissingContextKey_ThrowsNonLeakingError()
    {
        var ex = Assert.Throws<BifrostExecutionError>(() =>
            RowScopeCompiler.Compile("tenant_id = {tenant_id}", "Orders", EmptyContext));

        // Non-leaking: the message must not echo the column or table name.
        ex.Message.Should().NotContain("tenant_id");
        ex.Message.Should().NotContain("Orders");
    }

    [Fact]
    public void Compile_NullContextValue_ThrowsNonLeakingError()
    {
        var context = new Dictionary<string, object?> { ["tenant_id"] = null };

        var ex = Assert.Throws<BifrostExecutionError>(() =>
            RowScopeCompiler.Compile("tenant_id = {tenant_id}", "Orders", context));

        ex.Message.Should().NotContain("tenant_id");
        ex.Message.Should().NotContain("Orders");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("tenant_id")]
    [InlineData("tenant_id = tenant_id")]
    [InlineData("tenant_id = {tenant_id")]
    [InlineData("tenant_id == {tenant_id}")]
    [InlineData("= {tenant_id}")]
    [InlineData("tenant_id = {}")]
    public void Compile_MalformedExpression_ThrowsNonLeakingError(string expression)
    {
        var context = new Dictionary<string, object?> { ["tenant_id"] = 1 };

        var ex = Assert.Throws<BifrostExecutionError>(() =>
            RowScopeCompiler.Compile(expression, "Orders", context));

        // A malformed policy must fail closed and must not leak the table name.
        ex.Message.Should().NotContain("Orders");
    }
}
