using BifrostQL.Core.Model;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Tests for ColumnDto.DeduplicateGraphQlNames which ensures that columns with 
/// duplicate GraphQL names (after normalization) get unique names by appending numeric suffixes.
/// 
/// This fixes issues with WordPress and other databases that may have columns like
/// "wp_comment_id" and "wpCommentId" which both normalize to the same GraphQL name.
/// </summary>
public class ColumnDtoDeduplicationTests
{
    [Fact]
    public void DeduplicateGraphQlNames_NoDuplicates_ReturnsSameColumns()
    {
        var columns = new List<ColumnDto>
        {
            CreateColumn("id", "colid"),
            CreateColumn("name", "colname"),
            CreateColumn("email", "colemail"),
        };

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        result.Should().HaveCount(3);
        result[0].GraphQlName.Should().Be("colid");
        result[1].GraphQlName.Should().Be("colname");
        result[2].GraphQlName.Should().Be("colemail");
    }

    [Fact]
    public void DeduplicateGraphQlNames_TwoDuplicates_SecondGetsSuffix()
    {
        // Simulate two columns that would normalize to the same GraphQL name
        var columns = new List<ColumnDto>
        {
            CreateColumn("wp_comment_id", "colwp_comment_id"),
            CreateColumn("wpCommentId", "colwp_comment_id"), // Same normalized name
        };

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        result.Should().HaveCount(2);
        result[0].GraphQlName.Should().Be("colwp_comment_id");
        result[1].GraphQlName.Should().Be("colwp_comment_id_1");
    }

    [Fact]
    public void DeduplicateGraphQlNames_ThreeDuplicates_GetIncrementalSuffixes()
    {
        var columns = new List<ColumnDto>
        {
            CreateColumn("user_id", "coluser_id"),
            CreateColumn("userId", "coluser_id"),
            CreateColumn("User_Id", "coluser_id"),
        };

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        result.Should().HaveCount(3);
        result[0].GraphQlName.Should().Be("coluser_id");
        result[1].GraphQlName.Should().Be("coluser_id_1");
        result[2].GraphQlName.Should().Be("coluser_id_2");
    }

    [Fact]
    public void DeduplicateGraphQlNames_MixedDuplicatesAndUnique_OnlyDuplicatesGetSuffixes()
    {
        var columns = new List<ColumnDto>
        {
            CreateColumn("id", "colid"),
            CreateColumn("wp_post_id", "colwp_post_id"),
            CreateColumn("wpPostId", "colwp_post_id"), // Duplicate
            CreateColumn("name", "colname"),
        };

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        result.Should().HaveCount(4);
        result[0].GraphQlName.Should().Be("colid");
        result[1].GraphQlName.Should().Be("colwp_post_id");
        result[2].GraphQlName.Should().Be("colwp_post_id_1");
        result[3].GraphQlName.Should().Be("colname");
    }

    [Fact]
    public void DeduplicateGraphQlNames_PreservesAllOtherProperties()
    {
        var original = new ColumnDto
        {
            TableCatalog = "test_db",
            TableSchema = "dbo",
            TableName = "users",
            ColumnName = "wp_comment_id",
            GraphQlName = "colwp_comment_id",
            NormalizedName = "wp_comment",
            DataType = "int",
            IsNullable = false,
            OrdinalPosition = 1,
            IsIdentity = true,
            IsPrimaryKey = true,
        };

        var columns = new List<ColumnDto>
        {
            original,
            CreateColumn("wpCommentId", "colwp_comment_id", tableName: "users"),
        };

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        var deduplicated = result[1];
        deduplicated.TableCatalog.Should().Be("test_db");
        deduplicated.TableSchema.Should().Be("dbo");
        deduplicated.TableName.Should().Be("users");
        deduplicated.ColumnName.Should().Be("wpCommentId");
        deduplicated.DataType.Should().Be("nvarchar");
        deduplicated.IsNullable.Should().BeTrue();
    }

    [Fact]
    public void DeduplicateGraphQlNames_EmptyList_ReturnsEmptyList()
    {
        var columns = new List<ColumnDto>();

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        result.Should().BeEmpty();
    }

    [Fact]
    public void DeduplicateGraphQlNames_SingleColumn_ReturnsSameColumn()
    {
        var columns = new List<ColumnDto>
        {
            CreateColumn("id", "colid"),
        };

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        result.Should().HaveCount(1);
        result[0].GraphQlName.Should().Be("colid");
    }

    [Fact]
    public void DeduplicateGraphQlNames_CaseInsensitiveComparison()
    {
        // GraphQL names should be compared case-insensitively
        var columns = new List<ColumnDto>
        {
            CreateColumn("UserID", "colUserID"),
            CreateColumn("userid", "coluserid"), // Different case but same normalized
        };

        var result = ColumnDto.DeduplicateGraphQlNames(columns);

        result.Should().HaveCount(2);
        result[0].GraphQlName.Should().Be("colUserID");
        result[1].GraphQlName.Should().Be("coluserid_1");
    }

    private static ColumnDto CreateColumn(string columnName, string graphQlName, string? tableName = null)
    {
        return new ColumnDto
        {
            TableCatalog = "test_db",
            TableSchema = "dbo",
            TableName = tableName ?? "test_table",
            ColumnName = columnName,
            GraphQlName = graphQlName,
            NormalizedName = columnName,
            DataType = "nvarchar",
            IsNullable = true,
            OrdinalPosition = 0,
            IsIdentity = false,
            IsPrimaryKey = false,
        };
    }
}
