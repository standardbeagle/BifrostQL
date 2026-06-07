using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Unit tests for <see cref="EnumValueLoader.ResolveValueColumn"/>. The async DB
/// load (<see cref="EnumValueLoader.LoadAsync"/>) is exercised by integration tasks.
/// </summary>
public class EnumValueLoaderTests
{
    private static IDbTable BuildTable(string metaValue) =>
        DbModelTestFixture.Create()
            .WithTable("Status", t => t
                .WithSchema("dbo")
                .WithMetadata(EnumTableConfig.MetadataKey, metaValue)
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Code", "varchar"))
            .Build()
            .GetTableFromDbName("Status");

    [Fact]
    public void ResolveValueColumn_ExplicitColumn_IsReturned()
    {
        // Arrange: enum metadata names an explicit value column.
        var table = BuildTable("Code");

        // Act
        var resolved = EnumValueLoader.ResolveValueColumn(table);

        // Assert
        resolved.Should().Be("Code");
    }

    [Fact]
    public void ResolveValueColumn_Auto_PicksFirstNonPkStringColumn()
    {
        // Arrange: enum: true → auto-detect. PK int is skipped, varchar is chosen.
        var table = BuildTable("true");

        // Act
        var resolved = EnumValueLoader.ResolveValueColumn(table);

        // Assert
        resolved.Should().Be("Code");
    }

    [Fact]
    public void ResolveValueColumn_NotEnumConfigured_ReturnsNull()
    {
        // Arrange: a table with no enum metadata.
        var table = DbModelTestFixture.Create()
            .WithTable("Plain", t => t
                .WithSchema("dbo")
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn("Name", "nvarchar"))
            .Build()
            .GetTableFromDbName("Plain");

        // Act
        var resolved = EnumValueLoader.ResolveValueColumn(table);

        // Assert
        resolved.Should().BeNull();
    }
}
