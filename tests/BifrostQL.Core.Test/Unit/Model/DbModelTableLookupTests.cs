using System;
using System.Collections.Generic;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// The table lookups sit on the query hot path. They are now backed by
/// case-insensitive dictionaries built once (replacing a per-call linear scan),
/// and must preserve the previous <c>MatchName</c> semantics: a table is
/// reachable by its bare GraphQL name, its schema-qualified full name, and its
/// raw DB name, all case-insensitively; an unknown name throws.
/// </summary>
public sealed class DbModelTableLookupTests
{
    private static DbTable Table(string dbName, string graphQlName, string schema) => new()
    {
        DbName = dbName,
        GraphQlName = graphQlName,
        NormalizedName = graphQlName,
        TableSchema = schema,
    };

    private static DbModel BuildModel(params DbTable[] tables) => new()
    {
        Tables = tables,
        Metadata = new Dictionary<string, object?>(),
    };

    [Fact]
    public void GetTableByFullGraphQlName_ResolvesBareName_CaseInsensitive()
    {
        var users = Table("Users", "users", "dbo");
        var model = BuildModel(users);

        model.GetTableByFullGraphQlName("users").Should().BeSameAs(users);
        model.GetTableByFullGraphQlName("USERS").Should().BeSameAs(users);
    }

    [Fact]
    public void GetTableByFullGraphQlName_ResolvesSchemaQualifiedFullName()
    {
        // A non-dbo table is reachable by "{schema}_{graphql}" (its FullName) AND
        // its bare GraphQL name, matching the old MatchName's OR semantics.
        var audit = Table("Audit", "audit", "logs");
        var model = BuildModel(audit);

        model.GetTableByFullGraphQlName("logs_audit").Should().BeSameAs(audit);
        model.GetTableByFullGraphQlName("audit").Should().BeSameAs(audit);
    }

    [Fact]
    public void GetTableFromDbName_IsCaseInsensitive()
    {
        var users = Table("Users", "users", "dbo");
        var model = BuildModel(users);

        model.GetTableFromDbName("Users").Should().BeSameAs(users);
        model.GetTableFromDbName("users").Should().BeSameAs(users);
    }

    [Fact]
    public void GetTableByFullGraphQlName_Unknown_Throws()
    {
        var model = BuildModel(Table("Users", "users", "dbo"));

        var act = () => model.GetTableByFullGraphQlName("nope");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetTableFromDbName_Unknown_Throws()
    {
        var model = BuildModel(Table("Users", "users", "dbo"));

        var act = () => model.GetTableFromDbName("nope");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetTableFromDbName_BareName_DefinedInTwoSchemas_FailsFast_NotSilentWrongTable()
    {
        // Two tables share the DbName "orders" across schemas. A bare-name lookup cannot
        // pick the right one; the previous first-wins index silently returned whichever
        // enumerated first, binding the wrong table's columns/policy/write target. It must
        // now fail fast with an actionable, schema-hint error instead.
        var sales = Table("orders", "sales_orders", "sales");
        var archive = Table("orders", "archive_orders", "archive");
        var model = BuildModel(sales, archive);

        model.Invoking(m => m.GetTableFromDbName("orders"))
            .Should().Throw<BifrostExecutionError>()
            .WithMessage("*ambiguous*")
            .Which.Message.Should().Contain("schema");
    }

    [Fact]
    public void GetTableFromDbName_SchemaQualified_ResolvesEachAmbiguousTable()
    {
        var sales = Table("orders", "sales_orders", "sales");
        var archive = Table("orders", "archive_orders", "archive");
        var model = BuildModel(sales, archive);

        model.GetTableFromDbName("sales", "orders").Should().BeSameAs(sales);
        model.GetTableFromDbName("archive", "orders").Should().BeSameAs(archive);
        // Case-insensitive on both schema and name, like the bare lookup.
        model.GetTableFromDbName("ARCHIVE", "ORDERS").Should().BeSameAs(archive);
    }

    [Fact]
    public void GetTableFromDbName_BareName_StillResolvesWhenUnambiguous()
    {
        // A DbName that exists in only one schema is not ambiguous and still resolves by
        // bare name — the common single-schema case is unaffected.
        var sales = Table("orders", "sales_orders", "sales");
        var customers = Table("customers", "customers", "dbo");
        var model = BuildModel(sales, customers);

        model.GetTableFromDbName("orders").Should().BeSameAs(sales);
        model.GetTableFromDbName("customers").Should().BeSameAs(customers);
    }

    [Fact]
    public void GetTableFromDbName_SchemaQualified_Unknown_Throws()
    {
        var model = BuildModel(Table("orders", "orders", "sales"));

        model.Invoking(m => m.GetTableFromDbName("archive", "orders"))
            .Should().Throw<ArgumentOutOfRangeException>();
    }
}
