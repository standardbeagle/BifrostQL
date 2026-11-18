using System.Data.Common;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.Storage;
using BifrostQL.Integration.Test.Infrastructure;
using BifrostQL.Model;
using BifrostQL.Server.S3;
using FluentAssertions;
using GraphQL;

namespace BifrostQL.Integration.Test;

/// <summary>
/// ListObjectsV2 pages in S3 key order on every engine. The order and the page bounds come
/// from SQL (<c>ISqlDialect.ObjectKeyExpression</c> under a binary collation, a keyset seek,
/// and a <c>max-keys + 1</c> sentinel), so each fact runs against the real database: integer
/// keys whose key order is not numeric order, text keys holding LIKE metacharacters and
/// non-ASCII, prefixes, continuation, and IsTruncated on the last page.
/// </summary>
public abstract class S3ListingKeyOrderTestBase
{
    private const string Endpoint = "/graphql";
    private const int IntRows = 25;

    /// <summary>Text keys: LIKE metacharacters, unreserved and reserved ASCII, and non-ASCII.</summary>
    protected static readonly string[] TextKeys =
    {
        "a%b", "a_b", "a-b", "ab", "a b", "a%", "a_", "Z", "café", "\U0001F600", "",
    };

    protected abstract IIntegrationTestDatabase CreateDatabase();
    protected abstract bool Available { get; }

    /// <summary>DDL for <c>s3ints (id int PK, body text)</c> and <c>s3texts (slug varchar PK, body text)</c>.</summary>
    protected abstract IReadOnlyList<string> Ddl { get; }

    private static readonly string[] Rules =
    {
        "*.s3ints.body { file: json }",
        "*.s3texts.body { file: json }",
    };

    private static readonly Dictionary<string, object?> Caller = new() { ["user_id"] = "lister" };

    [SkippableFact]
    public async Task Integer_keys_page_in_utf8_order_with_no_duplicate_or_gap()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = await SeedAsync();
        var lister = await ListerAsync(db);

        var pages = await WalkAsync(lister, "s3ints", prefix: null, maxKeys: 10);

