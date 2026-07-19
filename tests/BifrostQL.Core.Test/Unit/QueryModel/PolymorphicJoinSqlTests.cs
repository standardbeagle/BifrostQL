using BifrostQL.Core.Model;
using BifrostQL.Core.Model.Relationships;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.QueryModel;

/// <summary>
/// SQL-generation tests for polymorphic child joins. The discriminator predicate
/// must be emitted as a parameterized equality against the child table, and a
/// query through one parent must never surface another parent's child rows.
/// </summary>
public sealed class PolymorphicJoinSqlTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    private static IDbModel BuildModel()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("companies", t => t
                .WithPrimaryKey("company_id")
                .WithColumn("name", "nvarchar"))
            .WithTable("contacts", t => t
                .WithPrimaryKey("contact_id")
                .WithColumn("first_name", "nvarchar"))
            .WithTable("notes", t => t
                .WithPrimaryKey("note_id")
                .WithColumn("entity_type", "nvarchar")
                .WithColumn("entity_id", "int")
                .WithColumn("content", "nvarchar")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicTypeCol, "entity_type")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicIdCol, "entity_id")
                .WithMetadata(MetadataKeys.Relationships.PolymorphicMap, "company=companies, contact=contacts"))
            .Build();
        new PolymorphicRelationshipStrategy().DiscoverRelationships(model);
        return model;
    }

    private static (IDictionary<string, ParameterizedSql> sqls, SqlParameterCollection parameters)
        BuildNotesJoinSql(IDbModel model, string parentDbName)
    {
        var parent = model.GetTableFromDbName(parentDbName);
        var notesLink = new GqlObjectQuery
        {
            GraphQlName = "notes",
            ScalarColumns = { new GqlObjectColumn("note_id"), new GqlObjectColumn("content") },
        };
        var query = new GqlObjectQuery
        {
            DbTable = parent,
            TableName = parentDbName,
            GraphQlName = parent.GraphQlName,
            Path = parentDbName,
            ScalarColumns = { new GqlObjectColumn(parent.KeyColumns.First().ColumnName) },
            Links = { notesLink },
        };

        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);
        return (sqls, parameters);
    }

    private static ParameterizedSql NotesJoin(IDictionary<string, ParameterizedSql> sqls)
        => sqls[sqls.Keys.First(k => k.Contains("->notes"))];

    [Fact]
    public void CompaniesNotes_EmitsParameterizedDiscriminator()
    {
        var model = BuildModel();

        var (sqls, parameters) = BuildNotesJoinSql(model, "companies");
        var join = NotesJoin(sqls);

        // The discriminator is filtered on the child (notes), parameterized.
        join.Sql.Should().Contain("[notes]");
        join.Sql.Should().Contain("[entity_type]");
        join.Sql.Should().MatchRegex(@"\[entity_type\]\s*=\s*@");
        // No string literal interpolation of the discriminator value.
        join.Sql.Should().NotContain("'company'");
        parameters.Parameters.Should().Contain(p => Equals(p.Value, "company"));
    }

    [Fact]
    public void ContactsNotes_UsesContactDiscriminator_NotCompany()
    {
        var model = BuildModel();

        var (sqls, parameters) = BuildNotesJoinSql(model, "contacts");
        var join = NotesJoin(sqls);

        join.Sql.Should().Contain("[entity_type]");
        // Cross-type isolation: querying contacts must bind 'contact', never 'company'.
        parameters.Parameters.Should().Contain(p => Equals(p.Value, "contact"));
        parameters.Parameters.Should().NotContain(p => Equals(p.Value, "company"));
    }

    [Fact]
    public void CompaniesNotes_NoChildColumns_EmitsValidProjection()
    {
        // A polymorphic child collection selected with only the relationship and
        // no scalar fields contributes no child columns. The connected projection
        // must not append a trailing comma ("[src_id], FROM" → "Incorrect syntax
        // near ','").
        var model = BuildModel();
        var parent = model.GetTableFromDbName("companies");
        var notesLink = new GqlObjectQuery { GraphQlName = "notes" };
        var query = new GqlObjectQuery
        {
            DbTable = parent,
            TableName = "companies",
            GraphQlName = parent.GraphQlName,
            Path = "companies",
            ScalarColumns = { new GqlObjectColumn(parent.KeyColumns.First().ColumnName) },
            Links = { notesLink },
        };
        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, Dialect, sqls, parameters);
        var join = NotesJoin(sqls);

        // Parse the real generated SQL — the stray comma ("[src_id], FROM") is a
        // syntax error ScriptDom catches directly.
        SqlSyntax.AssertValid(join.Sql, "polymorphic child with no scalar columns");
        join.Sql.Should().Contain("[src_id]");
        join.Sql.Should().Contain("INNER JOIN [notes]");
    }

    [Fact]
    public void CompaniesNotes_JoinsOnParentKeyToEntityId()
    {
        var model = BuildModel();

        var (sqls, _) = BuildNotesJoinSql(model, "companies");
        var join = NotesJoin(sqls);

        // The join still connects the company key to notes.entity_id.
        join.Sql.Should().Contain("[entity_id]");
        join.Sql.Should().Contain("INNER JOIN");
    }
}
