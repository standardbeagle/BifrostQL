using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// Unit coverage for <see cref="ConcurrencyMutationTransformer"/>: it guards only
/// UPDATE, ANDs a <c>token = @clientVersion</c> predicate into the WHERE, bumps the
/// token in the SET, flags <see cref="MutationTransformResult.ConflictOnNoRows"/>, and
/// rejects a missing token or an unsupported token type.
/// </summary>
public sealed class ConcurrencyMutationTransformerTests
{
    private static IDbModel IntTokenModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("version", "int")
                .WithMetadata(MetadataKeys.Concurrency.Token, "version"))
            .Build();

    private static MutationTransformContext Context(IDbModel model) => new()
    {
        Model = model,
        UserContext = new Dictionary<string, object?>(),
    };

    private static Dictionary<string, object?> UpdateData(int version) =>
        new() { ["Id"] = 1, ["Name"] = "changed", ["version"] = version };

    // The model-derived schema.table identity both rejects leak on the detail channel.
    // Spelled as a literal here (not read back from the fixture) so a fact asserting its
    // ABSENCE from the wire text cannot be satisfied by a code path that never produced
    // the name at all.
    private const string TableIdentity = "dbo.Orders";

    private static IDbModel UnsupportedTokenModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("row_version", "nvarchar")
                .WithMetadata(MetadataKeys.Concurrency.Token, "row_version"))
            .Build();

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void Priority_IsSixty()
        => new ConcurrencyMutationTransformer().Priority.Should().Be(60);

    [Fact]
    public void AppliesTo_UpdateWithMetadata_True_ButNotInsertOrDelete()
    {
        var model = IntTokenModel();
        var table = model.GetTableFromDbName("Orders");
        var transformer = new ConcurrencyMutationTransformer();
        var ctx = Context(model);

        transformer.AppliesTo(table, MutationType.Update, ctx).Should().BeTrue();
        transformer.AppliesTo(table, MutationType.Insert, ctx).Should().BeFalse();
        transformer.AppliesTo(table, MutationType.Delete, ctx).Should().BeFalse();
    }

    [Fact]
    public async Task Update_GuardsWhereOnClientVersion_BumpsSet_AndFlagsConflict()
    {
        var model = IntTokenModel();
        var table = model.GetTableFromDbName("Orders");

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, UpdateData(3), Context(model));

        result.Errors.Should().BeEmpty();
        result.ConflictOnNoRows.Should().BeTrue();

        // WHERE: version = 3 (the client's read version).
        result.AdditionalFilter.Should().NotBeNull();
        result.AdditionalFilter!.ColumnName.Should().Be("version");
        result.AdditionalFilter.Next!.Value.Should().Be(3);

        // SET: version bumped to 4.
        result.Data["version"].Should().Be(4L);
    }

    [Fact]
    public async Task Update_MissingToken_IsRejected()
    {
        var model = IntTokenModel();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "changed" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle().Which.Should().Contain("must include the concurrency token");
    }

    [Fact]
    public async Task Update_DatetimeToken_RestampsToNow()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("updated_at", "datetime2")
                .WithMetadata(MetadataKeys.Concurrency.Token, "updated_at"))
            .Build();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["updated_at"] = "2020-01-01T00:00:00Z" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data["updated_at"].Should().BeOfType<DateTimeOffset>();
        result.ConflictOnNoRows.Should().BeTrue();
    }

    [Fact]
    public async Task Update_TokenValueAtMax_IsRejectedCleanly_NotThrown()
    {
        // A Decimal token at decimal.MaxValue cannot be advanced; the transformer must
        // return a clean error result, not throw an unhandled OverflowException.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("version", "decimal")
                .WithMetadata(MetadataKeys.Concurrency.Token, "version"))
            .Build();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["version"] = decimal.MaxValue };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle().Which.Should().Contain("representable range");
    }

    [Fact]
    public async Task Update_UnsupportedTokenType_IsRejected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithColumn("row_version", "nvarchar")
                .WithMetadata(MetadataKeys.Concurrency.Token, "row_version"))
            .Build();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["row_version"] = "abc" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        result.Errors.Should().ContainSingle().Which.Should().Contain("unsupported type");
    }

    // ---- wire sanitization: the model-derived schema.table must not reach the client ----
    // (protocol-adapter-security.md invariant 3; sibling of the lost-update CONFLICT
    //  sanitization task 01M1ZK87RA6E4KPM5S46CFCRPD). The two pre-write conditions stay
    //  distinguishable on the wire by their message text; the table identity is recorded
    //  server-side through BifrostErrorSink, exactly as the CONFLICT detail is.

    [Fact]
    public async Task MissingToken_WireMessage_NamesCondition_AndOmitsTableIdentity()
    {
        var model = IntTokenModel();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "changed" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        // Byte-exact wire text (spelled out, not read back from production): a missing
        // token is a distinct condition from a stale/unbumpable one and says so.
        result.Errors.Should().ContainSingle().Which.Should().Be(
            "The update must include the concurrency token column 'version' (the version the row was read at).");
        // The negative fact: the identifier is gone from the message.
        string.Join(" ", result.Errors).Should().NotContain(TableIdentity,
            "the model-derived schema.table must never reach the wire; only the actionable, "
            + "identifier-free instruction does");
    }

    [Fact]
    public async Task MissingToken_LogsTableIdentityServerSide()
    {
        // CONTROL for the absence assertion above: the name IS still recorded, through
        // the shared sink — otherwise "not on the wire" is indistinguishable from
        // "the name was never produced at all".
        var logger = new CapturingLogger();
        using (BifrostErrorSink.OverrideForTesting(logger))
        {
            var model = IntTokenModel();
            var table = model.GetTableFromDbName("Orders");
            var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "changed" };

            var result = await new ConcurrencyMutationTransformer()
                .TransformAsync(table, MutationType.Update, data, Context(model));

            result.Errors.Should().NotBeEmpty();
            logger.Messages.Should().Contain(m => m.Contains(TableIdentity),
                "the model-derived schema.table is kept server-side in the diagnostic");
        }
    }

    [Fact]
    public async Task UnbumpableToken_WireMessage_DistinguishableFromMissing_AndOmitsTableIdentity()
    {
        var model = UnsupportedTokenModel();
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["row_version"] = "abc" };

        var result = await new ConcurrencyMutationTransformer()
            .TransformAsync(table, MutationType.Update, data, Context(model));

        var wire = result.Errors.Should().ContainSingle().Which;
        // A caller distinguishes "your token cannot be advanced" from "you did not send a
        // token": different leading text, not merely a different code (there is no code).
        wire.Should().StartWith("The concurrency token 'row_version' ");
        wire.Should().Contain("unsupported type");
        wire.Should().NotContain(TableIdentity,
            "the model-derived schema.table must never reach the wire");
    }

    [Fact]
    public async Task UnbumpableToken_LogsTableIdentityServerSide()
    {
        var logger = new CapturingLogger();
        using (BifrostErrorSink.OverrideForTesting(logger))
        {
            var model = UnsupportedTokenModel();
            var table = model.GetTableFromDbName("Orders");
            var data = new Dictionary<string, object?> { ["Id"] = 1, ["Name"] = "x", ["row_version"] = "abc" };

            var result = await new ConcurrencyMutationTransformer()
                .TransformAsync(table, MutationType.Update, data, Context(model));

            result.Errors.Should().NotBeEmpty();
            logger.Messages.Should().Contain(m => m.Contains(TableIdentity),
                "the model-derived schema.table is kept server-side in the diagnostic");
        }
    }
}
