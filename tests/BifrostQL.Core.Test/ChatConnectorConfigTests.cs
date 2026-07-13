using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for chat-connector slice 1 — the metadata contract parsed from
/// <c>chat-connector</c> and its media/plan/tool-description mapping keys into a typed
/// <see cref="ChatConnectorConfig"/>. Connectors expose DB tables to the chat LLM as
/// Claude tools (explore = read/query, media = images/files, plan = gated writes), so
/// the parse and its fail-fast behavior on typo'd tokens, stray keys, and empty values
/// are pinned here: a silently ignored connector key would expose the wrong tool — or
/// no tool — with no error.
/// </summary>
public class ChatConnectorConfigTests
{
    /// <summary>
    /// A documents table with a binary image column, a URL column, a string caption
    /// column, and an int column — enough shapes for every connector type.
    /// </summary>
    private static IDbTable ConnectorTable(params (string key, object? value)[] metadata)
        => BuildTable("documents", t =>
        {
            t.WithSchema("dbo").WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("Image", "varbinary")
                .WithColumn("ImageUrl", "nvarchar")
                .WithColumn("Caption", "nvarchar")
                .WithColumn("Size", "int");
            foreach (var (key, value) in metadata)
                t.WithMetadata(key, value);
        });

    private static IDbTable BuildTable(string name, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var model = DbModelTestFixture.Create().WithTable(name, configure).Build();
        return model.GetTableFromDbName(name);
    }

    // ---- opt-in token parsing ----

    [Fact]
    public void FromTable_NoConnectorKeys_ReturnsNone()
    {
        // Arrange
        var table = ConnectorTable();

        // Act
        var config = ChatConnectorConfig.FromTable(table);

        // Assert
        config.IsConnector.Should().BeFalse();
        config.Should().BeSameAs(ChatConnectorConfig.None);
    }

    [Fact]
    public void FromTable_ExploreToken_ParsesExploreOnly()
    {
        // Arrange
        var table = ConnectorTable((MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore));

        // Act
        var config = ChatConnectorConfig.FromTable(table);

        // Assert
        config.IsConnector.Should().BeTrue();
        config.Explore.Should().BeTrue();
        config.Media.Should().BeFalse();
        config.Plan.Should().BeFalse();
    }

    [Fact]
    public void FromTable_AllTypesCombined_ParsesEveryField()
    {
        // Arrange: explore + media + plan on one table, every mapping present. Each
        // parsed field is pinned so a later slice cannot silently drop one.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, "explore, media, plan"),
            (MetadataKeys.ChatConnector.MediaColumn, "Image"),
            (MetadataKeys.ChatConnector.MediaVision, MetadataKeys.Chat.Enabled),
            (MetadataKeys.ChatConnector.MediaCaption, "Caption"),
            (MetadataKeys.ChatConnector.PlanOperations, "insert, update, delete"),
            (MetadataKeys.ChatConnector.ToolDescription, "Company documents with scanned images."));

        // Act
        var config = ChatConnectorConfig.FromTable(table);

