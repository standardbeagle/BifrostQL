using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// M31 (harvest-found, H6 follow-up): DbModel signals table-not-found with
/// <see cref="ArgumentOutOfRangeException"/> whose message embeds the queried
/// name. Every client-reachable caller must instead surface a
/// <see cref="BifrostExecutionError"/> whose text carries NO caller-supplied
/// table/schema name. One fact per client-reachable caller, spanning all three
/// lookup overloads (closed enumeration):
/// <list type="bullet">
/// <item>GetTableFromDbName(string) — mutation intent (single + batch), query intent, file download/upload/delete resolvers.</item>
/// <item>GetTableByFullGraphQlName — the role-gated generic <c>_table</c> resolver.</item>
/// <item>GetTableFromDbName(schema, dbName) — no client-reachable caller exists
/// (its only caller, EavMetaProvider, resolves server-config-derived names and
/// already maps the miss onto a sanitized BifrostExecutionError); justification
/// recorded in the fix commit.</item>
/// </list>
/// </summary>
public sealed class TableLookupWireSafetyTests : IAsyncLifetime
{
    private const string ConnString = "Data Source=bifrost_table_lookup_wire_test;Mode=Memory;Cache=Shared";
    private const string EndpointPath = "/graphql";
    private const string PhantomTable = "m31_phantom_table";
    private SqliteConnection _keepAlive = null!;

