using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Fail-fast validation coverage for the CDC / transactional-outbox metadata
/// contract (slice 1). Once a table opts into <c>emit-events</c>, the model must
/// name an existing <c>outbox-table</c> that carries the full outbox column
/// contract — otherwise the before-commit writer (a later slice) would abort a
/// real production write on the first mutation instead of failing at model load.
/// </summary>
public class CdcMetadataValidationTests
{
    // Builds a fixture table carrying every column of the outbox contract so it
    // passes the column-hole check. Mirrors the documented DDL.
    private static void WithOutboxColumns(DbModelTestFixture.TableBuilder t)
    {
        t.WithSchema("dbo").WithPrimaryKey("id");
        foreach (var col in MetadataKeys.Cdc.OutboxColumns)
        {
            if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase))
                continue; // added as PK above
            t.WithColumn(col, "nvarchar", isNullable: true);
        }
    }

    [Fact]
    public void Validate_EmitEventsWithValidOutbox_DoesNotThrow()
    {
        // Arrange: orders emits events; a __outbox table with the full contract exists.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert,update,delete")
                .WithMetadata(MetadataKeys.Cdc.EventPayload, "changed"))
            .WithTable("__outbox", WithOutboxColumns)
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_EmitEventsWithoutOutboxTable_Throws()
    {
        // Arrange: a table opts in but no model-level outbox-table is configured.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert"))
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Cdc.OutboxTable)
            .And.Contain(MetadataKeys.Cdc.EmitEvents);
    }

    [Fact]
    public void Validate_OutboxTableDoesNotExist_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.__outbox").And.Contain("does not name an existing table");
    }

    [Fact]
    public void Validate_OutboxTableInWrongSchema_Throws_NoNameOnlyFallback()
    {
        // Arrange: outbox-table names dbo.__outbox, but the only __outbox table lives
        // in the 'audit' schema. A name-only fallback would silently bind to it and
        // write events to the wrong schema; fail-fast must reject instead.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert"))
            .WithTable("__outbox", t =>
            {
                t.WithSchema("audit").WithPrimaryKey("id");
                foreach (var col in MetadataKeys.Cdc.OutboxColumns)
                {
                    if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase)) continue;
                    t.WithColumn(col, "nvarchar", isNullable: true);
                }
            })
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.__outbox").And.Contain("does not name an existing table");
    }

    [Fact]
    public void Validate_OutboxTableMissingColumn_ThrowsNamingColumn()
    {
        // Arrange: outbox table exists but is missing the 'dead' dead-letter column.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert"))
            .WithTable("__outbox", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("id");
                foreach (var col in MetadataKeys.Cdc.OutboxColumns)
                {
                    if (string.Equals(col, "id", StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(col, "dead", StringComparison.OrdinalIgnoreCase)) continue; // hole
                    t.WithColumn(col, "nvarchar", isNullable: true);
                }
            })
            .Build();

        // Act
        var act = () => ModelConfigValidator.Validate(model);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("missing required").And.Contain("dead");
    }

    [Fact]
    public void Validate_BadEmitEventsToken_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert,bogus"))
            .WithTable("__outbox", WithOutboxColumns)
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("bogus").And.Contain(MetadataKeys.Cdc.EmitEvents);
    }

    [Fact]
    public void Validate_NoCdcConfigured_OutboxOptional()
    {
        // No table opts in → outbox table is not required and validation passes.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_SubscriptionTablesNamingExistingTable_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithModelMetadata(MetadataKeys.Cdc.SubscriptionTables, "dbo.orders")
            .WithModelMetadata(MetadataKeys.Cdc.SubscriptionTenant, "tenant-x")
            .WithModelMetadata(MetadataKeys.Cdc.SubscriptionRedact, "Total")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert,update,delete"))
            .WithTable("__outbox", WithOutboxColumns)
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnknownSubscriptionKey_Throws()
    {
        // A typo in the subscription-* family must fail the model-load unknown-key gate,
        // not silently no-op the delivery scope.
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithModelMetadata("subscription-tabels", "dbo.orders") // typo
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert"))
            .WithTable("__outbox", WithOutboxColumns)
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("subscription-tabels").And.Contain("unrecognized");
    }

    [Fact]
    public void Validate_SubscriptionTablesNamingMissingTable_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata(MetadataKeys.Cdc.OutboxTable, "dbo.__outbox")
            .WithModelMetadata(MetadataKeys.Cdc.SubscriptionTables, "dbo.orders, dbo.ghosts")
            .WithTable("orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Cdc.EmitEvents, "insert"))
            .WithTable("__outbox", WithOutboxColumns)
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.ghosts").And.Contain("does not name an existing");
    }
}