        // Assert
        config.IsConnector.Should().BeTrue();
        config.Explore.Should().BeTrue();
        config.Media.Should().BeTrue();
        config.Plan.Should().BeTrue();
        config.MediaColumn.Should().Be("Image");
        config.MediaMode.Should().Be(ChatMediaMode.Binary);
        config.VisionEnabled.Should().BeTrue();
        config.MediaCaptionColumn.Should().Be("Caption");
        config.PlanOperations.Should().BeEquivalentTo(
            new[] { MutationType.Insert, MutationType.Update, MutationType.Delete });
        config.ToolDescription.Should().Be("Company documents with scanned images.");
    }

    [Fact]
    public void FromTable_UnknownTypeToken_Throws()
    {
        // Arrange: 'browse' is not a connector type; silently accepting it would
        // leave the author believing a tool exists when it does not.
        var table = ConnectorTable((MetadataKeys.ChatConnector.Marker, "explore, browse"));

        // Act
        var act = () => ChatConnectorConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'browse'*{MetadataKeys.ChatConnector.Marker}*explore*media*plan*");
    }

    [Fact]
    public void FromTable_EmptyMarkerValue_Throws()
    {
        // Arrange: key present but names no type — the author opted in and got nothing.
        var table = ConnectorTable((MetadataKeys.ChatConnector.Marker, " , "));

        // Act
        var act = () => ChatConnectorConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.Marker}*");
    }

    // ---- media mapping ----

    [Fact]
    public void FromTable_MediaWithoutMediaColumn_Throws()
    {
        // Arrange: a media connector with no column serves nothing.
        var table = ConnectorTable((MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.MediaColumn}*");
    }

    [Fact]
    public void FromTable_MediaColumn_CanonicalizedToDbCasing()
    {
        // Arrange: 'image' spelled in the wrong case; the config must carry the
        // column's database casing so downstream SQL identifiers match.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "image"),
            (MetadataKeys.ChatConnector.MediaCaption, "caption"));

        // Act
        var config = ChatConnectorConfig.FromTable(table);

        // Assert
        config.MediaColumn.Should().Be("Image");
        config.MediaCaptionColumn.Should().Be("Caption");
    }

    [Fact]
    public void FromTable_BinaryMediaColumn_DerivesBinaryMode()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "Image"));

        var config = ChatConnectorConfig.FromTable(table);

        config.MediaMode.Should().Be(ChatMediaMode.Binary);
    }

    [Fact]
    public void FromTable_StringMediaColumn_DerivesUrlMode()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "ImageUrl"));

        var config = ChatConnectorConfig.FromTable(table);

        config.MediaMode.Should().Be(ChatMediaMode.Url);
    }

    [Fact]
    public void FromTable_MediaColumnNotResolvable_KeepsNameAndNullMode()
    {
        // Arrange: the named column does not exist. The parse keeps the name verbatim
        // (no mode) for ModelConfigValidator to report — same division of labor as the
        // chat pair's column mappings.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "Photo"));

        var config = ChatConnectorConfig.FromTable(table);

        config.MediaColumn.Should().Be("Photo");
        config.MediaMode.Should().BeNull();
    }

    [Fact]
    public void FromTable_MediaColumnWithoutMediaToken_Throws()
    {
        // Arrange: chat-media-column on an explore-only connector configures nothing.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore),
            (MetadataKeys.ChatConnector.MediaColumn, "Image"));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.MediaColumn}*{MetadataKeys.ChatConnector.TypeMedia}*");
    }

    [Fact]
    public void FromTable_VisionEnabled_Parses()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "Image"),
            (MetadataKeys.ChatConnector.MediaVision, MetadataKeys.Chat.Enabled));

        var config = ChatConnectorConfig.FromTable(table);

        config.VisionEnabled.Should().BeTrue();
    }

    [Fact]
    public void FromTable_VisionOmitted_DefaultsOff()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "Image"));

        var config = ChatConnectorConfig.FromTable(table);

        config.VisionEnabled.Should().BeFalse();
    }

    [Fact]
    public void FromTable_VisionUnknownValue_Throws()
    {
        // Arrange: 'true' is not the opt-in token; accepting it would make every
        // other truthy spelling appear to work.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "Image"),
            (MetadataKeys.ChatConnector.MediaVision, "true"));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'true'*{MetadataKeys.ChatConnector.MediaVision}*{MetadataKeys.Chat.Enabled}*");
    }

    [Fact]
    public void FromTable_VisionWithoutMediaToken_Throws()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore),
            (MetadataKeys.ChatConnector.MediaVision, MetadataKeys.Chat.Enabled));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.MediaVision}*{MetadataKeys.ChatConnector.TypeMedia}*");
    }

    [Fact]
    public void FromTable_CaptionEmptyValue_Throws()
    {
        // Arrange: present-but-empty is an error, not an omission.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia),
            (MetadataKeys.ChatConnector.MediaColumn, "Image"),
            (MetadataKeys.ChatConnector.MediaCaption, ""));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.MediaCaption}*");
    }

    // ---- plan mapping ----

    [Fact]
    public void FromTable_PlanOperations_ParsesAllowList()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan),
            (MetadataKeys.ChatConnector.PlanOperations, "insert, update"));

        var config = ChatConnectorConfig.FromTable(table);

        config.Plan.Should().BeTrue();
        config.PlanOperations.Should().BeEquivalentTo(new[] { MutationType.Insert, MutationType.Update });
        config.AllowsPlanOperation(MutationType.Insert).Should().BeTrue();
        config.AllowsPlanOperation(MutationType.Update).Should().BeTrue();
    }

    [Fact]
    public void FromTable_PlanDelete_NeverImplied_MustBeListedExplicitly()
    {
        // Arrange: delete is destructive — it is allowed ONLY when spelled out.
        var withoutDelete = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan),
            (MetadataKeys.ChatConnector.PlanOperations, "insert, update"));
        var withDelete = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan),
            (MetadataKeys.ChatConnector.PlanOperations, "delete"));

        // Act + Assert
        ChatConnectorConfig.FromTable(withoutDelete)
            .AllowsPlanOperation(MutationType.Delete).Should().BeFalse();
        ChatConnectorConfig.FromTable(withDelete)
            .AllowsPlanOperation(MutationType.Delete).Should().BeTrue();
    }

    [Fact]
    public void FromTable_PlanWithoutOperations_Throws()
    {
        // Arrange: a plan connector allowing no operation gates nothing — the author
        // opted into writes and got a tool that can never write.
        var table = ConnectorTable((MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.PlanOperations}*");
    }

    [Fact]
    public void FromTable_PlanOperationsEmpty_Throws()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan),
            (MetadataKeys.ChatConnector.PlanOperations, ", ,"));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.PlanOperations}*");
    }

    [Fact]
    public void FromTable_PlanOperationsUnknownOp_Throws()
    {
        // Arrange: 'upsert' is not an operation; silently dropping it would leave the
        // tool narrower (or, worse, a typo'd 'delete' would fail open elsewhere).
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan),
            (MetadataKeys.ChatConnector.PlanOperations, "insert, upsert"));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'upsert'*{MetadataKeys.ChatConnector.PlanOperations}*insert*update*delete*");
    }

    [Fact]
    public void FromTable_PlanOperationsWithoutPlanToken_Throws()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore),
            (MetadataKeys.ChatConnector.PlanOperations, "insert"));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.PlanOperations}*{MetadataKeys.ChatConnector.TypePlan}*");
    }

    // ---- tool description ----

    [Fact]
    public void FromTable_ToolDescription_ValidOnAnyConnectorType()
    {
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore),
            (MetadataKeys.ChatConnector.ToolDescription, "Orders placed by customers."));

        var config = ChatConnectorConfig.FromTable(table);

        config.ToolDescription.Should().Be("Orders placed by customers.");
    }

    [Fact]
    public void FromTable_ToolDescriptionEmpty_Throws()
    {
        // Arrange: present-but-empty would generate a Claude tool with a blank
        // description — worse than the schema-derived default.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore),
            (MetadataKeys.ChatConnector.ToolDescription, "  "));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.ToolDescription}*");
    }

    [Fact]
    public void FromTable_ToolDescriptionWithoutConnector_Throws()
    {
        // Arrange: a stray connector key without chat-connector configures nothing —
        // the author believes the table is a connector and it is not.
        var table = ConnectorTable(
            (MetadataKeys.ChatConnector.ToolDescription, "Orders placed by customers."));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.ToolDescription}*{MetadataKeys.ChatConnector.Marker}*");
    }

    [Fact]
    public void FromTable_MediaColumnWithoutConnector_Throws()
    {
        var table = ConnectorTable((MetadataKeys.ChatConnector.MediaColumn, "Image"));

        var act = () => ChatConnectorConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.ChatConnector.MediaColumn}*{MetadataKeys.ChatConnector.Marker}*");
    }

    // ---- FromModel: collect every connector table ----

    [Fact]
    public void FromModel_CollectsAllConnectorTables()
    {
        // Arrange: two connectors and one plain table.
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore))
            .WithTable("documents", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithColumn("Image", "varbinary")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, "explore, media")
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Image"))
            .WithTable("customers", t => t.WithSchema("dbo").WithPrimaryKey("Id"))
            .Build();

        // Act
        var connectors = ChatConnectorConfig.FromModel(model);

        // Assert
        connectors.Should().HaveCount(2);
        connectors.Select(c => c.Table.DbName).Should().BeEquivalentTo(new[] { "orders", "documents" });
        connectors.Single(c => c.Table.DbName == "documents").Config.Media.Should().BeTrue();
    }

    [Fact]
    public void FromModel_NoConnectors_ReturnsEmpty()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t.WithSchema("dbo").WithPrimaryKey("Id"))
            .Build();

        ChatConnectorConfig.FromModel(model).Should().BeEmpty();
    }

    [Fact]
    public void FromModel_ParseErrorPropagates()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, "browse"))
            .Build();

        var act = () => ChatConnectorConfig.FromModel(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'browse'*");
    }
}