        var expected = Enumerable.Range(1, IntRows).Select(i => $"body/{i}").OrderBy(k => k, StringComparer.Ordinal).ToList();
        expected.Take(4).Should().Equal("body/1", "body/10", "body/11", "body/12");
        pages.SelectMany(p => p.Keys).Should().Equal(expected,
            "S3 key order is UTF-8 order on the key text (\"1\", \"10\"..\"19\", \"2\", \"20\"..), each key once");
        pages.Select(p => p.Keys.Count).Should().Equal(10, 10, 5);
        pages.Select(p => p.IsTruncated).Should().Equal(true, true, false);
    }

    [SkippableFact]
    public async Task IsTruncated_turns_false_exactly_when_the_page_holds_the_last_key()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = await SeedAsync();
        var lister = await ListerAsync(db);

        // body/1 and body/10..body/19: eleven keys under the prefix.
        var full = await lister.ListObjectsV2Async("s3ints", "body/1", null, 11, null, null, Caller);
        full.Objects.Should().HaveCount(11);
        full.IsTruncated.Should().BeFalse("eleven keys fill an eleven-key page with nothing after it");
        full.NextContinuationToken.Should().BeNull();

        var oneShort = await lister.ListObjectsV2Async("s3ints", "body/1", null, 10, null, null, Caller);
        oneShort.Objects.Select(o => o.Key).Should().Equal(
            new[] { "body/1" }.Concat(Enumerable.Range(10, 9).Select(i => $"body/{i}")));
        oneShort.IsTruncated.Should().BeTrue("body/19 lies past a ten-key page");

        var rest = await lister.ListObjectsV2Async("s3ints", "body/1", null, 10, oneShort.NextContinuationToken, null, Caller);
        rest.Objects.Select(o => o.Key).Should().Equal("body/19");
        rest.IsTruncated.Should().BeFalse();
    }

    [SkippableFact]
    public async Task Text_keys_with_like_metacharacters_and_non_ascii_page_in_utf8_order()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = await SeedAsync();
        var lister = await ListerAsync(db);
        var model = await (await ReaderAsync(db)).GetModelAsync(Endpoint);
        var body = model.Tables.Single(t => t.DbName == "s3texts").ColumnLookup["body"];

        var pages = await WalkAsync(lister, "s3texts", prefix: null, maxKeys: 3);

        var expected = TextKeys.Select(k => S3ObjectKeyMap.KeyFor(body, new object?[] { k }))
            .OrderBy(k => k, StringComparer.Ordinal).ToList();
        pages.SelectMany(p => p.Keys).Should().Equal(expected);
        pages.Select(p => p.IsTruncated).Should().Equal(true, true, true, false);
    }

    [SkippableFact]
    public async Task A_prefix_holding_like_metacharacters_matches_only_keys_that_start_with_it()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = await SeedAsync();
        var lister = await ListerAsync(db);

        // '%' escapes to %25 and '_' stays itself. A LIKE pattern built from the prefix
        // unescaped would let '%' and '_' match anything.
        var percent = await lister.ListObjectsV2Async("s3texts", "body/a%25", null, 10, null, null, Caller);
        percent.Objects.Select(o => o.Key).Should().Equal("body/a%25", "body/a%25b");

        var underscore = await lister.ListObjectsV2Async("s3texts", "body/a_", null, 10, null, null, Caller);
        underscore.Objects.Select(o => o.Key).Should().Equal("body/a_", "body/a_b");

        var walked = await WalkAsync(lister, "s3texts", prefix: "body/a", maxKeys: 2);
        walked.SelectMany(p => p.Keys).Should().Equal(
            "body/a%20b", "body/a%25", "body/a%25b", "body/a-b", "body/a_", "body/a_b", "body/ab");
        walked.Select(p => p.IsTruncated).Should().Equal(true, true, true, false);
    }

    [SkippableFact]
    public async Task A_supplementary_character_lists_after_a_private_use_bmp_character()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await using var db = await SeedAsync();
        var lister = await ListerAsync(db);

        // UTF-8: U+E000 is EE 80 80, U+1F600 is F0 9F 98 80, so S3 order puts U+E000 first.
        // UTF-16 order is the reverse (U+1F600 starts with the surrogate D83D < E000), which
        // is how SQL Server's _BIN2 compares the raw column. The listing orders the
        // percent-encoded key text instead, so it agrees with UTF-8 on every engine.
        var page = await lister.ListObjectsV2Async("s3texts", "body/%E", null, 10, null, null, Caller);
        page.Objects.Select(o => o.Key).Should().Equal("body/%EE%80%80");
        var all = await lister.ListObjectsV2Async("s3texts", "body/%", null, 10, null, null, Caller);
        all.Objects.Select(o => o.Key).Where(k => k.StartsWith("body/%E") || k.StartsWith("body/%F"))
            .Should().Equal("body/%EE%80%80", "body/%F0%9F%98%80");

        if (RawBinaryOrderSql is { } rawOrder)
        {
            var raw = await QueryStringsAsync(db, rawOrder);
            raw.Should().Equal(new[] { "\U0001F600", "" },
                "the engine's own binary collation on the raw column orders by UTF-16 code unit");
        }
    }

    /// <summary>
    /// When the engine's binary collation is not UTF-8 order on the raw column, a query
    /// returning the U+1F600 and U+E000 slugs ordered by it; null otherwise.
    /// </summary>
    protected virtual string? RawBinaryOrderSql => null;

    // ---- fixture --------------------------------------------------------------

    private sealed record ListedPage(IReadOnlyList<string> Keys, bool IsTruncated);

    private static async Task<List<ListedPage>> WalkAsync(S3Listing lister, string bucket, string? prefix, int maxKeys)
    {
        var pages = new List<ListedPage>();
        string? token = null;
        do
        {
            var page = await lister.ListObjectsV2Async(bucket, prefix, null, maxKeys, token, null, Caller);
            pages.Add(new ListedPage(page.Objects.Select(o => o.Key).ToList(), page.IsTruncated));
            token = page.NextContinuationToken;
            pages.Count.Should().BeLessThan(50, "pagination must terminate");
        } while (token is not null);
        return pages;
    }

    private async Task<IIntegrationTestDatabase> SeedAsync()
    {
        var db = CreateDatabase();
        await db.InitializeAsync();
        await using var conn = db.ConnFactory.GetConnection();
        await conn.OpenAsync();
        foreach (var sql in Ddl)
            await ExecAsync(conn, sql);

        var pointer = new FileMetadata
        {
            FileKey = "key",
            Size = 1,
            ETag = "etag",
            UploadedAt = new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc),
            ContentType = "application/octet-stream",
        }.ToJson();
        for (var i = 1; i <= IntRows; i++)
            await ExecAsync(conn, "INSERT INTO s3ints (id, body) VALUES (@id, @body)", ("@id", i), ("@body", pointer));
        foreach (var slug in TextKeys)
            await ExecAsync(conn, "INSERT INTO s3texts (slug, body) VALUES (@slug, @body)", ("@slug", slug), ("@body", pointer));
        return db;
    }

    private static async Task<QueryIntentExecutor> ReaderAsync(IIntegrationTestDatabase db)
    {
        var model = await new DbModelLoader(db.ConnFactory, new MetadataLoader(Rules)).LoadAsync();
        var pathCache = new PathCache<Inputs>();
        pathCache.AddLoader(Endpoint, () => Task.FromResult(new Inputs(new Dictionary<string, object?>
        {
            ["model"] = model,
            ["dbSchema"] = DbSchema.FromModel(model),
            ["connFactory"] = db.ConnFactory,
        })));
        return new QueryIntentExecutor(pathCache, new QueryTransformerService(new FilterTransformersWrap
        {
            Transformers = Array.Empty<IFilterTransformer>(),
        }));
    }

    private static async Task<S3Listing> ListerAsync(IIntegrationTestDatabase db) =>
        new(await ReaderAsync(db), new S3Options { Endpoint = Endpoint, ContinuationTokenSecret = "s3-key-order-secret" });

    private static async Task ExecAsync(DbConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> QueryStringsAsync(IIntegrationTestDatabase db, string sql)
    {
        await using var conn = db.ConnFactory.GetConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var values = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            values.Add(reader.GetString(0));
        return values;
    }
}

