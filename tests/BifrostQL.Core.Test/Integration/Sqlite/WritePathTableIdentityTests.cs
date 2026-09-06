using System.Data;
using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.Core.Test.Sqlite;

/// <summary>
/// Finding M11-w: the WRITE path resolved a client-supplied table name by BARE
/// DbName (<c>TryGetTableFromDbName(name, out …)</c> in
/// <see cref="MutationIntentExecutor"/> and the three File*Resolvers). On a model
/// carrying <c>sales.orders</c> AND <c>archive.orders</c> the bare lookup is
/// ambiguous and resolves to nothing, so a table that exists in two schemas was
/// UNWRITABLE through <see cref="IMutationIntentExecutor"/> and the file
/// resolvers. The rule: a schema-qualified name (<c>schema.name</c>) resolves
/// exactly; a bare name resolves only when unique; ambiguity and unknown answer
/// the SAME sanitized error via <see cref="BifrostErrorSink.LookupMiss"/>.
///
/// SQLite has one schema per database, so the two-schema fixture ATTACHes two
/// shared-cache in-memory databases as <c>sales</c> and <c>archive</c> (every
/// connection the factory hands out attaches them on open) and hand-builds the
/// DbModel — the loader hardcodes schema "main". Assertions read committed row
/// state in BOTH schemas, so "reached the intended table" and "left the sibling
/// untouched" are both proven.
/// </summary>
public sealed class WritePathTableIdentityTests : IAsyncLifetime
{
    private const string EndpointPath = "/graphql";

