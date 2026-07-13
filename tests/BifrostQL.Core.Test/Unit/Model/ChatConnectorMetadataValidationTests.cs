using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Fail-fast validation coverage for the chat-connector metadata contract (connector
/// slice 1). A connector exposes its table to the chat LLM as a Claude tool, so every
/// structural assumption the tool generator will make — a published, non-system table,
/// a media column of a servable type, a keyed table behind gated writes — must be
/// rejected at model load instead of surfacing as a broken (or dangerous) tool on the
/// first chat request.
/// </summary>
public class ChatConnectorMetadataValidationTests
{
    /// <summary>
    /// A structurally valid connector table: dbo.documents with PK Id, a binary
    /// Image column, a string ImageUrl column, and a string Caption column.
    /// Tests mutate from this known-good baseline.
    /// </summary>
    private static DbModelTestFixture ConnectorFixture(
        Action<DbModelTestFixture.TableBuilder>? documents = null)
        => DbModelTestFixture.Create()
            .WithTable("documents", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("Id")
                    .WithColumn("Name", "nvarchar")
                    .WithColumn("Image", "varbinary")
                    .WithColumn("ImageUrl", "nvarchar")
                    .WithColumn("Caption", "nvarchar")
                    .WithColumn("Size", "int");
                documents?.Invoke(t);
            });

    // ---- valid configs, each connector type alone and combined ----

    [Fact]
    public void Validate_ExploreConnector_DoesNotThrow()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MediaConnectorBinaryColumn_DoesNotThrow()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Image")
                .WithMetadata(MetadataKeys.ChatConnector.MediaVision, MetadataKeys.Chat.Enabled)
                .WithMetadata(MetadataKeys.ChatConnector.MediaCaption, "Caption"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_MediaConnectorStringUrlColumn_DoesNotThrow()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "ImageUrl"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_PlanConnector_DoesNotThrow()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan)
                .WithMetadata(MetadataKeys.ChatConnector.PlanOperations, "insert, update, delete"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_CombinedConnector_DoesNotThrow()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, "explore, media, plan")
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Image")
                .WithMetadata(MetadataKeys.ChatConnector.MediaVision, MetadataKeys.Chat.Enabled)
                .WithMetadata(MetadataKeys.ChatConnector.MediaCaption, "Caption")
                .WithMetadata(MetadataKeys.ChatConnector.PlanOperations, "insert, update")
                .WithMetadata(MetadataKeys.ChatConnector.ToolDescription, "Company documents."))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    // ---- per-rule failures ----

    [Fact]
    public void Validate_UnknownTypeToken_Throws_NamingTableAndKey()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, "browse"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.Marker)
            .And.Contain("'browse'");
    }

    [Fact]
    public void Validate_HiddenConnectorTable_Throws()
    {
        // Arrange: a hidden table is not published into the GraphQL schema; a tool
        // over an unpublished table would read data the API deliberately removed.
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore)
                .WithMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.Marker)
            .And.Contain("published");
    }

    [Fact]
    public void Validate_ConnectorOnHistoryTarget_Throws()
    {
        // Arrange: some tracked table writes its history rows INTO the connector
        // table. History targets are unpublished system tables whose reads the
        // intent executors reject — a tool over one could never work.
        var model = ConnectorFixture(t =>
            {
                t.WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore);
                foreach (var col in MetadataKeys.History.HistoryColumns)
                {
                    if (string.Equals(col, MetadataKeys.History.Column.Id, StringComparison.OrdinalIgnoreCase))
                        continue; // 'Id' PK already satisfies the contract's 'id'.
                    t.WithColumn(col, "nvarchar", isNullable: true);
                }
            })
            .WithTable("orders", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithColumn("Status", "nvarchar")
                .WithMetadata(MetadataKeys.History.Enabled, "update")
                .WithMetadata(MetadataKeys.History.Table, "dbo.documents"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.Marker)
            .And.Contain("history");
    }

    [Fact]
    public void Validate_MediaColumnDoesNotExist_Throws()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Photo"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.MediaColumn)
            .And.Contain("Photo");
    }

    [Fact]
    public void Validate_MediaColumnNeitherBinaryNorStringTyped_Throws()
    {
        // Arrange: an int column can hold neither bytes (binary mode) nor a URL
        // (URL mode) — there is no mode to derive.
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Size"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.MediaColumn)
            .And.Contain("Size")
            .And.Contain("binary")
            .And.Contain("string");
    }

    [Fact]
    public void Validate_CaptionColumnDoesNotExist_Throws()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Image")
                .WithMetadata(MetadataKeys.ChatConnector.MediaCaption, "AltText"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.MediaCaption)
            .And.Contain("AltText");
    }

    [Fact]
    public void Validate_CaptionColumnNotStringTyped_Throws()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Image")
                .WithMetadata(MetadataKeys.ChatConnector.MediaCaption, "Size"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.ChatConnector.MediaCaption)
            .And.Contain("string-typed");
    }

    [Fact]
    public void Validate_PlanConnectorWithoutPrimaryKey_Throws()
    {
        // Arrange: gated writes target rows by primary key; a keyless table cannot
        // name the row a plan step would update or delete.
        var model = DbModelTestFixture.Create()
            .WithTable("documents", t => t
                .WithSchema("dbo")
                .WithColumn("Name", "nvarchar")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan)
                .WithMetadata(MetadataKeys.ChatConnector.PlanOperations, "insert, update"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.Marker)
            .And.Contain("primary-key");
    }

    [Fact]
    public void Validate_MediaColumnWithoutMediaToken_Throws()
    {
        // Arrange: the collector's stray-key rule surfaces through the validator with
        // the table named (Problem format), not as a bare parse exception.
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Image"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain(MetadataKeys.ChatConnector.MediaColumn);
    }

    [Fact]
    public void Validate_UnknownChatConnectorPrefixedKey_RejectedByUnknownKeyGate()
    {
        // Arrange: 'chat-connector-mode' is not a contract key; the central
        // unknown-metadata-key gate must reject it as a typo.
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore)
                .WithMetadata("chat-connector-mode", "binary"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain("chat-connector-mode")
            .And.Contain("unrecognized");
    }

    [Fact]
    public void Validate_UnknownChatMediaPrefixedKey_RejectedByUnknownKeyGate()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeMedia)
                .WithMetadata(MetadataKeys.ChatConnector.MediaColumn, "Image")
                .WithMetadata("chat-media-thumbnail", "Image"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("chat-media-thumbnail").And.Contain("unrecognized");
    }

    [Fact]
    public void Validate_UnknownChatPlanPrefixedKey_RejectedByUnknownKeyGate()
    {
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypePlan)
                .WithMetadata(MetadataKeys.ChatConnector.PlanOperations, "insert")
                .WithMetadata("chat-plan-role", "admin"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("chat-plan-role").And.Contain("unrecognized");
    }

    [Fact]
    public void Validate_ParseError_AttributedToTheOffendingKey_NotTheMarker()
    {
        // Arrange: the marker is valid; the fault is the stray chat-plan-operations
        // key without the plan token. The error must be attributed to that key, not
        // blamed on chat-connector because it happens to be listed first.
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore)
                .WithMetadata(MetadataKeys.ChatConnector.PlanOperations, "insert"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.documents")
            .And.Contain($"[{MetadataKeys.ChatConnector.PlanOperations}]");
    }

    [Fact]
    public void Validate_ExploreConnectorOnTheChatMessagesTable_IsAccepted()
    {
        // Arrange: the chat pair tables MAY be connectors — a conscious decision, not
        // an oversight. "Explore my own conversation history" is a legitimate tool;
        // the pair tables are ordinary published tables and the row-scope transformers
        // guard connector reads exactly as they guard the chat store's.
        var model = DbModelTestFixture.Create()
            .WithTable("conversations", t => t
                .WithSchema("dbo").WithPrimaryKey("Id").WithColumn("Title", "nvarchar")
                .WithMetadata(MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled)
                .WithMetadata(MetadataKeys.Chat.Title, "Title")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore))
            .WithTable("messages", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithColumn("Role", "nvarchar")
                .WithColumn("Content", "nvarchar")
                .WithColumn("ConversationId", "int")
                .WithColumn("CreatedAt", "datetime2")
                .WithMetadata(MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled)
                .WithMetadata(MetadataKeys.Chat.Role, "Role")
                .WithMetadata(MetadataKeys.Chat.Content, "Content")
                .WithMetadata(MetadataKeys.Chat.ConversationFk, "ConversationId")
                .WithMetadata(MetadataKeys.Chat.CreatedAt, "CreatedAt")
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore))
            .WithSingleLink("messages", "ConversationId", "conversations", "Id", "conversation")
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_ConnectorComposesWithChatPairAndHistoryEnabled_DoesNotThrow()
    {
        // Arrange: a connector on a table that also records history (history-ENABLED,
        // not a history TARGET) composes, exactly like the chat pair tables.
        var model = ConnectorFixture(t => t
                .WithMetadata(MetadataKeys.ChatConnector.Marker, MetadataKeys.ChatConnector.TypeExplore)
                .WithMetadata(MetadataKeys.History.Enabled, "update"))
            .WithModelMetadata(MetadataKeys.History.Table, "dbo.__history")
            .WithTable("__history", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey(MetadataKeys.History.Column.Id);
                foreach (var col in MetadataKeys.History.HistoryColumns)
                {
                    if (string.Equals(col, MetadataKeys.History.Column.Id, StringComparison.OrdinalIgnoreCase))
                        continue;
                    t.WithColumn(col, "nvarchar", isNullable: true);
                }
            })
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }
}
