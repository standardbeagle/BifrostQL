using BifrostQL.Core.Model;
using BifrostQL.Core.Modules.Chat;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Coverage for Chat slice 1 — the metadata contract parsed from
/// <c>chat-conversations</c> / <c>chat-messages</c> and the message column mappings
/// into a typed <see cref="ChatConfig"/>. The chat surface (a later slice) consumes
/// this config, so its parse and its fail-fast behavior on typo'd tokens and partial
/// mappings are pinned here: a silently ignored key would leave the author believing
/// a chat schema exists when it does not.
/// </summary>
public class ChatConfigTests
{
    private static IDbTable ConversationsTable(params (string key, object? value)[] metadata)
        => BuildTable("conversations", t =>
        {
            t.WithSchema("dbo").WithPrimaryKey("Id").WithColumn("Title", "nvarchar");
            foreach (var (key, value) in metadata)
                t.WithMetadata(key, value);
        });

    private static IDbTable MessagesTable(params (string key, object? value)[] metadata)
        => BuildTable("messages", t =>
        {
            t.WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Role", "nvarchar")
                .WithColumn("Content", "nvarchar")
                .WithColumn("ConversationId", "int")
                .WithColumn("CreatedAt", "datetime2");
            foreach (var (key, value) in metadata)
                t.WithMetadata(key, value);
        });

    private static IDbTable BuildTable(string name, Action<DbModelTestFixture.TableBuilder> configure)
    {
        var model = DbModelTestFixture.Create().WithTable(name, configure).Build();
        return model.GetTableFromDbName(name);
    }

    private static (string key, object? value)[] FullMessagesMetadata() => new (string, object?)[]
    {
        (MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled),
        (MetadataKeys.Chat.Role, "Role"),
        (MetadataKeys.Chat.Content, "Content"),
        (MetadataKeys.Chat.ConversationFk, "ConversationId"),
        (MetadataKeys.Chat.CreatedAt, "CreatedAt"),
    };

    [Fact]
    public void FromTable_NoChatKeys_ReturnsNone()
    {
        // Arrange
        var table = ConversationsTable();

        // Act
        var config = ChatConfig.FromTable(table);

        // Assert
        config.Kind.Should().Be(ChatTableKind.None);
        config.Should().BeSameAs(ChatConfig.None);
    }

    [Fact]
    public void FromTable_ConversationsEnabled_ParsesKind()
    {
        // Arrange
        var table = ConversationsTable((MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled));

        // Act
        var config = ChatConfig.FromTable(table);

        // Assert
        config.Kind.Should().Be(ChatTableKind.Conversations);
        config.TitleColumn.Should().BeNull();
    }

    [Fact]
    public void FromTable_ConversationsTitle_CanonicalizedToDbCasing()
    {
        // Arrange: 'title' is spelled in the wrong case; the config must carry the
        // column's database casing so downstream SQL identifiers match (history
        // defect 3 lesson — canonicalize through ColumnLookup at parse time).
        var table = ConversationsTable(
            (MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled),
            (MetadataKeys.Chat.Title, "title"));

        // Act
        var config = ChatConfig.FromTable(table);

        // Assert
        config.TitleColumn.Should().Be("Title");
    }

    [Fact]
    public void FromTable_ConversationsUnknownToken_Throws()
    {
        // Arrange: 'true' is not the opt-in token; silently accepting it would make
        // every other truthy spelling appear to work.
        var table = ConversationsTable((MetadataKeys.Chat.Conversations, "true"));

        // Act
        var act = () => ChatConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'true'*{MetadataKeys.Chat.Conversations}*{MetadataKeys.Chat.Enabled}*");
    }

