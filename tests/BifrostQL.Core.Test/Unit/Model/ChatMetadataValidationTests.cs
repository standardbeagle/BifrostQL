using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Fail-fast validation coverage for the chat metadata contract (slice 1). The chat
/// tables are user-supplied, so every structural assumption the chat surface will make
/// — one conversations/messages pair, real columns of the right types, a message FK
/// that actually reaches the conversation PK, published tables — must be rejected at
/// model load instead of surfacing as a broken chat schema on the first request.
/// </summary>
public class ChatMetadataValidationTests
{
    /// <summary>
    /// A structurally valid chat pair: dbo.conversations (PK Id, Title) and
    /// dbo.messages (PK Id, Role/Content/CreatedAt mappings, ConversationId FK
    /// linked to conversations.Id). Tests mutate from this known-good baseline.
    /// </summary>
    private static DbModelTestFixture ValidChatFixture(
        Action<DbModelTestFixture.TableBuilder>? conversations = null,
        Action<DbModelTestFixture.TableBuilder>? messages = null,
        bool linkFkToConversationPk = true)
    {
        var fixture = DbModelTestFixture.Create()
            .WithTable("conversations", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("Id").WithColumn("Title", "nvarchar")
                    .WithMetadata(MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled)
                    .WithMetadata(MetadataKeys.Chat.Title, "Title");
                conversations?.Invoke(t);
            })
            .WithTable("messages", t =>
            {
                t.WithSchema("dbo").WithPrimaryKey("Id")
                    .WithColumn("Role", "nvarchar")
                    .WithColumn("Content", "nvarchar")
                    .WithColumn("ConversationId", "int")
                    .WithColumn("CreatedAt", "datetime2")
                    .WithMetadata(MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled)
                    .WithMetadata(MetadataKeys.Chat.Role, "Role")
                    .WithMetadata(MetadataKeys.Chat.Content, "Content")
                    .WithMetadata(MetadataKeys.Chat.ConversationFk, "ConversationId")
                    .WithMetadata(MetadataKeys.Chat.CreatedAt, "CreatedAt");
                messages?.Invoke(t);
            });

        if (linkFkToConversationPk)
            fixture.WithSingleLink("messages", "ConversationId", "conversations", "Id", "conversation");

