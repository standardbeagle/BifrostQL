using BifrostQL.Core.Auth;
using BifrostQL.Core.Model;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Mcp.Test
{
    /// <summary>
    /// MCP tools address tables by BARE name, so a name two schemas both define cannot
    /// be resolved by guessing. The pre-fix FirstOrDefault silently bound the caller's
    /// operation to whichever schema's table enumerated first — with THAT table's
    /// policy/tenant scope — the same silent mis-binding DbModel.GetTableFromDbName
    /// fails fast on. Every ToolJson resolver must answer the ambiguity instead.
    /// </summary>
    public sealed class ToolJsonAmbiguityTests
    {
        private static DbTable Table(string schema, string name, string? policyActions = null)
        {
            var columns = new[]
            {
                new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
            };
            return new DbTable
            {
                DbName = name,
                GraphQlName = name,
                NormalizedName = name,
                TableSchema = schema,
                TableType = "BASE TABLE",
                ColumnLookup = columns.ToDictionary(c => c.DbName, StringComparer.OrdinalIgnoreCase),
                GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
                Metadata = policyActions is null
                    ? new Dictionary<string, object?>()
                    : new Dictionary<string, object?> { [MetadataKeys.Policy.Actions] = policyActions },
            };
        }

        private static IDbModel ModelWithDuplicateName() => new DbModel
        {
            Tables = [Table("dbo", "items"), Table("sales", "items")],
            Metadata = new Dictionary<string, object?>(),
        };

        [Fact]
        public void ResolveTable_RawOverloads_ThrowOnACrossSchemaDuplicateName()
        {
            var model = ModelWithDuplicateName();
            var userContext = new Dictionary<string, object?>();
            var visible = SchemaReadVisibility.Project(model, userContext);

            var viaContext = () => ToolJson.ResolveTable(model, userContext, "items");
            var viaVisible = () => ToolJson.ResolveTable(model, visible, "items");

            viaContext.Should().Throw<ToolPromptException>().WithMessage("*ambiguous*");
            viaVisible.Should().Throw<ToolPromptException>().WithMessage("*ambiguous*");
        }

        [Fact]
        public void ResolveVisibleTable_ThrowsOnACrossSchemaDuplicateName()
        {
            var model = ModelWithDuplicateName();
            var visible = SchemaReadVisibility.Project(model, new Dictionary<string, object?>());

            var act = () => ToolJson.ResolveVisibleTable(model, visible, "items");

            act.Should().Throw<ToolPromptException>().WithMessage("*ambiguous*");
        }

        [Fact]
        public void DuplicateName_WithExactlyOneReadableCandidate_ResolvesToIt()
        {
            // A caller who may read only ONE of the two same-named tables can only mean
            // that one: the tools stay usable, agree across raw and visible resolution,
            // and the refusal path never reveals the hidden twin.
            var readable = Table("dbo", "items");
            var hidden = Table("sales", "items", policyActions: "create"); // read denied
            var model = new DbModel { Tables = [readable, hidden], Metadata = new Dictionary<string, object?>() };
            var userContext = new Dictionary<string, object?>();
            var visible = SchemaReadVisibility.Project(model, userContext);

            ToolJson.ResolveTable(model, userContext, "items").Should().BeSameAs(readable);
            ToolJson.ResolveTable(model, visible, "items").Should().BeSameAs(readable);
            ToolJson.ResolveVisibleTable(model, visible, "items").Table.Should().BeSameAs(readable);
        }

        [Fact]
        public void DuplicateName_WithNoReadableCandidate_AnswersUnknownTable_NotAmbiguous()
        {
            // Zero readable candidates: "ambiguous" would disclose that TWO hidden tables
            // share the name. The caller gets the visibility-scoped unknown-table prompt,
            // indistinguishable from a name that does not exist (invariant 4).
            var model = new DbModel
            {
                Tables = [Table("dbo", "items", policyActions: "create"), Table("sales", "items", policyActions: "create")],
                Metadata = new Dictionary<string, object?>(),
            };
            var userContext = new Dictionary<string, object?>();

            var act = () => ToolJson.ResolveTable(model, userContext, "items");

            act.Should().Throw<ToolPromptException>()
                .WithMessage("*Unknown table*").And.Message.Should().NotContain("ambiguous");
        }

        [Fact]
        public void UniqueBareName_StillResolves()
        {
            var only = Table("dbo", "orders");
            var model = new DbModel { Tables = [only], Metadata = new Dictionary<string, object?>() };
            var visible = SchemaReadVisibility.Project(model, new Dictionary<string, object?>());

            ToolJson.ResolveTable(model, new Dictionary<string, object?>(), "orders").Should().BeSameAs(only);
            ToolJson.ResolveVisibleTable(model, visible, "orders").Table.Should().BeSameAs(only);
        }
    }
}