    private readonly string _mainConnString =
        $"Data Source=bifrost_write_identity_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly string _salesUri = $"file:bifrost_wti_sales_{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly string _archiveUri = $"file:bifrost_wti_archive_{Guid.NewGuid():N}?mode=memory&cache=shared";

    private SqliteConnection _keepAlive = null!;
    private AttachingConnFactory _connFactory = null!;
    private DbModel _model = null!;
    private RecordingStorageProvider _storage = null!;
    private FileStorageService _storageService = null!;

    public async Task InitializeAsync()
    {
        // The keep-alive holds the two attached shared-cache databases open for the
        // fixture's lifetime; every pooled connection attaches the same names.
        _keepAlive = new SqliteConnection(_mainConnString);
        await _keepAlive.OpenAsync();
        await Exec(_keepAlive, $"ATTACH DATABASE '{_salesUri}' AS sales");
        await Exec(_keepAlive, $"ATTACH DATABASE '{_archiveUri}' AS archive");

        var attach = new[]
        {
            $"ATTACH DATABASE '{_salesUri}' AS sales",
            $"ATTACH DATABASE '{_archiveUri}' AS archive",
        };
        _connFactory = new AttachingConnFactory(_mainConnString, attach);

        // Same DbName in two schemas: a bare-name resolve of "orders"/"docs" is
        // ambiguous, so the pre-fix write path refused every write to either.
        await Exec(_keepAlive, "CREATE TABLE sales.orders (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        await Exec(_keepAlive, "CREATE TABLE archive.orders (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        await Exec(_keepAlive, "INSERT INTO sales.orders(id, label) VALUES (1, 'sale-one')");
        await Exec(_keepAlive, "INSERT INTO archive.orders(id, label) VALUES (1, 'arch-one')");
        await Exec(_keepAlive, "CREATE TABLE sales.docs (id INTEGER PRIMARY KEY, file_data TEXT NULL)");
        await Exec(_keepAlive, "CREATE TABLE archive.docs (id INTEGER PRIMARY KEY, file_data TEXT NULL)");
        await Exec(_keepAlive, $"INSERT INTO sales.docs(id, file_data) VALUES (1, '{Pointer("sale-doc.bin")}')");
        await Exec(_keepAlive, $"INSERT INTO archive.docs(id, file_data) VALUES (1, '{Pointer("arch-doc.bin")}')");
        // Unique to one schema: a BARE name must keep resolving (the rule narrows
        // ambiguity, not bare names in general).
        await Exec(_keepAlive, "CREATE TABLE sales.labels (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        // Ambiguous bare name whose sales-side table is policy-locked: a qualified
        // name reaches a table the bare name could not, and the pipeline must still
        // be the one that says no (invariant 7 corollary: resolution is not a gate).
        await Exec(_keepAlive, "CREATE TABLE sales.locked (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        await Exec(_keepAlive, "CREATE TABLE archive.locked (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");
        // A DbName that itself contains a dot: the split is on the FIRST dot, so
        // `sales.dot.ted` resolves exactly and bare `dot.ted` (no schema `dot`)
        // falls through to the unique bare name — never to a different table.
        await Exec(_keepAlive, "CREATE TABLE sales.\"dot.ted\" (id INTEGER PRIMARY KEY, label TEXT NOT NULL)");

        _model = new DbModel
        {
            Tables = new IDbTable[]
            {
                Table("sales", "orders", ("id", "INTEGER", true), ("label", "TEXT", false)),
                Table("archive", "orders", ("id", "INTEGER", true), ("label", "TEXT", false)),
                Table("sales", "docs", ("id", "INTEGER", true), ("file_data", "TEXT", false)),
                Table("archive", "docs", ("id", "INTEGER", true), ("file_data", "TEXT", false)),
                Table("sales", "labels", ("id", "INTEGER", true), ("label", "TEXT", false)),
                Table("sales", "locked", ("id", "INTEGER", true), ("label", "TEXT", false)),
                Table("archive", "locked", ("id", "INTEGER", true), ("label", "TEXT", false)),
                Table("sales", "dot.ted", ("id", "INTEGER", true), ("label", "TEXT", false)),
            },
        };
        foreach (var table in _model.Tables.Where(t => t.DbName == "docs"))
            table.ColumnLookup["file_data"].Metadata[MetadataKeys.Storage.Config] = "provider:recording;bucket:bucket";
        _model.GetTableFromDbName("sales", "locked").Metadata[MetadataKeys.Policy.Actions] = "read";

        _storage = new RecordingStorageProvider("recording");
        var providerFactory = new StorageProviderFactory();
        providerFactory.RegisterProvider(_storage);
        _storageService = new FileStorageService(providerFactory);
    }

    public async Task DisposeAsync() => await _keepAlive.DisposeAsync();

    private static async Task Exec(SqliteConnection conn, string sql)
    {
        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> Scalar(string sql)
    {
        await using var cmd = new SqliteCommand(sql, _keepAlive);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static string Pointer(string fileKey) => new FileMetadata
    {
        FileKey = fileKey,
        ProviderType = "recording",
        BucketName = "bucket",
        Size = 10,
    }.ToJson();

    private static DbTable Table(string schema, string name, params (string Name, string Type, bool IsKey)[] fields)
    {
        var columns = fields.Select((field, i) => new ColumnDto
        {
            ColumnName = field.Name,
            GraphQlName = field.Name,
            DataType = field.Type,
            IsPrimaryKey = field.IsKey,
            OrdinalPosition = i + 1,
        }).ToArray();
        return new DbTable
        {
            TableSchema = schema,
            DbName = name,
            // The loader sanitizes GraphQL names; DbName keeps the raw identifier.
            GraphQlName = $"{schema}_{name.Replace('.', '_')}",
            NormalizedName = name,
            TableType = "BASE TABLE",
            ColumnLookup = columns.ToDictionary(c => c.DbName),
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName),
        };
    }

    private MutationIntentExecutor BuildExecutor(params IMutationTransformer[] transformers)
    {
        var pathCache = new PathCache<Inputs>();
        var model = _model;
        var factory = _connFactory;
        pathCache.AddLoader(EndpointPath, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["connFactory"] = factory,
        })));
        return new MutationIntentExecutor(pathCache, new MutationTransformersWrap
        {
            Transformers = transformers,
        });
    }

    private FileResolverTestContext FileContext(string table, string recordId, Dictionary<string, object?>? extra = null)
    {
        var arguments = new Dictionary<string, object?>
        {
            ["table"] = table,
            ["column"] = "file_data",
            ["recordId"] = recordId,
        };
        foreach (var kv in extra ?? new Dictionary<string, object?>())
            arguments[kv.Key] = kv.Value;
        return new FileResolverTestContext(
            _connFactory, _model, arguments, FileResolverTestWiring.Services(),
            FileResolverTestWiring.Executor(_model));
    }

    // ---- IMutationIntentExecutor: insert / update / delete / batch ---------

    [Fact]
    public async Task Insert_QualifiedName_WritesToTheIntendedSchemaOnly()
    {
        var executor = BuildExecutor();

        await executor.ExecuteAsync(new MutationIntent
        {
            Table = "sales.orders",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["id"] = 2L, ["label"] = "via-intent" },
            Endpoint = EndpointPath,
        });

        (await Scalar("SELECT label FROM sales.orders WHERE id = 2")).Should().Be("via-intent");
        (await Scalar("SELECT COUNT(*) FROM archive.orders")).Should().Be(1L,
            "the sibling schema's same-named table must be untouched");
    }

    [Fact]
    public async Task Update_QualifiedName_UpdatesTheIntendedSchemaOnly()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "archive.orders",
            Action = MutationIntentAction.Update,
            Data = new Dictionary<string, object?> { ["label"] = "arch-updated" },
            PrimaryKey = new object?[] { 1L },
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1);
        (await Scalar("SELECT label FROM archive.orders WHERE id = 1")).Should().Be("arch-updated");
        (await Scalar("SELECT label FROM sales.orders WHERE id = 1")).Should().Be("sale-one");
    }

    [Fact]
    public async Task Delete_QualifiedName_DeletesFromTheIntendedSchemaOnly()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteAsync(new MutationIntent
        {
            Table = "archive.orders",
            Action = MutationIntentAction.Delete,
            Data = new Dictionary<string, object?>(),
            PrimaryKey = new object?[] { 1L },
            Endpoint = EndpointPath,
        });

        result.AffectedRows.Should().Be(1);
        (await Scalar("SELECT COUNT(*) FROM archive.orders")).Should().Be(0L);
        (await Scalar("SELECT COUNT(*) FROM sales.orders")).Should().Be(1L);
    }

