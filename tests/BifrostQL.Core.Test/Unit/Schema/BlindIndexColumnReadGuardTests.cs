using BifrostQL.Core.Auth;
using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Schema;

/// <summary>
/// Read-side counterpart of the blind-index write guard: the shadow column's
/// token is a deterministic HMAC, so reading it enables equality correlation
/// across visible rows and predicating on it probes the index directly. The
/// column is hidden from every read schema surface and BlindIndexColumnGuard
/// rejects direct references — while the server's own equality rewrite (which
/// injects ServerDerived filter nodes) stays functional, and a policy read-deny
/// on the encrypted SOURCE column now binds to the rewritten equality probe.
/// </summary>
public class BlindIndexColumnReadGuardTests
{
    private const string KeyRef = "config:pii";

    private static IDbModel SecretsModel(string? readDenyColumn = null) =>
        DbModelTestFixture.Create()
            .WithTable("secrets", t =>
            {
                t.WithSchema("main")
                    .WithPrimaryKey("id")
                    .WithColumn("ssn", "nvarchar")
                    .WithColumn("ssn_bidx", "nvarchar", isNullable: true)
                    .WithColumn("note")
                    .WithColumnMetadata("ssn", MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
                    .WithColumnMetadata("ssn", MetadataKeys.Crypto.KeyRef, KeyRef)
                    .WithColumnMetadata("ssn", MetadataKeys.Crypto.BlindIndex, "ssn_bidx");
                if (readDenyColumn is not null)
                    t.WithMetadata(MetadataKeys.Policy.Actions, "read")
                        .WithMetadata(MetadataKeys.Policy.ReadDeny, readDenyColumn);
            })
            .Build();

    private static EnvelopeKeyManager NewManager()
    {
        var root = new byte[FieldCipher.KeySize];
        for (var i = 0; i < root.Length; i++) root[i] = (byte)(i + 7);
        return new EnvelopeKeyManager(new ConfigRootKeyProvider(root), new InMemoryDataEncryptionKeyStore());
    }

    private static QueryTransformerService Service(EnvelopeKeyManager? manager) =>
        new(new FilterTransformersWrap
        {
            Transformers = new IFilterTransformer[]
            {
                new EncryptedColumnReadGuard(),
                new BlindIndexColumnGuard(),
                new PolicyFilterTransformer(),
            },
        }, manager);

    private static IDictionary<string, object?> UserContext() =>
        new Dictionary<string, object?> { ["user_id"] = "user-1", ["roles"] = new[] { "user" } };

    private static GqlObjectQuery Query(
        IDbModel model,
        string[] columns,
        Action<BifrostQL.Core.QueryModel.TestFixtures.TableFilterBuilder>? filter = null)
    {
        var builder = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("secrets"))
            .WithColumns(columns);
        if (filter is not null)
            builder = builder.WithFilter(filter);
        return builder.Build();
    }

    // ---------------------------------------------------------------- schema

    [Fact]
    public void TypeDefinition_OmitsBlindIndexColumn()
    {
        var model = SecretsModel();
        var table = model.GetTableFromDbName("secrets");

        var sdl = new TableSchemaGenerator(table).GetTableTypeDefinition(model, includeDynamicJoins: false);

        sdl.Should().NotContain("ssn_bidx");
        sdl.Should().Contain("ssn");
    }

    [Fact]
    public void SortEnum_OmitsBlindIndexColumn()
    {
        var model = SecretsModel();
        var table = model.GetTableFromDbName("secrets");

        var sdl = new TableSchemaGenerator(table).GetTableSortEnumDefinition();

        sdl.Should().NotContain("ssn_bidx");
        sdl.Should().Contain("ssn_asc");
    }

    // ----------------------------------------------------------- query guard

    [Fact]
    public void SelectingBlindIndexColumn_IsDenied()
    {
        var model = SecretsModel();
        var query = Query(model, new[] { "id", "ssn_bidx" });

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage(BlindIndexColumnGuard.DeniedMessage);
    }

    [Fact]
    public void FilteringOnBlindIndexColumn_IsDenied()
    {
        var model = SecretsModel();
        var query = Query(model, new[] { "id" }, f => f.WhereEquals("ssn_bidx", "forged-token"));

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>()
            .WithMessage(BlindIndexColumnGuard.DeniedMessage);
    }

    [Fact]
    public void EqualityOnEncryptedColumn_StillRewrites_WithGuardRegistered()
    {
        // The server's own rewrite injects a ServerDerived node targeting the
        // shadow column; the guard must not fire on it.
        var model = SecretsModel();
        var query = Query(model, new[] { "id" }, f => f.WhereEquals("ssn", "123-45-6789"));

        Service(NewManager()).ApplyTransformers(query, model, UserContext());

        var parameters = new SqlParameterCollection();
        var rendered = query.Filter!.ToSqlParameterized(model, SqliteDialect.Instance, parameters);
        rendered.Sql.Should().Contain("\"ssn_bidx\"");
    }

    [Fact]
    public void PolicyReadDenyOnEncryptedColumn_DeniesEqualityProbe()
    {
        // The rewrite replaces `ssn` with `ssn_bidx` in the filter, which used to
        // remove the ORIGINAL column from the guard sets entirely — a policy
        // read-deny on `ssn` no longer bound, so the result set became an equality
        // oracle for the denied value. The rewrite now records the original column
        // for the read guards.
        var model = SecretsModel(readDenyColumn: "ssn");
        var query = Query(model, new[] { "id" }, f => f.WhereEquals("ssn", "123-45-6789"));

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    // -------------------------------------------------------- introspection

    [Fact]
    public void SchemaReadVisibility_OmitsBlindIndexColumn()
    {
        var model = SecretsModel();

        var visible = SchemaReadVisibility.Project(model, UserContext());

        var secrets = visible.Single(t => t.Table.DbName == "secrets");
        secrets.Columns.Should().NotContain(c => c.DbName == "ssn_bidx");
        secrets.Columns.Should().Contain(c => c.DbName == "ssn");
    }

    // ------------------------------------------------------------ model stamp

    [Fact]
    public void FromTables_StampsBlindIndexColumnHidden()
    {
        // The FromTables build path (production model construction) marks shadow
        // columns Ui.Visibility=hidden so every surface honoring visibility (LDAP,
        // chat connectors, aggregates) hides them too. Routed through the fixture's
        // foreign-key path, which builds via DbModel.FromTables.
        var model = DbModelTestFixture.Create()
            .WithTable("secrets", t => t
                .WithSchema("main")
                .WithPrimaryKey("id")
                .WithColumn("ssn", "nvarchar")
                .WithColumn("ssn_bidx", "nvarchar", isNullable: true)
                .WithColumn("owner_id", "int")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.KeyRef, KeyRef)
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.BlindIndex, "ssn_bidx"))
            .WithTable("owners", t => t
                .WithSchema("main")
                .WithPrimaryKey("id")
                .WithColumn("name"))
            .WithForeignKey("fk_secrets_owner", "secrets", "owner_id", "owners", "id", schema: "main")
            .Build();

        var column = model.GetTableFromDbName("secrets").ColumnLookup["ssn_bidx"];
        column.CompareMetadata(MetadataKeys.Ui.Visibility, MetadataKeys.Ui.Hidden).Should().BeTrue();
    }
}
