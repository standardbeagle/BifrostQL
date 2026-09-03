using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BifrostQL.Core.Test.Modules;

/// <summary>
/// A column whose GraphQL field name differs from its database column name
/// (<c>Order Status</c> → <c>order_Status</c>, <c>sale-price</c> → <c>sale_price</c>)
/// used to reach the mutation transformer chain keyed by its GRAPHQL name, while
/// every metadata-driven transformer resolves its column by DATABASE name. The
/// write-deny list, the state column, the audit column set and the enum column map
/// therefore all missed the renamed column: a denied write went through and a state
/// transition was never validated.
///
/// The chain now rekeys its input to database column names ONCE, before the first
/// transformer runs (<see cref="MutationTransformersWrap.TransformAsync"/>), so every
/// transformer sees exactly one name space and none of them needs a dual-name
/// fallback. These tests pin that contract on a fixture whose GraphQL names differ
/// from its column names for EVERY column under test — a fixture where the two names
/// coincide cannot distinguish the fixed chain from the broken one.
/// </summary>
public sealed class RenamedColumnMutationRekeyTests
{
    // DB column name → GraphQL field name, exactly as ModelExtensions.ToGraphQl
    // would derive it from a column containing a space or a dash.
    private const string StateColumnDb = "Order Status";
    private const string StateColumnGraphQl = "order_Status";
    private const string PriceColumnDb = "sale-price";
    private const string PriceColumnGraphQl = "sale_price";
    private const string TokenColumnDb = "row version";
    private const string TokenColumnGraphQl = "row_version";
    private const string UpdatedByColumnDb = "updated by";
    private const string UpdatedByColumnGraphQl = "updated_by";
    private const string SecretColumnDb = "secret note";
    private const string SecretColumnGraphQl = "secret_note";
    private const string EnumColumnDb = "Status Code";
    private const string EnumColumnGraphQl = "status_Code";

    private static MutationTransformContext Context(
        IDbModel model,
        IDictionary<string, object?>? userContext = null,
        IReadOnlyDictionary<string, object?>? currentRow = null,
        IServiceProvider? services = null) => new()
        {
            Model = model,
            UserContext = userContext ?? new Dictionary<string, object?>(),
            CurrentRow = currentRow,
            Services = services,
        };

    private static IMutationTransformers Chain(params IMutationTransformer[] transformers)
        => new MutationTransformersWrap { Transformers = transformers };

    // ---- policy column write-deny on a renamed column --------------------

