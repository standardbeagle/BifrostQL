using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Resolvers;

/// <summary>
/// CHARACTERIZATION, unit half — the two facts that make the keyed-write SQL facts
/// in <c>Integration/Sqlite/KeyedWriteSeamCharacterizationTests</c> non-vacuous, plus
/// the parameter-namespace contract the emitted statements depend on. Like that
/// file, these pin CURRENT behaviour so CHAR-3..5 cannot change it unseen; no
/// production file changes with them.
/// </summary>
public sealed class KeyedWriteCharacterizationTests
{
    private const string StampColumn = "updated_at";

    private static IDbModel LedgerModel() =>
        DbModelTestFixture.Create()
            .WithTable("ledger", t => t
                .WithSchema("main")
                .WithPrimaryKey("id")
                .WithColumn("id", "int", isPrimaryKey: true)
                .WithColumn("region", "nvarchar", isPrimaryKey: true)
                .WithColumn("status", "nvarchar")
                .WithColumn(StampColumn, "datetime", isNullable: true)
                .WithColumnMetadata(StampColumn, MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedOn))
            .Build();

    /// <summary>
    /// THE NON-VACUITY CONTROL for
    /// <c>PerRow_HardDelete_Where_IsClientColumnsUnionPk_AuditStampNotInWhere</c> and
    /// its batch sibling. Those facts assert that <c>updated_at</c> is ABSENT from a
    /// delete predicate — which is trivially true if nothing ever stamps it. This
    /// fact proves the chain DOES stamp it on a Delete
    /// (<see cref="AuditMutationTransformer"/>, the <c>MutationType.Delete</c> arm),
    /// so a seam that built its predicate from POST-chain data really would carry a
    /// never-matching <c>updated_at = &lt;now&gt;</c> term
    /// (<c>.claude/rules/protocol-adapter-security.md</c> invariant 8(c)).
    /// </summary>
    [Fact]
    public async Task AuditChain_StampsUpdatedAt_OnDelete_SoTheHardDeletePredicateFactIsNotVacuous()
    {
        var model = LedgerModel();
        var table = model.GetTableFromDbName("main", "ledger");
        var chain = new MutationTransformersWrap
        {
            Transformers = new IMutationTransformer[] { new AuditMutationTransformer() },
        };

        // Exactly the payload the hard-delete fact sends.
        var clientData = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = 1, ["region"] = "west", ["status"] = "open",
        };
        var result = await chain.TransformAsync(table, MutationType.Delete, clientData,
            new MutationTransformContext { Model = model, UserContext = new Dictionary<string, object?>() });

        result.Data.Keys.Should().Contain(StampColumn,
            "the audit transformer stamps updated-on for a DELETE, so the absence assertions in the "
            + "keyed-write delete facts are about the SEAM's predicate choice, not about an absent stamp");
        result.Data.Keys.Should().Contain("id").And.Contain("region").And.Contain("status",
            "and the client's own columns survive the chain alongside the stamp");
    }

    /// <summary>
    /// The keyed-write statements bind TWO parameter namespaces onto one command:
    /// sanitized column names for the assignments and key predicate, and the
    /// generated <c>@p0..</c> that <see cref="SqlParameterCollection"/> mints for the
    /// transformer-injected row scope. They must be disjoint. The hazard is a column
    /// LITERALLY NAMED <c>p0</c> — a perfectly legal parameter identifier — so
    /// <see cref="SqlParameterNames.Sanitize"/> RESERVES the generated shape and
    /// hash-suffixes any column that occupies it.
    /// </summary>
    [Fact]
    public async Task KeyedWriteSql_PlaceholderNamespace_IsDisjointFromGeneratedPredicateNames()
    {
        SqlParameterNames.Generated(0).Should().Be("p0");
        SqlParameterNames.IsGeneratedShape("p0").Should().BeTrue();
        SqlParameterNames.IsGeneratedShape("P12").Should().BeTrue("the reservation is case-insensitive");
        SqlParameterNames.IsGeneratedShape("price").Should().BeFalse();

        var sanitized = SqlParameterNames.Sanitize("p0");
        sanitized.Should().NotBe("p0", "a column in the reserved shape must be pushed out of it");
        sanitized.Should().StartWith("p0_");
        SqlParameterNames.Sanitize("sale-price").Should().NotBe("sale_price",
            "an invalid character forces a hash suffix, so two columns cannot sanitize to one name");

        // And the emitted statements really do route both sides through it.
        var model = LedgerModel();
        var table = model.GetTableFromDbName("main", "ledger");
        var dialect = new BifrostQL.Sqlite.SqliteDialect();
        var tableRef = dialect.TableReference(table.TableSchema, table.DbName);

        var update = MutationCommandExecutor.BuildUpdateSql(
            dialect, table, tableRef,
            setColumns: new[] { "status", "p0" },
            keyColumns: new[] { "id", "region" },
            whereSuffix: " AND (\"tenant_id\" = @p0)");
        update.Should().Contain($"\"p0\"=@{sanitized}");
        update.Should().NotContain("\"p0\"=@p0,").And.NotContain("\"p0\"=@p0 ");
        update.Should().Contain("WHERE \"id\"=@id AND \"region\"=@region AND (\"tenant_id\" = @p0);");

        var delete = MutationCommandExecutor.BuildDeleteSql(
            dialect, tableRef,
            whereColumns: new[] { "id", "region", "status" },
            whereSuffix: " AND (\"tenant_id\" = @p0)");
        delete.Should().Be(
            "DELETE FROM \"main\".\"ledger\" WHERE \"id\"=@id AND \"region\"=@region AND \"status\"=@status AND (\"tenant_id\" = @p0);");
        await Task.CompletedTask;
    }
}