    [Fact]
    public void FromTable_ConversationsEmptyValue_Throws()
    {
        // Arrange: key present but blank — the author opted in and got nothing.
        var table = ConversationsTable((MetadataKeys.Chat.Conversations, ""));

        // Act
        var act = () => ChatConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Conversations}*empty*");
    }

    [Fact]
    public void FromTable_MessagesComplete_ParsesAllColumnsCanonicalized()
    {
        // Arrange: every mapping is spelled in the wrong case; each must be
        // canonicalized to the column's database casing through ColumnLookup.
        var table = MessagesTable(
            (MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled),
            (MetadataKeys.Chat.Role, "role"),
            (MetadataKeys.Chat.Content, "content"),
            (MetadataKeys.Chat.ConversationFk, "conversationid"),
            (MetadataKeys.Chat.CreatedAt, "createdat"));

        // Act
        var config = ChatConfig.FromTable(table);

        // Assert
        config.Kind.Should().Be(ChatTableKind.Messages);
        config.RoleColumn.Should().Be("Role");
        config.ContentColumn.Should().Be("Content");
        config.ConversationFkColumn.Should().Be("ConversationId");
        config.CreatedAtColumn.Should().Be("CreatedAt");
    }

    [Fact]
    public void FromTable_MessagesMissingMappings_Throws_ListingEveryMissingKey()
    {
        // Arrange: opted in but only the role mapping is present. The error must list
        // every missing key so the author fixes the config in one pass.
        var table = MessagesTable(
            (MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled),
            (MetadataKeys.Chat.Role, "Role"));

        // Act
        var act = () => ChatConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Content}*{MetadataKeys.Chat.ConversationFk}*{MetadataKeys.Chat.CreatedAt}*");
    }

    [Fact]
    public void FromTable_MessagesEmptyMappingValue_Throws()
    {
        // Arrange: chat-content is present but blank — present-but-empty is an error,
        // not an omission.
        var table = MessagesTable(
            (MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled),
            (MetadataKeys.Chat.Role, "Role"),
            (MetadataKeys.Chat.Content, ""),
            (MetadataKeys.Chat.ConversationFk, "ConversationId"),
            (MetadataKeys.Chat.CreatedAt, "CreatedAt"));

        // Act
        var act = () => ChatConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Content}*");
    }

    [Fact]
    public void FromTable_BothOptInsOnOneTable_Throws()
    {
        // Arrange: one table cannot be both sides of the pair.
        var table = MessagesTable(
            (MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled),
            (MetadataKeys.Chat.Messages, MetadataKeys.Chat.Enabled));

        // Act
        var act = () => ChatConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Conversations}*{MetadataKeys.Chat.Messages}*");
    }

    [Fact]
    public void FromTable_MessageMappingWithoutOptIn_Throws()
    {
        // Arrange: chat-role without chat-messages records nothing — the author
        // believes the table is a chat table and it is not.
        var table = MessagesTable((MetadataKeys.Chat.Role, "Role"));

        // Act
        var act = () => ChatConfig.FromTable(table);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Role}*{MetadataKeys.Chat.Messages}*");
    }

    [Fact]
    public void FromTable_TitleWithoutConversations_Throws()
    {
        var table = ConversationsTable((MetadataKeys.Chat.Title, "Title"));

        var act = () => ChatConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Title}*{MetadataKeys.Chat.Conversations}*");
    }

    [Fact]
    public void FromTable_TitleOnMessagesTable_Throws()
    {
        // Arrange: chat-title belongs to the conversations table only.
        var metadata = FullMessagesMetadata().Append((MetadataKeys.Chat.Title, (object?)"Role")).ToArray();
        var table = MessagesTable(metadata);

        var act = () => ChatConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Title}*conversations*");
    }

    [Fact]
    public void FromTable_MessageMappingOnConversationsTable_Throws()
    {
        // Arrange: chat-role belongs to the messages table only.
        var table = ConversationsTable(
            (MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled),
            (MetadataKeys.Chat.Role, "Title"));

        var act = () => ChatConfig.FromTable(table);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Role}*messages*");
    }

    // ---- FromModel: exactly one (conversations, messages) pair per model ----

    private static DbModelTestFixture PairFixture(
        Action<DbModelTestFixture.TableBuilder>? conversations = null,
        Action<DbModelTestFixture.TableBuilder>? messages = null)
        => DbModelTestFixture.Create()
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
            })
            .WithSingleLink("messages", "ConversationId", "conversations", "Id", "conversation");

    [Fact]
    public void FromModel_NoChatTables_ReturnsNull()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("orders", t => t.WithSchema("dbo").WithPrimaryKey("Id"))
            .Build();

        ChatConfig.FromModel(model).Should().BeNull();
    }

    [Fact]
    public void FromModel_ValidPair_ReturnsBothTablesWithConfigs()
    {
        // Arrange
        var model = PairFixture().Build();

        // Act
        var chat = ChatConfig.FromModel(model);

        // Assert
        chat.Should().NotBeNull();
        chat!.ConversationsTable.DbName.Should().Be("conversations");
        chat.MessagesTable.DbName.Should().Be("messages");
        chat.ConversationsConfig.TitleColumn.Should().Be("Title");
        chat.MessagesConfig.ConversationFkColumn.Should().Be("ConversationId");
    }

    [Fact]
    public void FromModel_MessagesWithoutConversations_Throws()
    {
        // Arrange: a messages table with no conversations table in the model.
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

        var act = () => ChatConfig.FromModel(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*dbo.messages*{MetadataKeys.Chat.Conversations}*");
    }

    [Fact]
    public void FromModel_ConversationsWithoutMessages_Throws()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("conversations", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled))
            .Build();

        var act = () => ChatConfig.FromModel(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*dbo.conversations*{MetadataKeys.Chat.Messages}*");
    }

    [Fact]
    public void FromModel_TwoConversationsTables_Throws()
    {
        var model = PairFixture().WithTable("threads", t => t
                .WithSchema("dbo").WithPrimaryKey("Id")
                .WithMetadata(MetadataKeys.Chat.Conversations, MetadataKeys.Chat.Enabled))
            .Build();

        var act = () => ChatConfig.FromModel(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Conversations}*dbo.conversations*dbo.threads*");
    }

    [Fact]
    public void FromModel_TwoMessagesTables_Throws()
    {
        var model = PairFixture().WithTable("messages2", t => t
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

        var act = () => ChatConfig.FromModel(model);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{MetadataKeys.Chat.Messages}*dbo.messages*dbo.messages2*");
    }
}