    [Fact]
    public async Task Batch_QualifiedName_ReachesTheIntendedSchemaOnly()
    {
        var executor = BuildExecutor();

        var result = await executor.ExecuteBatchAsync(new MutationBatchIntent
        {
            Table = "archive.orders",
            Actions = new[]
            {
                new MutationBatchAction(MutationIntentAction.Insert,
                    new Dictionary<string, object?> { ["id"] = 7L, ["label"] = "batch-row" }),
            },
            Endpoint = EndpointPath,
        });

        result.TotalAffected.Should().Be(1);
        (await Scalar("SELECT label FROM archive.orders WHERE id = 7")).Should().Be("batch-row");
        (await Scalar("SELECT COUNT(*) FROM sales.orders WHERE id = 7")).Should().Be(0L);
    }

    [Fact]
    public async Task BareName_WhenUniqueAcrossSchemas_StillResolves()
    {
        var executor = BuildExecutor();

        await executor.ExecuteAsync(new MutationIntent
        {
            Table = "labels",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["id"] = 1L, ["label"] = "bare-ok" },
            Endpoint = EndpointPath,
        });

        (await Scalar("SELECT label FROM sales.labels WHERE id = 1")).Should().Be("bare-ok");
    }

    /// <summary>
    /// A dotted DbName is split on the FIRST dot only: `sales.dot.ted` resolves
    /// exactly, and bare `dot.ted` cannot match a qualified key (no schema `dot`),
    /// so it falls through to the unique bare name. Both land on the same table.
    /// </summary>
    [Theory]
    [InlineData("sales.dot.ted", 1L)]
    [InlineData("dot.ted", 2L)]
    public async Task DottedDbName_QualifiedAndBare_BothReachTheSameTable(string clientName, long id)
    {
        var executor = BuildExecutor();

        await executor.ExecuteAsync(new MutationIntent
        {
            Table = clientName,
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["id"] = id, ["label"] = clientName },
            Endpoint = EndpointPath,
        });

        (await Scalar($"SELECT label FROM sales.\"dot.ted\" WHERE id = {id}")).Should().Be(clientName);
    }

    /// <summary>
    /// Invariant 3/9: an ambiguous bare name and an unknown name are the SAME
    /// client-facing condition — byte-identical sanitized wire text, and the
    /// caller-supplied name never appears in it (the detail with the name stays
    /// server-side in <see cref="BifrostErrorSink"/>).
    /// </summary>
    [Fact]
    public async Task AmbiguousBareName_AndUnknownName_ProduceByteIdenticalWireError()
    {
        var executor = BuildExecutor();

        MutationIntent Intent(string table) => new()
        {
            Table = table,
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["id"] = 9L, ["label"] = "x" },
            Endpoint = EndpointPath,
        };

        var ambiguous = await Assert.ThrowsAsync<BifrostExecutionError>(
            () => executor.ExecuteAsync(Intent("orders")));
        var unknown = await Assert.ThrowsAsync<BifrostExecutionError>(
            () => executor.ExecuteAsync(Intent("no_such_table")));

        unknown.Message.Should().Be(ambiguous.Message,
            "unknown and ambiguous are one wire condition (invariant 3)");
        ambiguous.Message.Should().NotContain("orders").And.NotContain("sales").And.NotContain("archive");
    }

    /// <summary>
    /// Invariant 7 corollary: <c>TryGetTableFromClientName</c> is a lookup, not a
    /// visibility gate. A qualified name reaches a policy-locked table the bare
    /// name could not, and the PIPELINE still refuses it with its own
    /// access-denied signal and writes nothing.
    /// </summary>
    [Fact]
    public async Task PolicyDeniedWrite_QualifiedName_IsRefusedByThePipeline()
    {
        var executor = BuildExecutor(new PolicyMutationTransformer());

        var act = () => executor.ExecuteAsync(new MutationIntent
        {
            Table = "sales.locked",
            Action = MutationIntentAction.Insert,
            Data = new Dictionary<string, object?> { ["id"] = 1L, ["label"] = "denied" },
            Endpoint = EndpointPath,
        });

        (await act.Should().ThrowAsync<BifrostExecutionError>().WithMessage("*Access denied by authorization policy*"))
            .Which.ErrorCode.Should().Be(BifrostExecutionError.AccessDeniedCode);
        (await Scalar("SELECT COUNT(*) FROM sales.locked")).Should().Be(0L);
    }

    /// <summary>
    /// Invariant 9 symmetry: each file op class answers an ambiguous bare name and
    /// an unknown name with byte-identical wire text carrying no table name.
    /// </summary>
    [Theory]
    [InlineData("upload")]
    [InlineData("download")]
    [InlineData("delete")]
    public async Task FileResolvers_AmbiguousBareName_AndUnknownName_ProduceByteIdenticalWireError(string op)
    {
        var extra = new Dictionary<string, object?>
        {
            ["file"] = System.Text.Encoding.UTF8.GetBytes("hello"),
            ["filename"] = "note.txt",
            ["contentType"] = "text/plain",
        };
        Func<string, Task<object?>> resolve = op switch
        {
            "upload" => t => new FileUploadResolver(_storageService).ResolveAsync(FileContext(t, "1", extra)).AsTask(),
            "download" => t => new FileDownloadResolver(_storageService).ResolveAsync(FileContext(t, "1")).AsTask(),
            _ => t => new FileDeleteResolver(_storageService).ResolveAsync(FileContext(t, "1")).AsTask(),
        };

        var ambiguous = await Assert.ThrowsAsync<BifrostExecutionError>(() => resolve("docs"));
        var unknown = await Assert.ThrowsAsync<BifrostExecutionError>(() => resolve("no_such_table"));

        unknown.Message.Should().Be(ambiguous.Message);
        ambiguous.Message.Should().NotContain("docs").And.NotContain("sales").And.NotContain("archive");
    }

    // ---- File*Resolvers: upload / delete ------------------------------------

    [Fact]
    public async Task FileUpload_QualifiedName_RepointsTheRowInTheIntendedSchemaOnly()
    {
        var resolver = new FileUploadResolver(_storageService);

        var result = await resolver.ResolveAsync(FileContext("sales.docs", "1", new Dictionary<string, object?>
        {
            ["file"] = System.Text.Encoding.UTF8.GetBytes("hello"),
            ["filename"] = "note.txt",
            ["contentType"] = "text/plain",
        }));

        var upload = result.Should().BeOfType<FileUploadResult>().Subject;
        upload.Success.Should().BeTrue();
        (await Scalar("SELECT file_data FROM sales.docs WHERE id = 1")).As<string>()
            .Should().Contain(upload.FileKey);
        (await Scalar("SELECT file_data FROM archive.docs WHERE id = 1")).Should().Be(Pointer("arch-doc.bin"),
            "the sibling schema's same-named row keeps its pointer");
    }

    [Fact]
    public async Task FileDelete_QualifiedName_ClearsTheIntendedSchemaOnly()
    {
        var resolver = new FileDeleteResolver(_storageService);

        var result = await resolver.ResolveAsync(FileContext("archive.docs", "1"));

        result.Should().Be(true);
        (await Scalar("SELECT file_data FROM archive.docs WHERE id = 1")).Should().BeNull();
        (await Scalar("SELECT file_data FROM sales.docs WHERE id = 1")).Should().Be(Pointer("sale-doc.bin"));
        _storage.DeletedKeys.Should().ContainSingle().Which.Should().Be("arch-doc.bin");
    }

    // ---- fixture plumbing ----------------------------------------------------

    /// <summary>
    /// Hands out connections that ATTACH the two schema databases on open —
    /// ATTACH is connection-local in SQLite, and the pipeline opens a fresh
    /// connection per write.
    /// </summary>
    private sealed class AttachingConnFactory : IDbConnFactory
    {
        private readonly string _connectionString;
        private readonly string[] _attachSql;

        public AttachingConnFactory(string connectionString, string[] attachSql)
        {
            _connectionString = connectionString;
            _attachSql = attachSql;
        }

        public ISqlDialect Dialect => SqliteDialect.Instance;
        public ISchemaReader SchemaReader => new SqliteSchemaReader();
        public ITypeMapper TypeMapper => SqliteTypeMapper.Instance;

        public DbConnection GetConnection() => new AttachOnOpenConnection(_connectionString, _attachSql);
    }

    private sealed class AttachOnOpenConnection : DbConnection
    {
        private readonly SqliteConnection _inner;
        private readonly string[] _attachSql;

        public AttachOnOpenConnection(string connectionString, string[] attachSql)
        {
            _inner = new SqliteConnection(connectionString);
            _attachSql = attachSql;
        }

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get => _inner.ConnectionString; set => _inner.ConnectionString = value; }
        public override string Database => _inner.Database;
        public override string DataSource => _inner.DataSource;
        public override string ServerVersion => _inner.ServerVersion;
        public override ConnectionState State => _inner.State;

        public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
        public override void Close() => _inner.Close();

        public override void Open()
        {
            _inner.Open();
            Attach();
        }

        public override async Task OpenAsync(CancellationToken cancellationToken)
        {
            await _inner.OpenAsync(cancellationToken);
            Attach();
        }

        private void Attach()
        {
            foreach (var sql in _attachSql)
            {
                using var cmd = _inner.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
            => _inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => _inner.CreateCommand();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