public sealed class SqliteS3ListingKeyOrderTests : S3ListingKeyOrderTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new SqliteTestDatabase();
    protected override bool Available => true;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE s3ints (id INTEGER PRIMARY KEY, body TEXT)",
        "CREATE TABLE s3texts (slug TEXT PRIMARY KEY, body TEXT)",
    };
}

public sealed class PostgresS3ListingKeyOrderTests : S3ListingKeyOrderTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new PostgresTestDatabase();
    protected override bool Available => PostgresTestDatabase.ConnectionString is not null;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE s3ints (id INT PRIMARY KEY, body TEXT)",
        "CREATE TABLE s3texts (slug TEXT PRIMARY KEY, body TEXT)",
    };
}

public sealed class MySqlS3ListingKeyOrderTests : S3ListingKeyOrderTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new MySqlTestDatabase();
    protected override bool Available => MySqlTestDatabase.ConnectionString is not null;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE s3ints (id INT PRIMARY KEY, body TEXT)",
        "CREATE TABLE s3texts (slug VARCHAR(100) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin PRIMARY KEY, body TEXT)",
    };
}

public sealed class SqlServerS3ListingKeyOrderTests : S3ListingKeyOrderTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new SqlServerTestDatabase();
    protected override bool Available => SqlServerTestDatabase.ConnectionString is not null;
    protected override IReadOnlyList<string> Ddl => new[]
    {
        "CREATE TABLE s3ints (id INT PRIMARY KEY, body NVARCHAR(MAX))",
        "CREATE TABLE s3texts (slug NVARCHAR(100) PRIMARY KEY, body NVARCHAR(MAX))",
    };

    protected override string RawBinaryOrderSql =>
        "SELECT slug FROM s3texts WHERE slug COLLATE Latin1_General_100_BIN2 IN (NCHAR(0xE000), NCHAR(0xD83D) + NCHAR(0xDE00)) " +
        "ORDER BY slug COLLATE Latin1_General_100_BIN2";
}