    public async Task InitializeAsync()
    {
        _keepAlive = new SqliteConnection(ConnString);
        await _keepAlive.OpenAsync();
        await using var cmd = new SqliteCommand(
            "CREATE TABLE IF NOT EXISTS orders (id INTEGER PRIMARY KEY, name TEXT NOT NULL)", _keepAlive);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static MutationIntentExecutor BuildMutationExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["connFactory"] = factory,
            });
        });
        return new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = Array.Empty<IMutationTransformer>(),
        });
    }

    private static QueryIntentExecutor BuildQueryExecutor()
    {
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(EndpointPath, async () =>
        {
            var factory = new SqliteDbConnFactory(ConnString);
            var model = await new DbModelLoader(factory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
            return new Inputs(new Dictionary<string, object?>
            {
                ["model"] = model,
                ["dbSchema"] = DbSchema.FromModel(model),
                ["connFactory"] = factory,
            });
        });
        var transformerService = new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        });
        return new QueryIntentExecutor(pathCache, transformerService);
    }

    private static void AssertSanitized(Exception error)
    {
        error.Should().BeOfType<BifrostExecutionError>(
            "a client-shape table miss must surface as the adapter-owned execution error, " +
            "not a raw ArgumentOutOfRangeException whose message embeds the queried name");
        error.Message.Should().NotContain(PhantomTable,
            "wire-facing error text must not echo a caller-supplied table/schema name");
    }

    // ---- GetTableFromDbName(string): mutation intent seam ----------------

    [Fact]
    public async Task MutationIntent_UnknownTable_DoesNotEchoName()
    {
        var executor = BuildMutationExecutor();

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = PhantomTable,
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["name"] = "x" },
            Endpoint = EndpointPath,
        });

        AssertSanitized((await act.Should().ThrowAsync<Exception>()).Which);
    }

    [Fact]
    public async Task MutationBatchIntent_UnknownTable_DoesNotEchoName()
    {
        var executor = BuildMutationExecutor();

        var act = () => executor.ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = PhantomTable,
            Actions = new[]
            {
                new MutationBatchAction(
                    MutationIntentAction.Insert,
                    new Dictionary<string, object?> { ["name"] = "x" }),
            },
            Endpoint = EndpointPath,
        });

        AssertSanitized((await act.Should().ThrowAsync<Exception>()).Which);
    }

    // ---- GetTableFromDbName(string): query intent seam --------------------

    [Fact]
    public async Task QueryIntent_UnknownTable_DoesNotEchoName()
    {
        var executor = BuildQueryExecutor();
        // A phantom table from a DIFFERENT (stale) model: the intent seam re-resolves
        // the intent's table against the endpoint's model, and that lookup is where
        // the caller-supplied name used to leak.
        var phantom = DbModelTestFixture.Create()
            .WithTable(PhantomTable, t => t.WithPrimaryKey("id"))
            .Build()
            .GetTableFromDbName(PhantomTable);
        var query = new GqlObjectQuery
        {
            DbTable = phantom,
            SchemaName = phantom.TableSchema,
            TableName = phantom.DbName,
            GraphQlName = phantom.GraphQlName,
            Path = phantom.GraphQlName,
            ScalarColumns = { new GqlObjectColumn("id") },
        };

        var act = () => executor.ExecuteAsync(new QueryIntent
        {
            Query = query,
            Endpoint = EndpointPath,
        });

        AssertSanitized((await act.Should().ThrowAsync<Exception>()).Which);
    }

    // ---- GetTableFromDbName(string): file resolvers (GraphQL 'table' arg) -

    private static IDbModel FileModel() => DbModelTestFixture.Create()
        .WithTable("widget", t => t
            .WithPrimaryKey("id")
            .WithColumn("photo", "text", isNullable: true)
            .WithColumnMetadata("photo", MetadataKeys.FileStorage.File, "true"))
        .Build();

    private static FileResolverTestContext PhantomFileContext(IDbModel model, IDbConnFactory factory) =>
        new(factory, model, new Dictionary<string, object?>
        {
            ["table"] = PhantomTable,
            ["column"] = "photo",
            ["recordId"] = "1",
            ["file"] = new byte[] { 1, 2, 3 },
        });

    [Fact]
    public async Task FileDownload_UnknownTable_DoesNotEchoName()
    {
        var model = FileModel();
        var factory = new SqliteDbConnFactory(ConnString);
        var resolver = new FileDownloadResolver();

        var act = async () => await resolver.ResolveAsync(PhantomFileContext(model, factory));

        AssertSanitized((await act.Should().ThrowAsync<Exception>()).Which);
    }

    [Fact]
    public async Task FileUpload_UnknownTable_DoesNotEchoName()
    {
        var model = FileModel();
        var factory = new SqliteDbConnFactory(ConnString);
        var resolver = new FileUploadResolver();

        var act = async () => await resolver.ResolveAsync(PhantomFileContext(model, factory));

        AssertSanitized((await act.Should().ThrowAsync<Exception>()).Which);
    }

    [Fact]
    public async Task FileDelete_UnknownTable_DoesNotEchoName()
    {
        var model = FileModel();
        var factory = new SqliteDbConnFactory(ConnString);
        var resolver = new FileDeleteResolver();

        var act = async () => await resolver.ResolveAsync(PhantomFileContext(model, factory));

        AssertSanitized((await act.Should().ThrowAsync<Exception>()).Which);
    }

    // ---- GetTableByFullGraphQlName: generic _table resolver ---------------

    [Fact]
    public void GenericTable_UnknownTable_DoesNotEchoName()
    {
        var model = DbModelTestFixture.Create()
            .WithModelMetadata("generic-table", "enabled")
            .WithTable("widget", t => t.WithPrimaryKey("id"))
            .Build();
        var resolver = new GenericTableQueryResolver(model, GenericTableConfig.FromModel(model));

        var act = () => resolver.ResolveTable(PhantomTable);

        AssertSanitized(act.Should().Throw<Exception>().Which);
    }

    // ---- TryGet contract (all three overloads, closed enumeration) --------

    [Fact]
    public void TryGet_UnknownNames_ReturnFalse()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("widget", t => t.WithPrimaryKey("id"))
            .Build();

        model.TryGetTableFromDbName(PhantomTable, out _).Should().BeFalse();
        model.TryGetTableFromDbName("dbo", PhantomTable, out _).Should().BeFalse();
        model.TryGetTableByFullGraphQlName(PhantomTable, out _).Should().BeFalse();

        model.TryGetTableFromDbName("widget", out var byBare).Should().BeTrue();
        byBare.Should().NotBeNull();
        model.TryGetTableByFullGraphQlName("widget", out var byGraphQl).Should().BeTrue();
        byGraphQl.Should().NotBeNull();
    }

    // The fixture above is the test fake, so it exercises IDbModel's DEFAULT
    // TryGet bodies. The production DbModel OVERRIDES them over its own indexes;
    // this fact drives those overrides directly, including the one answer the
    // defaults and the overrides must agree on — a bare name defined in two
    // schemas is NOT a positive resolve, while the schema-qualified overload
    // still resolves each side.

    private static DbTable Table(string schema, string dbName)
    {
        var columns = new[]
        {
            new ColumnDto { ColumnName = "id", GraphQlName = "id", DataType = "int", OrdinalPosition = 1, IsPrimaryKey = true },
        };
        return new DbTable
        {
            DbName = dbName,
            GraphQlName = schema == "dbo" ? dbName : $"{schema}_{dbName}",
            NormalizedName = dbName.ToLowerInvariant(),
            TableSchema = schema,
            TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(c => c.DbName, StringComparer.OrdinalIgnoreCase),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object?>(),
        };
    }

    [Fact]
    public void DbModel_TryGetOverrides_ResolvePositivelyAndRefuseAmbiguity()
    {
        var model = new DbModel
        {
            Tables = new[] { Table("dbo", "widget"), Table("dbo", "gadget"), Table("sales", "gadget") },
            Metadata = new Dictionary<string, object?>(),
        };

        model.TryGetTableFromDbName(PhantomTable, out _).Should().BeFalse();
        model.TryGetTableFromDbName("dbo", PhantomTable, out _).Should().BeFalse();
        model.TryGetTableByFullGraphQlName(PhantomTable, out _).Should().BeFalse();

        model.TryGetTableFromDbName("widget", out var bare).Should().BeTrue();
        bare!.DbName.Should().Be("widget");
        model.TryGetTableByFullGraphQlName("sales_gadget", out var byFull).Should().BeTrue();
        byFull!.TableSchema.Should().Be("sales");
        model.TryGetTableByFullGraphQlName("gadget", out var byBareGraphQl).Should().BeTrue();
        byBareGraphQl!.TableSchema.Should().Be("dbo", "the bare GraphQL name is the dbo table's own name");

        // Ambiguous bare DbName: no positive resolve, and no exception carrying the name.
        model.TryGetTableFromDbName("gadget", out var ambiguous).Should().BeFalse();
        ambiguous.Should().BeNull();
        model.TryGetTableFromDbName("sales", "gadget", out var qualified).Should().BeTrue();
        qualified!.TableSchema.Should().Be("sales");
    }
}
