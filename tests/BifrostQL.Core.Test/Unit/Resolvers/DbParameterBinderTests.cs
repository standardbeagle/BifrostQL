using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// Covers the GraphQL-name → DB-column-name mapping used by the mutation
/// resolvers so a sanitized/prefixed column (whose GraphQL field name differs
/// from its real column name) lands in the right column instead of emitting the
/// GraphQL name as a column identifier.
/// </summary>
public class DbParameterBinderTests
{
    private static BifrostQL.Core.Model.IDbTable SanitizedTable()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithColumn("user_id", "int", isPrimaryKey: true, graphQlName: "userId")
                .WithColumn("email_address", "nvarchar", graphQlName: "emailAddress"))
            .Build();
        return model.GetTableFromDbName("Users");
    }

    [Fact]
    public void ToDbColumnName_MapsGraphQlFieldToColumn()
    {
        var table = SanitizedTable();
        DbParameterBinder.ToDbColumnName(table, "emailAddress").Should().Be("email_address");
        DbParameterBinder.ToDbColumnName(table, "userId").Should().Be("user_id");
    }

    [Fact]
    public void ToDbColumnName_PassesThroughUnknownKeys()
    {
        var table = SanitizedTable();
        // Transformer-added keys are already DB names and must pass through.
        DbParameterBinder.ToDbColumnName(table, "email_address").Should().Be("email_address");
        DbParameterBinder.ToDbColumnName(table, "tenant_id").Should().Be("tenant_id");
    }

    [Fact]
    public void ToDbColumnKeys_RekeysWholeMap()
    {
        var table = SanitizedTable();
        var input = new Dictionary<string, object?> { ["userId"] = 5, ["emailAddress"] = "a@b.c" };

        var result = DbParameterBinder.ToDbColumnKeys(table, input);

        result.Should().ContainKey("user_id").WhoseValue.Should().Be(5);
        result.Should().ContainKey("email_address").WhoseValue.Should().Be("a@b.c");
        result.Should().NotContainKey("emailAddress");
    }

    [Fact]
    public void IsPrimaryKeyColumn_ResolvesByGraphQlOrDbName()
    {
        var table = SanitizedTable();
        DbParameterBinder.IsPrimaryKeyColumn(table, "userId").Should().BeTrue();
        DbParameterBinder.IsPrimaryKeyColumn(table, "user_id").Should().BeTrue();
        DbParameterBinder.IsPrimaryKeyColumn(table, "emailAddress").Should().BeFalse();
    }
}