    private static IDbModel PolicyModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn(PriceColumnDb, "decimal", graphQlName: PriceColumnGraphQl)
                .WithColumn("Total", "decimal")
                .WithMetadata(MetadataKeys.Policy.Actions, "update")
                .WithMetadata(MetadataKeys.Policy.WriteDeny, PriceColumnDb))
            .Build();

    [Fact]
    public async Task WriteDeny_DeniesRenamedColumn_SuppliedUnderItsGraphQlName()
    {
        var model = PolicyModel();
        var chain = Chain(new PolicyMutationTransformer());
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            [PriceColumnGraphQl] = 99m,
        };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"),
            MutationType.Update,
            data,
            Context(model, new Dictionary<string, object?> { ["user_id"] = "u1", ["roles"] = new[] { "user" } }));

        result.Errors.Should().NotBeEmpty(
            "a write-denied column must stay denied when the client addresses it by its GraphQL field name");
    }

    [Fact]
    public async Task WriteDeny_StillAllowsAnUndeniedColumn()
    {
        var model = PolicyModel();
        var chain = Chain(new PolicyMutationTransformer());
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Total"] = 5m };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"),
            MutationType.Update,
            data,
            Context(model, new Dictionary<string, object?> { ["user_id"] = "u1", ["roles"] = new[] { "user" } }));

        result.Errors.Should().BeEmpty();
    }

    // ---- state machine on a renamed state column -------------------------

    private static IDbModel StateMachineModel() =>
        DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn(StateColumnDb, "varchar", graphQlName: StateColumnGraphQl)
                .WithMetadata(MetadataKeys.StateMachine.StateColumn, StateColumnDb)
                .WithMetadata(MetadataKeys.StateMachine.InitialState, "pending")
                .WithMetadata(MetadataKeys.StateMachine.States, "pending, active, inactive")
                .WithMetadata(MetadataKeys.StateMachine.Transitions, "pending->active"))
            .Build();

    [Fact]
    public async Task StateMachine_DeniesIllegalTransition_OnRenamedStateColumn()
    {
        var model = StateMachineModel();
        var chain = Chain(new StateMachineMutationTransformer());
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            [StateColumnGraphQl] = "inactive",
        };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"),
            MutationType.Update,
            data,
            Context(
                model,
                currentRow: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [StateColumnDb] = "pending" }));

        result.Errors.Should().NotBeEmpty(
            "pending->inactive is not a declared transition, and the state column must be found under its DB name");
    }

    [Fact]
    public async Task StateMachine_AllowsDeclaredTransition_OnRenamedStateColumn()
    {
        var model = StateMachineModel();
        var chain = Chain(new StateMachineMutationTransformer());
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            [StateColumnGraphQl] = "active",
        };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"),
            MutationType.Update,
            data,
            Context(
                model,
                currentRow: new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { [StateColumnDb] = "pending" }));

        result.Errors.Should().BeEmpty();
        result.StateTransition.Should().NotBeNull(
            "the transition must actually be recognised, not merely skipped for lack of a matching key");
        result.StateTransition!.From.Should().Be("pending");
        result.StateTransition.To.Should().Be("active");
    }

    // ---- concurrency token on a renamed column ---------------------------

    [Fact]
    public async Task ConcurrencyToken_BumpsUnderTheDatabaseColumnName()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithColumn(TokenColumnDb, "int", graphQlName: TokenColumnGraphQl)
                .WithMetadata(MetadataKeys.Concurrency.Token, TokenColumnDb))
            .Build();
        var chain = Chain(new ConcurrencyMutationTransformer());
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Total"] = 5m,
            [TokenColumnGraphQl] = 3,
        };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"), MutationType.Update, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data.Should().ContainKey(TokenColumnDb);
        result.Data[TokenColumnDb].Should().Be(4);
        result.Data.Should().NotContainKey(TokenColumnGraphQl,
            "the chain hands SQL one name space; a surviving GraphQL alias would bind a second, unmapped parameter");
        result.ConflictOnNoRows.Should().BeTrue();
    }

    // ---- encrypt-on-write on a renamed column ----------------------------

    [Fact]
    public async Task EncryptOnWrite_EncryptsRenamedColumnUnderItsDatabaseName()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn(SecretColumnDb, "nvarchar", graphQlName: SecretColumnGraphQl)
                .WithColumnMetadata(SecretColumnDb, MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
                .WithColumnMetadata(SecretColumnDb, MetadataKeys.Crypto.KeyRef, "config:pii"))
            .Build();

        var rootKey = new byte[FieldCipher.KeySize];
        for (var i = 0; i < rootKey.Length; i++) rootKey[i] = (byte)(i + 7);
        var services = new ServiceCollection()
            .AddSingleton(new EnvelopeKeyManager(
                new ConfigRootKeyProvider(rootKey), new InMemoryDataEncryptionKeyStore()))
            .BuildServiceProvider();

        var chain = Chain(new EncryptOnWriteMutationTransformer());
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            [SecretColumnGraphQl] = "hunter2",
        };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"), MutationType.Update, data, Context(model, services: services));

        result.Errors.Should().BeEmpty();
        result.Data.Should().ContainKey(SecretColumnDb);
        result.Data[SecretColumnDb].Should().NotBe("hunter2");
        result.Data.Should().NotContainKey(SecretColumnGraphQl,
            "plaintext must not survive under a second key that the SQL layer would also bind");
    }

    // ---- enum name mapping on a renamed column ---------------------------

    [Fact]
    public async Task EnumMapping_RewritesRenamedColumnUnderItsDatabaseName()
    {
        const string enumTable = "OrderStatus";
        const string valueColumn = "Code";
        var model = (DbModel)DbModelTestFixture.Create()
            .WithTable(enumTable, t => t
                .WithSchema("dbo")
                .WithMetadata(EnumTableConfig.MetadataKey, valueColumn)
                .WithColumn("Id", "int", isPrimaryKey: true)
                .WithColumn(valueColumn, "varchar")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn(EnumColumnDb, "varchar", graphQlName: EnumColumnGraphQl))
            .WithForeignKey("FK_Orders_StatusCode", "Orders", EnumColumnDb, enumTable, valueColumn)
            .Build();
        model.EnumColumns = EnumColumnMap.Build(
            model,
            new Dictionary<string, IReadOnlyList<EnumValueEntry>>(StringComparer.OrdinalIgnoreCase)
            {
                [enumTable] = EnumValueSanitizer.SanitizeAll(new[] { "active", "on hold" }),
            },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [enumTable] = valueColumn });

        var chain = Chain(new EnumValueMutationTransformer());
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            [EnumColumnGraphQl] = "ON_HOLD",
        };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"), MutationType.Update, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data.Should().ContainKey(EnumColumnDb);
        result.Data[EnumColumnDb].Should().Be("on hold",
            "enum-name resolution must survive the rekey — the stored value, not the GraphQL enum name, reaches SQL");
    }

    // ---- audit populate on a renamed column ------------------------------

    [Fact]
    public async Task AuditPopulate_StripsClientSuppliedActor_OnRenamedColumn()
    {
        // No model-level audit user key is configured, so the audit transformer has no
        // trustworthy actor and must STRIP the client's value rather than let it reach
        // the row. Addressed by its GraphQL name, the client's value used to slip past
        // the strip and then be rekeyed onto the real column by the executor — audit
        // spoofing through a renamed column.
        var model = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal")
                .WithColumn(UpdatedByColumnDb, "nvarchar", graphQlName: UpdatedByColumnGraphQl)
                .WithColumnMetadata(UpdatedByColumnDb, MetadataKeys.AutoPopulate.Marker, MetadataKeys.AutoPopulate.UpdatedBy))
            .Build();

        var chain = Chain(new AuditMutationTransformer());
        var data = new Dictionary<string, object?>
        {
            ["Id"] = 1,
            ["Total"] = 5m,
            [UpdatedByColumnGraphQl] = "spoofed-actor",
        };

        var result = await chain.TransformAsync(
            model.GetTableFromDbName("Orders"), MutationType.Update, data, Context(model));

        result.Errors.Should().BeEmpty();
        result.Data.Should().NotContainKey(UpdatedByColumnGraphQl);
        result.Data.Should().NotContainKey(UpdatedByColumnDb,
            "with no configured audit user key the server-owned column must be stripped, not carry the client's value");
    }

    [Fact]
    public async Task ChainOutput_IsKeyedOnlyByDatabaseColumnNames()
    {
        var model = PolicyModel();
        var chain = Chain(new PolicyMutationTransformer());
        var table = model.GetTableFromDbName("Orders");
        var data = new Dictionary<string, object?> { ["Id"] = 1, ["Total"] = 5m };

        var result = await chain.TransformAsync(
            table,
            MutationType.Update,
            data,
            Context(model, new Dictionary<string, object?> { ["user_id"] = "u1", ["roles"] = new[] { "user" } }));

        result.Data.Keys.Should().OnlyContain(k => table.ColumnLookup.ContainsKey(k));
    }
}
