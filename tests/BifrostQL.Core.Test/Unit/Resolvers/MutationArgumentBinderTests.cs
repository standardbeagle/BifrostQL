using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Direct unit coverage for <see cref="MutationArgumentBinder"/> — the argument
/// key/standard split and _primaryKey resolution that feed the UPDATE/DELETE paths,
/// which previously had only end-to-end integration coverage.
/// </summary>
public sealed class MutationArgumentBinderTests
{
    private static IDbTable Users()
        => DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id", "int")
                .WithColumn("Name", "nvarchar"))
            .Build()
            .GetTableFromDbName("Users");

    private static IDbTable CompositeKey()
        => DbModelTestFixture.Create()
            .WithTable("Enrollment", t => t
                .WithPrimaryKey("school_id", "int")
                .WithPrimaryKey("student_id", "int")
                .WithColumn("grade", "nvarchar"))
            .Build()
            .GetTableFromDbName("Enrollment");

    // --- SplitProperties -----------------------------------------------------

    [Fact]
    public void SplitProperties_SplitsInlinePrimaryKeyFromStandardColumns()
    {
        var (data, keyData, standardData) = MutationArgumentBinder.SplitProperties(
            Users(),
            new Dictionary<string, object?> { ["Id"] = 5, ["Name"] = "Alice" },
            primaryKeyValues: null);

        keyData.Should().ContainKey("Id").WhoseValue.Should().Be(5);
        keyData.Should().NotContainKey("Name");
        standardData.Should().ContainKey("Name").WhoseValue.Should().Be("Alice");
        standardData.Should().NotContainKey("Id");
        data.Should().ContainKeys("Id", "Name");
    }

    [Fact]
    public void SplitProperties_ExplicitPrimaryKeyValues_WinAndKeyByDbColumn()
    {
        // No PK in the data dict — the positional _primaryKey supplies it.
        var (_, keyData, standardData) = MutationArgumentBinder.SplitProperties(
            Users(),
            new Dictionary<string, object?> { ["Name"] = "Bob" },
            primaryKeyValues: new object?[] { 7 });

        keyData.Should().ContainKey("Id").WhoseValue.Should().Be(7);
        standardData.Should().ContainKey("Name");
    }

    // --- ResolvePrimaryKey ---------------------------------------------------

    [Fact]
    public void ResolvePrimaryKey_NullOrEmpty_ReturnsNull()
    {
        MutationArgumentBinder.ResolvePrimaryKey(Users(), null).Should().BeNull();
        MutationArgumentBinder.ResolvePrimaryKey(Users(), System.Array.Empty<object?>()).Should().BeNull();
    }

    [Fact]
    public void ResolvePrimaryKey_Composite_ZipsInDeclaredColumnOrder()
    {
        var result = MutationArgumentBinder.ResolvePrimaryKey(CompositeKey(), new object?[] { 7, 42 });

        result.Should().NotBeNull();
        result!["school_id"].Should().Be(7);
        result["student_id"].Should().Be(42);
    }

    [Fact]
    public void ResolvePrimaryKey_ArityMismatch_Throws()
    {
        var act = () => MutationArgumentBinder.ResolvePrimaryKey(CompositeKey(), new object?[] { 7 });
        act.Should().Throw<BifrostExecutionError>().WithMessage("*expects 2 value(s)*received 1*");
    }

    [Fact]
    public void ResolvePrimaryKey_TableWithoutPrimaryKey_Throws()
    {
        var noPk = DbModelTestFixture.Create()
            .WithTable("Log", t => t.WithColumn("message", "nvarchar"))
            .Build()
            .GetTableFromDbName("Log");

        var act = () => MutationArgumentBinder.ResolvePrimaryKey(noPk, new object?[] { 1 });
        act.Should().Throw<BifrostExecutionError>().WithMessage("*has no primary key*");
    }

    // --- RequireCompleteKey --------------------------------------------------
    //
    // The rule the write pipelines share. The batch upsert probe is guarded by it
    // too and has no other direct coverage, since the GraphQL upsert input types
    // its key columns as required and the mutation-intent surface has no upsert
    // verb — so the contract is pinned here.

    [Fact]
    public void RequireCompleteKey_PartialCompositeKey_Throws()
    {
        var act = () => MutationArgumentBinder.RequireCompleteKey(
            CompositeKey(), new[] { "school_id", "grade" }, "Upsert");

        act.Should().Throw<BifrostExecutionError>()
            .Which.ErrorCode.Should().Be(MutationArgumentBinder.PartialPrimaryKeyCode);
    }

    [Fact]
    public void RequireCompleteKey_RefusalNamesCountsOnly_NeverKeyColumns()
    {
        // The refusal answers callers who may hold no read access to this table, so
        // it must not disclose the model's key-column names.
        var act = () => MutationArgumentBinder.RequireCompleteKey(
            CompositeKey(), new[] { "school_id" }, "Update");

        var message = act.Should().Throw<BifrostExecutionError>().Which.Message;
        message.Should().Contain("supplied 1 primary-key column value(s) but 2 are expected");
        message.Should().NotContain("student_id");
    }

    [Fact]
    public void RequireCompleteKey_CompleteOrAbsentKey_IsAccepted()
    {
        // Complete composite key, complete single-column key, and no key column at
        // all (a filtered delete) all pass; only a PARTIAL key is refused.
        MutationArgumentBinder.RequireCompleteKey(
            CompositeKey(), new[] { "student_id", "school_id", "grade" }, "Update");
        MutationArgumentBinder.RequireCompleteKey(CompositeKey(), new[] { "grade" }, "Delete");
        MutationArgumentBinder.RequireCompleteKey(Users(), new[] { "Id" }, "Update");
    }
}
