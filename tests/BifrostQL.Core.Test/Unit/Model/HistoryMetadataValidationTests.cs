using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Fail-fast validation coverage for the temporal-history metadata contract (slice 1).
/// A history-enabled table must resolve to an existing history table — its own
/// <c>history-table</c> override or the model-level shared default — that carries the
/// full history column contract. Otherwise the diff writer (a later slice) would abort
/// a real production write on the first mutation instead of failing at model load.
/// </summary>
public class HistoryMetadataValidationTests
{
    // Builds a fixture table carrying every column of the history contract. Mirrors the
    // documented DDL; the same shape serves a shared or a per-table history table.
    private static void WithHistoryColumns(DbModelTestFixture.TableBuilder t)
    {
        t.WithSchema("dbo").WithPrimaryKey("id");
        foreach (var col in MetadataKeys.History.HistoryColumns)
        {
            if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase))
                continue; // added as PK above
            t.WithColumn(col, "nvarchar", isNullable: true);
        }
    }

    [Fact]
    public void Validate_SharedHistoryTable_DoesNotThrow()
    {
        // Arrange: orders records history; the model-level shared __history table exists.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.History.Enabled, MetadataKeys.History.AllOperations)
                .WithMetadata(MetadataKeys.History.Columns, "Status,Total"))
            .WithTable("__history", WithHistoryColumns)
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_PerTableHistoryTableOverride_DoesNotThrow()
    {
        // Arrange: no model-level default; orders points at its own history table.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update")
                .WithMetadata(MetadataKeys.History.Table, "dbo.orders_history"))
            .WithTable("orders_history", WithHistoryColumns)
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_HistoryWithoutAnyHistoryTable_Throws()
    {
        // Arrange: opted in, but neither the table nor the model names a target.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.History.Table)
            .And.Contain("nowhere to be written");
    }

    [Fact]
    public void Validate_HistoryTableDoesNotExist_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.__history").And.Contain("does not name an existing table");
    }

    [Fact]
    public void Validate_HistoryTableInWrongSchema_Throws_NoNameOnlyFallback()
    {
        // Arrange: history-table names dbo.__history, but the only __history table lives in
        // the 'audit' schema. A name-only fallback would silently record the trail into the
        // wrong schema's table while reporting success.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update"))
            .WithTable("__history", t =>
            {
                t.WithSchema("audit").WithPrimaryKey("id");
                foreach (var col in MetadataKeys.History.HistoryColumns)
                {
                    if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase)) continue;
                    t.WithColumn(col, "nvarchar", isNullable: true);
                }
            })
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.__history").And.Contain("does not name an existing table");
    }

    [Fact]
    public void Validate_HistoryTableMissingColumn_ThrowsNamingColumn()
    {
        // Arrange: history table exists but has no 'changed_columns' column, so the diff
        // writer would fail on the first tracked mutation.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update"))
            .WithTable("__history", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("id");
                foreach (var col in MetadataKeys.History.HistoryColumns)
                {
                    if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(col, "changed_columns", StringComparison.OrdinalIgnoreCase)) continue; // hole
                    t.WithColumn(col, "nvarchar", isNullable: true);
                }
            })
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("missing required").And.Contain("changed_columns");
    }

    [Fact]
    public void Validate_HistoryEnabledTableWithoutPrimaryKey_Throws()
    {
        // Arrange: the table opts into history but has no key column. The writer names
        // every trail row by the full primary key, so at runtime every insert would fail
        // at read-back and every update/delete would be vetoed — the config is unusable
        // and must be rejected at model load.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update"))
            .WithTable("__history", WithHistoryColumns)
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.orders").And.Contain("primary-key");
    }

    [Fact]
    public void Validate_DuplicateHistoryColumns_Throws()
    {
        // Arrange: 'status' is listed twice (differing only in case). The writer projects
        // images into a case-insensitive dictionary keyed by the tracked columns, so the
        // duplicate would crash every recorded write; reject it at model load instead of
        // silently deduplicating a config whose intent is ambiguous.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update")
                .WithMetadata(MetadataKeys.History.Columns, "status,Status"))
            .WithTable("__history", WithHistoryColumns)
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("orders").And.Contain("Status").And.Contain("more than once");
    }

    [Fact]
    public void Validate_HistoryColumnDoesNotExist_Throws()
    {
        // Arrange: history-columns names a column the table does not have; its changes
        // would silently never be recorded.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update")
                .WithMetadata(MetadataKeys.History.Columns, "Status,statuss"))
            .WithTable("__history", WithHistoryColumns)
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("statuss").And.Contain(MetadataKeys.History.Columns);
    }

    [Fact]
    public void Validate_HistoryTableIsTheTrackedTableItself_Throws()
    {
        // Arrange: self-reference — every recorded change would write into the table it tracks.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("id");
                foreach (var col in MetadataKeys.History.HistoryColumns)
                {
                    if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase)) continue;
                    t.WithColumn(col, "nvarchar", isNullable: true);
                }
                t.WithMetadata(MetadataKeys.History.Enabled, "update")
                 .WithMetadata(MetadataKeys.History.Table, "dbo.orders");
            })
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("names the tracked table itself");
    }

    [Fact]
    public void Validate_HistoryTableItselfRecordsHistory_Throws()
    {
        // Arrange: the shared history table is itself history-enabled — writing a history
        // row would record a change of its own, recursing into the trail.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update"))
            .WithTable("__history", t =>
            {
                WithHistoryColumns(t);
                t.WithMetadata(MetadataKeys.History.Enabled, "update");
            })
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("cannot be tracked");
    }

    [Fact]
    public void Validate_HistoryColumnsWithoutHistoryOptIn_Throws()
    {
        // Arrange: history-columns without 'history' records nothing — the author believes
        // the table is tracked and it is not.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Columns, "Status"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.History.Columns)
            .And.Contain($"set without '{MetadataKeys.History.Enabled}'");
    }

    [Fact]
    public void Validate_NoHistoryConfigured_HistoryTableOptional()
    {
        // No table opts in → a history table is not required and validation passes.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }
}
