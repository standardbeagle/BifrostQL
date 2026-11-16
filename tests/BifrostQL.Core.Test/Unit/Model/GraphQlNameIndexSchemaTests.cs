using System.Linq;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// The GraphQL-name index holds two kinds of key: each table's emitted
/// <see cref="IDbTable.GraphQlName"/> (the root field name) and a schema-qualified alias
/// <c>{schema}_{GraphQlName}</c> for tables outside the default schema. An alias must never
/// capture a field another table emits, and the root-field check must answer from the
/// same index the resolver uses.
/// </summary>
public sealed class GraphQlNameIndexSchemaTests
{
    // Each model goes through DbModel.FromTables (the fixture's FK path), so the emitted
    // names are the ones DeduplicateTableGraphQlNames and the schema prefix produce.
    private static IDbModel Build(DbModelTestFixture fixture, string fkSchema, string fkTable) =>
        fixture.WithForeignKey("FK_self", fkSchema, fkTable, new[] { "id" }, fkSchema, fkTable, new[] { "id" })
            .Build();

    private static DbModelTestFixture WithTable(DbModelTestFixture fixture, string schema, string dbName) =>
        fixture.WithTable(dbName, t => t.WithSchema(schema).WithPrimaryKey("id"));

    [Fact]
    public void PostgresDefaultSchema_EmittedRootFieldBeatsAnotherTablesSchemaAlias()
    {
        // a.orders sorts first and its alias is "a_orders", exactly the name
        // public.a_orders emits as its root field.
        var fixture = DbModelTestFixture.Create();
        WithTable(fixture, "a", "orders");
        WithTable(fixture, "public", "a_orders");
        var model = Build(fixture, "a", "orders");

        model.Tables.Select(t => (t.TableSchema, t.GraphQlName)).Should()
            .BeEquivalentTo(new[] { ("a", "orders"), ("public", "a_orders") },
                "the collision is between an emitted name and an alias, which dedupe does not see");

        model.TryGetTableByFullGraphQlName("a_orders", out var byField).Should().BeTrue();
        (byField!.TableSchema, byField.DbName).Should().Be(("public", "a_orders"));
        model.GetTableByFullGraphQlName("a_orders").Should().BeSameAs(byField);
        model.GetTableByFullGraphQlName("orders").TableSchema.Should().Be("a");

        QueryField.IsTableRootField(model, new QueryField { Name = "a_orders" }).Should().BeTrue();
    }

    [Fact]
    public void AliasSharedByTwoTables_ResolvesToNeither()
    {
        // a.b_c and a_b.c both alias to "a_b_c"; logs.audit's alias is unshared (control).
        var fixture = DbModelTestFixture.Create();
        WithTable(fixture, "a", "b_c");
        WithTable(fixture, "a_b", "c");
        WithTable(fixture, "logs", "audit");
        var model = Build(fixture, "a", "b_c");

        model.TryGetTableByFullGraphQlName("a_b_c", out var shared).Should().BeFalse();
        shared.Should().BeNull();
        QueryField.IsTableRootField(model, new QueryField { Name = "a_b_c" }).Should().BeFalse();

        model.TryGetTableByFullGraphQlName("logs_audit", out var audit).Should().BeTrue();
        audit!.TableSchema.Should().Be("logs");
        QueryField.IsTableRootField(model, new QueryField { Name = "logs_audit" }).Should().BeTrue();
    }

    [Fact]
    public void SqlServerDefaultSchema_DboTableKeepsAnUnprefixedFullName()
    {
        var fixture = DbModelTestFixture.Create();
        WithTable(fixture, "dbo", "users");
        WithTable(fixture, "sales", "audit");
        var model = Build(fixture, "dbo", "users");

        var users = (DbTable)model.GetTableByFullGraphQlName("users");
        users.TableSchema.Should().Be("dbo");
        users.FullName.Should().Be("users");
        model.TryGetTableByFullGraphQlName("dbo_users", out _).Should().BeFalse();

        var audit = (DbTable)model.GetTableByFullGraphQlName("sales_audit");
        audit.TableSchema.Should().Be("sales");
        audit.FullName.Should().Be("sales_audit");
    }

    [Fact]
    public void ConfiguredDefaultSchema_GetsNoSchemaAlias()
    {
        // With schema-prefix-default public, public is the default schema: its tables are
        // unprefixed and have no "public_" alias, exactly as dbo tables have no "dbo_" alias.
        var fixture = DbModelTestFixture.Create()
            .WithModelMetadata("schema-prefix", "enabled")
            .WithModelMetadata("schema-prefix-default", "public");
        WithTable(fixture, "public", "orders");
        WithTable(fixture, "a", "items");
        var model = Build(fixture, "public", "orders");

        model.GetTableByFullGraphQlName("orders").TableSchema.Should().Be("public");
        model.TryGetTableByFullGraphQlName("public_orders", out _).Should().BeFalse();
        model.GetTableByFullGraphQlName("a_items").TableSchema.Should().Be("a");
        model.GetTableByFullGraphQlName("a_a_items").TableSchema.Should().Be("a",
            "a non-default table keeps its schema-qualified alias (control)");
    }
}
