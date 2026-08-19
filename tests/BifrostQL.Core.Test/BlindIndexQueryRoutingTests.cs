using BifrostQL.Core.Crypto;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Modules.Crypto;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Sqlite;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test;

/// <summary>
/// Unit coverage for the blind-index equality routing performed by
/// <see cref="QueryTransformerService"/> before <see cref="EncryptedColumnReadGuard"/>
/// rejects. Only <c>_eq</c>/<c>_in</c> on an encrypted column that carries a
/// <c>blind-index</c> sibling are rewritten to an equality/IN on the sibling column
/// (using the write path's token derivation); every other operator — and an encrypted
/// column with no sibling — stays rejected so the ciphertext is never an oracle. When
/// the key manager is unresolvable the rewrite fails closed rather than emitting a raw
/// predicate.
/// </summary>
public class BlindIndexQueryRoutingTests
{
    private const string KeyRef = "config:pii";

    private static IDbModel SecretsModel(bool withBlindIndex = true, string keyRef = KeyRef) =>
        DbModelTestFixture.Create()
            .WithTable("secrets", t =>
            {
                t.WithSchema("main")
                    .WithPrimaryKey("id")
                    .WithColumn("ssn", "nvarchar")
                    .WithColumn("ssn_bidx", "nvarchar")
                    .WithColumnMetadata("ssn", MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
                    .WithColumnMetadata("ssn", MetadataKeys.Crypto.KeyRef, keyRef);
                if (withBlindIndex)
                    t.WithColumnMetadata("ssn", MetadataKeys.Crypto.BlindIndex, "ssn_bidx");
            })
            .Build();

    // A parent table whose `secret` relationship points at the encrypted `secrets` table,
    // so a filter can nest the encrypted predicate inside a relationship traversal.
    private static IDbModel AccountsWithSecretModel() =>
        DbModelTestFixture.Create()
            .WithTable("secrets", t => t.WithSchema("main")
                .WithPrimaryKey("id")
                .WithColumn("ssn", "nvarchar")
                .WithColumn("ssn_bidx", "nvarchar")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.Encrypt, "aes-256-gcm")
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.KeyRef, KeyRef)
                .WithColumnMetadata("ssn", MetadataKeys.Crypto.BlindIndex, "ssn_bidx"))
            .WithTable("accounts", t => t.WithSchema("main")
                .WithPrimaryKey("id")
                .WithColumn("secret_id", "int"))
            .WithSingleLink("accounts", "secret_id", "secrets", "id", "secret")
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
            Transformers = new IFilterTransformer[] { new EncryptedColumnReadGuard() },
        }, manager);

    private static IDictionary<string, object?> UserContext() =>
        new Dictionary<string, object?> { ["roles"] = new[] { "user" } };

    private static GqlObjectQuery Query(IDbModel model, Action<BifrostQL.Core.QueryModel.TestFixtures.TableFilterBuilder> filter) =>
        GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("secrets"))
            .WithColumns("id")
            .WithFilter(filter)
            .Build();

    [Fact]
    public void EqualityOnEncryptedColumn_RewritesToBlindIndexSibling_SqlNeverTargetsCiphertext()
    {
        var model = SecretsModel();
        var manager = NewManager();
        var query = Query(model, f => f.WhereEquals("ssn", "123-45-6789"));

        Service(manager).ApplyTransformers(query, model, UserContext());

        var parameters = new SqlParameterCollection();
        var rendered = query.Filter!.ToSqlParameterized(model, SqliteDialect.Instance, parameters);

        // The predicate targets the blind-index sibling, never the ciphertext column.
        rendered.Sql.Should().Contain("\"ssn_bidx\"").And.NotContain("\"ssn\"");
        // The bound value is the write path's search token for the plaintext.
        var expected = BlindIndexComputer.ComputeSearchToken(manager, KeyRef, "123-45-6789");
        parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(expected);
    }

    [Fact]
    public void EqualityOnEncryptedColumn_ViaRelationshipWithSiblingPredicate_IsRewritten_NotRejected()
    {
        // The encrypted _eq sits inside a relationship filter next to a SIBLING predicate:
        // `secret: { ssn: {_eq}, id: {_eq} }`. That produces an AND wrapper whose Next is
        // null, so the old `Next.Next is null` leaf test mis-classified the RELATIONSHIP node
        // as a leaf, never routed the nested ssn _eq to its blind-index sibling, and the guard
        // rejected the whole query. Using the shared IsLeafColumnPredicate, the rewrite now
        // recurses into the relationship and routes the encrypted _eq like the flat case.
        var model = AccountsWithSecretModel();
        var manager = NewManager();
        var filter = TableFilter.FromObject(new Dictionary<string, object?>
        {
            ["secret"] = new Dictionary<string, object?>
            {
                ["ssn"] = new Dictionary<string, object?> { ["_eq"] = "123-45-6789" },
                ["id"] = new Dictionary<string, object?> { ["_eq"] = 5 },
            },
        }, "accounts");
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("accounts"))
            .WithColumns("id")
            .WithFilter(filter)
            .Build();

        // Before the fix this threw (the guard rejected the unrewritten encrypted _eq).
        Service(manager).ApplyTransformers(query, model, UserContext());

        var parameters = new SqlParameterCollection();
        var rendered = query.Filter!.ToSqlParameterized(model, SqliteDialect.Instance, parameters);
        // The nested encrypted predicate now targets the blind-index sibling, never the ciphertext.
        rendered.Sql.Should().Contain("ssn_bidx").And.NotContain("\"ssn\"");
        // Its bound value is the write path's search token for the plaintext.
        var expected = BlindIndexComputer.ComputeSearchToken(manager, KeyRef, "123-45-6789");
        parameters.Parameters.Select(p => p.Value).Should().Contain(expected);
    }

    [Fact]
    public void InOnEncryptedColumn_RewritesEachValueToBlindIndexTokens()
    {
        var model = SecretsModel();
        var manager = NewManager();
        var query = Query(model, f => f.WhereIn("ssn", "111-11-1111", "222-22-2222"));

        Service(manager).ApplyTransformers(query, model, UserContext());

        var parameters = new SqlParameterCollection();
        var rendered = query.Filter!.ToSqlParameterized(model, SqliteDialect.Instance, parameters);

        rendered.Sql.Should().Contain("\"ssn_bidx\"").And.NotContain("\"ssn\"");
        parameters.Parameters.Select(p => p.Value).Should().Equal(
            BlindIndexComputer.ComputeSearchToken(manager, KeyRef, "111-11-1111"),
            BlindIndexComputer.ComputeSearchToken(manager, KeyRef, "222-22-2222"));
    }

    [Theory]
    [InlineData("_gt")]
    [InlineData("_lt")]
    [InlineData("_gte")]
    [InlineData("_lte")]
    [InlineData("_contains")]
    [InlineData("_null")]
    public void NonEqualityOperatorOnEncryptedColumn_StaysRejected(string op)
    {
        var model = SecretsModel();
        var query = Query(model, f => f.Where("ssn", op, op == "_null" ? true : "x"));

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>()
            .Where(e => e.ErrorCode == BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public void BetweenOnEncryptedColumn_StaysRejected()
    {
        var model = SecretsModel();
        var query = Query(model, f => f.WhereBetween("ssn", "a", "z"));

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void SortOnEncryptedColumn_StaysRejected()
    {
        var model = SecretsModel();
        var query = GqlObjectQueryBuilder.Create()
            .WithDbTable(model.GetTableFromDbName("secrets"))
            .WithColumns("id")
            .WithSort("ssn_asc")
            .Build();

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void EqualityNullOnEncryptedColumn_IsNotRouted_StaysRejected()
    {
        // `_eq: null` is an IS NULL check, not a value search — routing it to the blind
        // index would compute a token of the empty string and match the wrong rows. It
        // is left in place so the guard rejects it.
        var model = SecretsModel();
        var query = Query(model, f => f.WhereEquals("ssn", null));

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void EqualityOnEncryptedColumn_WithNoBlindIndexSibling_StaysRejected()
    {
        // No blind-index sibling ⇒ equality is NOT routable (no partial oracle): the
        // encrypted column still rejects `_eq`.
        var model = SecretsModel(withBlindIndex: false);
        var query = Query(model, f => f.WhereEquals("ssn", "123-45-6789"));

        var act = () => Service(NewManager()).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>();
    }

    [Fact]
    public void EqualityOnEncryptedColumn_WithNoKeyManager_FailsClosed()
    {
        // The key manager is unresolvable, so no token can be derived. Reject rather
        // than fall through to a raw predicate on the ciphertext or the _bidx column.
        var model = SecretsModel();
        var query = Query(model, f => f.WhereEquals("ssn", "123-45-6789"));

        var act = () => Service(manager: null).ApplyTransformers(query, model, UserContext());

        act.Should().Throw<BifrostExecutionError>()
            .Where(e => e.ErrorCode == BifrostExecutionError.AccessDeniedCode);
    }

    [Fact]
    public void EqualityOnNonEncryptedColumn_PassesThroughUnchanged()
    {
        var model = SecretsModel();
        var query = Query(model, f => f.WhereEquals("id", 1));

        Service(NewManager()).ApplyTransformers(query, model, UserContext());

        var parameters = new SqlParameterCollection();
        var rendered = query.Filter!.ToSqlParameterized(model, SqliteDialect.Instance, parameters);
        rendered.Sql.Should().Contain("\"id\"").And.NotContain("bidx");
        parameters.Parameters.Should().ContainSingle().Which.Value.Should().Be(1);
    }
}