        return fixture;
    }

    [Fact]
    public void Validate_ValidChatPair_DoesNotThrow()
    {
        var model = ValidChatFixture().Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validate_UnknownOptInToken_Throws()
    {
        // Arrange: 'yes' is not the opt-in token; the gate must reject it rather than
        // let the author believe the table is a chat table.
        var model = ValidChatFixture(conversations: t => t
                .WithMetadata(MetadataKeys.Chat.Conversations, "yes"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.conversations")
            .And.Contain(MetadataKeys.Chat.Conversations)
            .And.Contain("'yes'");
    }

    [Fact]
    public void Validate_IncompleteMessagesMapping_Throws()
    {
        // Arrange: chat-content missing from the messages mapping.
        var model = DbModelTestFixture.Create()
            .WithTable("conversations", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled))
            .WithTable("messages", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithColumn("Role", "nvarchar")
                .WithColumn("ConversationId", "int")
                .WithColumn("CreatedAt", "datetime2")
                .WithMetadata(MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled)
                .WithMetadata(MetadataKeys.Chat.Role, "Role")
                .WithMetadata(MetadataKeys.Chat.ConversationFk, "ConversationId")
                .WithMetadata(MetadataKeys.Chat.CreatedAt, "CreatedAt"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.messages").And.Contain(MetadataKeys.Chat.Content);
    }

    [Fact]
    public void Validate_MessagesWithoutConversationsTable_Throws()
    {
        var model = DbModelTestFixture.Create()
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
                .WithMetadata(MetadataKeys.Chat.CreatedAt, "CreatedAt"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.messages").And.Contain(MetadataKeys.Chat.Conversations);
    }

    [Fact]
    public void Validate_TwoConversationsTables_Throws()
    {
        var model = ValidChatFixture()
            .WithTable("threads", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.conversations").And.Contain("dbo.threads");
    }

    [Fact]
    public void Validate_ConversationsWithoutPrimaryKey_Throws()
    {
        // Arrange: replace the conversations table with a PK-less shape.
        var model = ValidChatFixture(linkFkToConversationPk: false)
            .WithTable("conversations", t => t
                .WithSchema("dbo").WithColumn("Title", "nvarchar")
                .WithMetadata(MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.conversations").And.Contain("primary-key");
    }

    [Fact]
    public void Validate_ConversationsCompositePrimaryKey_Throws()
    {
        // Arrange: the conversation FK is a single column, so a composite conversation
        // PK cannot be referenced by it — clear error, not a first-column guess.
        var model = ValidChatFixture(conversations: t => t
                .WithColumn("TenantId", "int", isPrimaryKey: true))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.conversations").And.Contain("composite");
    }

    [Fact]
    public void Validate_MessagesWithoutPrimaryKey_Throws()
    {
        var model = ValidChatFixture(linkFkToConversationPk: false)
            .WithTable("messages", t => t
                .WithSchema("dbo")
                .WithColumn("Role", "nvarchar")
                .WithColumn("Content", "nvarchar")
                .WithColumn("ConversationId", "int")
                .WithColumn("CreatedAt", "datetime2")
                .WithMetadata(MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled)
                .WithMetadata(MetadataKeys.Chat.Role, "Role")
                .WithMetadata(MetadataKeys.Chat.Content, "Content")
                .WithMetadata(MetadataKeys.Chat.ConversationFk, "ConversationId")
                .WithMetadata(MetadataKeys.Chat.CreatedAt, "CreatedAt"))
            .WithSingleLink("messages", "ConversationId", "conversations", "Id", "conversation")
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.messages").And.Contain("primary-key");
    }

    [Fact]
    public void Validate_TitleColumnDoesNotExist_Throws()
    {
        var model = ValidChatFixture(conversations: t => t
                .WithMetadata(MetadataKeys.Chat.Title, "Subject"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.conversations")
            .And.Contain(MetadataKeys.Chat.Title)
            .And.Contain("Subject");
    }

    [Fact]
    public void Validate_RoleColumnDoesNotExist_Throws()
    {
        var model = ValidChatFixture(messages: t => t
                .WithMetadata(MetadataKeys.Chat.Role, "Speaker"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.messages")
            .And.Contain(MetadataKeys.Chat.Role)
            .And.Contain("Speaker");
    }

    [Fact]
    public void Validate_RoleColumnNotStringTyped_Throws()
    {
        // Arrange: an int role column cannot carry user/assistant role names.
        var model = ValidChatFixture(messages: t => t
                .WithColumn("RoleId", "int")
                .WithMetadata(MetadataKeys.Chat.Role, "RoleId"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.messages")
            .And.Contain(MetadataKeys.Chat.Role)
            .And.Contain("string-typed");
    }

    [Fact]
    public void Validate_ContentColumnNotStringTyped_Throws()
    {
        var model = ValidChatFixture(messages: t => t
                .WithColumn("Blob", "varbinary")
                .WithMetadata(MetadataKeys.Chat.Content, "Blob"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Chat.Content).And.Contain("string-typed");
    }

    [Fact]
    public void Validate_CreatedAtColumnNotDateTimeTyped_Throws()
    {
        // Arrange: a string created-at cannot order messages reliably.
        var model = ValidChatFixture(messages: t => t
                .WithColumn("CreatedText", "nvarchar")
                .WithMetadata(MetadataKeys.Chat.CreatedAt, "CreatedText"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain(MetadataKeys.Chat.CreatedAt).And.Contain("date/time");
    }

    [Fact]
    public void Validate_ConversationFkDoesNotReferenceConversationPk_Throws()
    {
        // Arrange: the FK column exists but no relationship links it to the
        // conversations PK — the chat surface would join messages to nothing.
        var model = ValidChatFixture(linkFkToConversationPk: false).Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.messages")
            .And.Contain(MetadataKeys.Chat.ConversationFk)
            .And.Contain("dbo.conversations");
    }

    [Fact]
    public void Validate_HiddenChatTable_Throws()
    {
        // Arrange: a hidden table is not published into the GraphQL schema, so the
        // chat surface built on that schema would reference a table that isn't there.
        var model = ValidChatFixture(conversations: t => t
                .WithMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.conversations").And.Contain("published");
    }

    [Fact]
    public void Validate_ChatTableIsHistoryTarget_Throws()
    {
        // Arrange: some tracked table writes its history rows INTO the conversations
        // table. History targets are system tables (never published, reads rejected),
        // which contradicts everything the chat surface needs from its tables.
        var model = ValidChatFixture(conversations: t =>
            {
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
                .WithMetadata(MetadataKeys.History.Table, "dbo.conversations"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("dbo.conversations").And.Contain("history");
    }

    [Fact]
    public void Validate_HistoryEnabledChatTable_DoesNotThrow_Composes()
    {
        // Arrange: the messages table itself records history (it is history-ENABLED,
        // not a history TARGET) — that composes with chat.
        var model = ValidChatFixture(messages: t => t
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

    [Fact]
    public void Validate_NoChatConfigured_DoesNotThrow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t.WithSchema("dbo").WithPrimaryKey("Id"))
            .Build();

        var act = () => ModelConfigValidator.Validate(model);

        act.Should().NotThrow();
    }
}
