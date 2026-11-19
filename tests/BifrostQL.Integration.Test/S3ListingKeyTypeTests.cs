using System.Data.Common;
using System.Globalization;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.QueryModel;
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
/// Every primary-key type that produces an S3 key lists on every engine. For each type the
/// listing (ordered and bounded in SQL on the dialect's key expression) must equal the keys
/// C# builds from the rows the read pipeline returns (<see cref="S3ObjectKeyMap.KeyFor"/>),
/// sorted ordinally: same text byte for byte, no gap, no duplicate. Values include each
/// type's boundaries (negatives, zero, extremes, padding, minimum and maximum dates).
/// Pages are three keys, so every type crosses several keyset seeks.
/// </summary>
public abstract class S3ListingKeyTypeTestBase
{
    private const string Endpoint = "/graphql";
    private const int FloatSamples = 200;

    /// <summary>
    /// A key type: a table <c>okt{Name} (k {Type} PRIMARY KEY, body text)</c> seeded with
    /// <see cref="Literals"/>. No empty value: an empty key segment is not addressable
    /// (<see cref="S3ObjectKeyMap.KeyFor"/> refuses it).
    /// </summary>
    protected sealed record KeyTypeCase(string Name, string Type, IReadOnlyList<string> Literals, bool Refused = false);

    protected abstract IIntegrationTestDatabase CreateDatabase();
    protected abstract bool Available { get; }
    protected abstract IReadOnlyList<KeyTypeCase> Cases { get; }

    /// <summary>The DDL type of the file column.</summary>
    protected abstract string BodyType { get; }

    /// <summary>A SQL literal of a double (<paramref name="single"/>: of a single), exactly that value.</summary>
    protected abstract string FloatLiteral(string roundTripText, bool single);

    /// <summary>
    /// The column types of a double and a single column whose keys the engine renders; null
    /// where it has no such type or refuses it (a refusal is pinned in <see cref="Cases"/>).
    /// </summary>
    protected abstract (string? Double, string? Single) FloatTypes { get; }

    /// <summary>True when the engine stores subnormal floats (SQL Server flushes them to zero).</summary>
    protected virtual bool StoresSubnormals => true;

    private bool Storable(double value) => StoresSubnormals || value == 0 || Math.Abs(value) >= 2.2250738585072014E-308;

    /// <summary>Double boundary values the engine can store.</summary>
    protected IEnumerable<string> StorableDoubles => DoubleValues.Where(v => Storable(double.Parse(v, CultureInfo.InvariantCulture)));

    /// <summary>Single boundary values the engine can store.</summary>
    protected IEnumerable<string> StorableSingles =>
        SingleValues.Where(v => StoresSubnormals || Math.Abs(float.Parse(v, CultureInfo.InvariantCulture)) is 0 or >= 1.17549435E-38f);

    private static readonly Dictionary<string, object?> Caller = new() { ["user_id"] = "lister" };

    [SkippableFact]
    public async Task Every_key_type_lists_in_the_order_and_text_of_the_csharp_key()
    {
        Skip.IfNot(Available, "test database connection string not set");
        await RunAsync(Cases);
    }

    /// <summary>
    /// Seeded random doubles and singles across the whole exponent range, so the SQL
    /// re-layout of the shortest round-trip digits meets every notation .NET picks.
    /// </summary>
    [SkippableTheory]
    [InlineData(20260719)]
    public async Task Random_floats_list_in_the_text_dotnet_formats(int seed)
    {
        Skip.IfNot(Available, "test database connection string not set");
        var (doubleType, singleType) = FloatTypes;
        Skip.If(doubleType == null && singleType == null, "this engine refuses float keys (pinned in Cases)");
        var random = new Random(seed);
        var doubles = new HashSet<string>(StringComparer.Ordinal);
        var singles = new HashSet<string>(StringComparer.Ordinal);
        while (doubles.Count < FloatSamples)
        {
            var d = BitConverter.Int64BitsToDouble(random.NextInt64());
            if (double.IsFinite(d) && Storable(d))
                doubles.Add(d.ToString("R", CultureInfo.InvariantCulture));
            var exp = random.Next(-30, 31);
            doubles.Add((random.NextDouble() * Math.Pow(10, exp)).ToString("R", CultureInfo.InvariantCulture));
        }
        while (singles.Count < FloatSamples)
        {
            var f = BitConverter.Int32BitsToSingle(random.Next() ^ (random.Next(2) << 31));
            if (float.IsFinite(f) && (StoresSubnormals || Math.Abs(f) is 0 or >= 1.17549435E-38f))
                singles.Add(f.ToString("R", CultureInfo.InvariantCulture));
        }

        var cases = new List<KeyTypeCase>();
        if (doubleType != null)
            cases.Add(new("rnd_double", doubleType, doubles.Select(d => FloatLiteral(d, single: false)).ToList()));
        if (singleType != null)
            cases.Add(new("rnd_single", singleType, singles.Select(f => FloatLiteral(f, single: true)).ToList()));
        await RunAsync(cases);
    }

    /// <summary>
    /// Rows without a file are filtered in SQL, so a long run of them before the first object
    /// costs neither page space nor seeks: one max-keys=1 page finds the object past 80 such
    /// rows, where a C#-side skip would spend its whole seek budget
    /// (<c>S3Listing.MaxSeeksPerPage</c> × 2 rows) and hand back an empty truncated page.
    /// </summary>
    [SkippableFact]
    public async Task A_long_run_of_rows_without_a_file_costs_no_seek()
    {
        Skip.IfNot(Available, "test database connection string not set");
        var keyCase = new KeyTypeCase("nulls", Cases[0].Type, new[] { "99" });
        await using var db = CreateDatabase();
        await db.InitializeAsync();
        await SeedAsync(db, new[] { keyCase }, withNullFileRow: false);
        await using (var conn = db.ConnFactory.GetConnection())
        {
            await conn.OpenAsync();
            for (var i = 1; i <= 80; i++)
                await ExecAsync(conn, $"INSERT INTO {TableName(keyCase)} (k, body) VALUES ({i}, NULL)");
        }
        var lister = new S3Listing(await ReaderAsync(db, new[] { keyCase }),
            new S3Options { Endpoint = Endpoint, ContinuationTokenSecret = "s3-key-type-secret" });

        var page = await lister.ListObjectsV2Async(TableName(keyCase), null, null, 1, null, null, Caller);

        page.Objects.Select(o => o.Key).Should().Equal("body/99");
        page.IsTruncated.Should().BeFalse();
    }

    private async Task RunAsync(IReadOnlyList<KeyTypeCase> cases)
    {
        await using var db = CreateDatabase();
        await db.InitializeAsync();
        await SeedAsync(db, cases);
        var reader = await ReaderAsync(db, cases);
        var lister = new S3Listing(reader, new S3Options { Endpoint = Endpoint, ContinuationTokenSecret = "s3-key-type-secret" });
        var model = await reader.GetModelAsync(Endpoint);

        var failures = new List<string>();
        foreach (var keyCase in cases)
        {
            var bucket = TableName(keyCase);
            var table = model.Tables.Single(t => t.DbName == bucket);
            if (keyCase.Refused)
            {
                var refused = async () => await lister.ListObjectsV2Async(bucket, null, null, 3, null, null, Caller);
                var thrown = await refused.Should().ThrowAsync<S3ProtocolException>();
                thrown.Which.Code.Should().Be("InvalidArgument");
                continue;
            }

            var expected = (await CSharpKeysAsync(reader, table))
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            List<string> listed;
            try
            {
                listed = await WalkAsync(lister, bucket);
            }
            catch (Exception ex)
            {
                failures.Add($"{keyCase.Name} ({keyCase.Type}): listing threw {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            if (!listed.SequenceEqual(expected, StringComparer.Ordinal))
            {
                var missing = expected.Except(listed, StringComparer.Ordinal).Take(5);
                var extra = listed.Except(expected, StringComparer.Ordinal).Take(5);
                failures.Add($"{keyCase.Name} ({keyCase.Type}): expected [{string.Join(", ", expected.Take(12))}] "
                    + $"listed [{string.Join(", ", listed.Take(12))}] missing [{string.Join(", ", missing)}] extra [{string.Join(", ", extra)}]");
            }
            expected.Should().OnlyHaveUniqueItems($"each {keyCase.Name} row has its own key");
            expected.Should().HaveCount(keyCase.Literals.Count, $"every seeded {keyCase.Name} row except the NULL-file one is an object");
        }

        failures.Should().BeEmpty();
    }

    /// <summary>The keys C# builds for every row with a file, through the read pipeline.</summary>
    private static async Task<List<string>> CSharpKeysAsync(QueryIntentExecutor reader, IDbTable table)
    {
        var query = new GqlObjectQuery
        {
            DbTable = table,
            SchemaName = table.TableSchema,
            TableName = table.DbName,
            GraphQlName = table.GraphQlName,
            Path = table.GraphQlName,
            Limit = -1,
        };
        query.ScalarColumns.Add(new GqlObjectColumn("k", "k"));
        query.ScalarColumns.Add(new GqlObjectColumn("body", "body"));
        var result = await reader.ExecuteAsync(new QueryIntent { Query = query, UserContext = Caller, Endpoint = Endpoint });
        var body = table.ColumnLookup["body"];
        return result.Rows
            .Where(r => r["body"] is not null)
            .Select(r => S3ObjectKeyMap.KeyFor(body, new[] { r["k"] }))
            .ToList();
    }

    private static async Task<List<string>> WalkAsync(S3Listing lister, string bucket)
    {
        var keys = new List<string>();
        string? token = null;
        var pages = 0;
        do
        {
            var page = await lister.ListObjectsV2Async(bucket, null, null, 3, token, null, Caller);
            keys.AddRange(page.Objects.Select(o => o.Key));
            token = page.NextContinuationToken;
            (++pages).Should().BeLessThan(1000, "pagination must terminate");
        } while (token is not null);
        return keys;
    }

    private async Task SeedAsync(IIntegrationTestDatabase db, IReadOnlyList<KeyTypeCase> cases, bool withNullFileRow = true)
    {
        await using var conn = db.ConnFactory.GetConnection();
        await conn.OpenAsync();
        var pointer = new FileMetadata
        {
            FileKey = "key",
            Size = 1,
            ETag = "etag",
            UploadedAt = new DateTime(2026, 07, 16, 8, 30, 00, DateTimeKind.Utc),
            ContentType = "application/octet-stream",
        }.ToJson();
        foreach (var keyCase in cases)
        {
            var table = TableName(keyCase);
            await ExecAsync(conn, $"CREATE TABLE {table} (k {keyCase.Type} NOT NULL PRIMARY KEY, body {BodyType} NULL)");
            foreach (var literal in keyCase.Literals)
                await ExecAsync(conn, $"INSERT INTO {table} (k, body) VALUES ({literal}, @body)", ("@body", pointer));
        }

        // A row with no file is no object: it must not list (the filter runs in SQL).
        if (!withNullFileRow)
            return;
        var first = cases[0];
        await ExecAsync(conn, $"INSERT INTO {TableName(first)} (k, body) VALUES ({NullFileKeyLiteral(first)}, NULL)");
    }

    /// <summary>The case's table, which is also its bucket: a legal S3 bucket name.</summary>
    private static string TableName(KeyTypeCase keyCase) => "okt" + keyCase.Name.Replace("_", "");

    /// <summary>A key literal for <paramref name="keyCase"/> not among its seeded values, for the NULL-file row.</summary>
    protected virtual string NullFileKeyLiteral(KeyTypeCase keyCase) => "7";

    private static async Task<QueryIntentExecutor> ReaderAsync(IIntegrationTestDatabase db, IReadOnlyList<KeyTypeCase> cases)
    {
        var rules = cases.Select(c => $"*.{TableName(c)}.body {{ file: json }}").ToArray();
        var model = await new DbModelLoader(db.ConnFactory, new MetadataLoader(rules)).LoadAsync();
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

    /// <summary>Double boundary and notation cases as round-trip text.</summary>
    protected static readonly string[] DoubleValues =
    {
        "0", "-1.5", "0.1", "0.30000000000000004", "123", "1E-05", "0.0001", "0.00012", "1E+16", "1E+17",
        "1.5E+16", "12345678901234567", "1.2345678901234568E+20", "1.7976931348623157E+308", "-1.7976931348623157E+308",
        "5E-324", "2.2250738585072014E-308", "100", "-0.001",
    };

    /// <summary>Single boundary and notation cases as round-trip text.</summary>
    protected static readonly string[] SingleValues =
    {
        "0", "-1.5", "0.1", "100000000", "1E+09", "123456790", "1E-05", "0.0001", "3.4028235E+38", "-3.4028235E+38",
        "1E-45", "16777216", "0.3",
    };
}

public sealed class SqliteS3ListingKeyTypeTests : S3ListingKeyTypeTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new SqliteTestDatabase();
    protected override bool Available => true;
    protected override string BodyType => "TEXT";
    protected override (string? Double, string? Single) FloatTypes => (null, null);
    protected override string FloatLiteral(string roundTripText, bool single) => roundTripText;

    protected override IReadOnlyList<KeyTypeCase> Cases => new KeyTypeCase[]
    {
        new("int", "INTEGER", new[] { "-9223372036854775808", "-1", "0", "1", "10", "9", "9223372036854775807" }),
        new("real", "REAL", new[] { "0.1", "1.5" }, Refused: true),
        new("numeric", "NUMERIC", new[] { "1.50", "2", "'abc'", "-0.25", "1e20" }),
        new("text", "TEXT", new[] { "'a b'", "'café'", "'a/b'", "'Z'", "' a'" }),
        new("uuid", "UUID", new[] { "'A0EEBC99-9C0B-4EF8-BB6D-6BB9BD380A11'", "'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a12'" }),
        new("datetime", "DATETIME", new[] { "'2024-01-02 03:04:05'", "'0001-01-01'", "'9999-12-31 23:59:59.999'" }),
        new("bool", "BOOLEAN", new[] { "0", "1" }),
        new("blob", "BLOB", new[] { "X'00'", "X'FF'", "X'010203'", "X'FBFF'", "X'FBFFBF'", "X'0102030405'" }),
        new("mixed", "", new[] { "3", "'b'", "2.5", "X'01'" }),
    };
}

public sealed class PostgresS3ListingKeyTypeTests : S3ListingKeyTypeTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new PostgresTestDatabase();
    protected override bool Available => PostgresTestDatabase.ConnectionString is not null;
    protected override string BodyType => "TEXT";
    protected override (string? Double, string? Single) FloatTypes => ("double precision", "real");
    protected override string FloatLiteral(string roundTripText, bool single) =>
        $"CAST('{roundTripText}' AS {(single ? "real" : "double precision")})";

    protected override IReadOnlyList<KeyTypeCase> Cases => new KeyTypeCase[]
    {
        new("int", "integer", new[] { "-2147483648", "-1", "0", "1", "10", "9", "2147483647" }),
        new("smallint", "smallint", new[] { "-32768", "0", "32767" }),
        new("bigint", "bigint", new[] { "-9223372036854775808", "0", "9223372036854775807" }),
        new("numeric", "numeric(10,2)", new[] { "-99999999.99", "-0.50", "0", "0.50", "1", "99999999.99" }),
        new("numeric_free", "numeric", new[] { "1.500", "0", "2", "-0.001", "123456789012345678.0123456789" }),
        new("real", "real", SingleValues.Select(v => FloatLiteral(v, true)).ToList()),
        new("double", "double precision",
            DoubleValues.Select(v => FloatLiteral(v, false)).Concat(new[] { "'NaN'", "'Infinity'", "'-Infinity'" }).ToList()),
        new("bool", "boolean", new[] { "true", "false" }),
        new("char", "char(5)", new[] { "'ab'", "'a'", "'abcde'", "' a'", "'é'" }),
        new("varchar", "varchar(20)", new[] { "'ab'", "'a b'", "'é'", "'a/b'" }),
        new("uuid", "uuid", new[] { "'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a11'", "'A0EEBC99-9C0B-4EF8-BB6D-6BB9BD380A12'", "'00000000-0000-0000-0000-000000000000'" }),
        new("date", "date", new[] { "'0001-01-01'", "'2024-01-02'", "'9999-12-31'" }),
        new("time", "time", new[] { "'00:00:00'", "'23:59:59.999999'", "'03:04:05.5'" }),
        new("timetz", "timetz", new[] { "'03:04:05+02'", "'03:04:05.5-05:30'", "'00:00:00+00'" }),
        new("timestamp", "timestamp", new[] { "'0001-01-01 00:00:00'", "'2024-01-02 03:04:05.123456'", "'9999-12-31 23:59:59.999999'" }),
        new("timestamptz", "timestamptz", new[] { "'2024-01-02 03:04:05+02'", "'2024-01-02 03:04:05.5-05:30'", "'1970-01-01 00:00:00+00'" }),
        new("interval", "interval", new[] { "'1 day 02:03:04'", "'-00:05:00'", "'0'", "'1 year 2 mons'" }),
        new("bytea", "bytea", new[] { "'\\x'", "'\\x00ff'", "'\\xfbff'" }),
        new("money", "money", new[] { "12.50", "-1", "0" }),
        new("inet", "inet", new[] { "'10.0.0.1'", "'::1'", "'192.168.0.0/16'" }),
    };
}

public sealed class MySqlS3ListingKeyTypeTests : S3ListingKeyTypeTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new MySqlTestDatabase();
    protected override bool Available => MySqlTestDatabase.ConnectionString is not null;
    protected override string BodyType => "TEXT";
    protected override (string? Double, string? Single) FloatTypes => ("DOUBLE", null);
    protected override string FloatLiteral(string roundTripText, bool single) => roundTripText;

    protected override IReadOnlyList<KeyTypeCase> Cases => new KeyTypeCase[]
    {
        new("int", "INT", new[] { "-2147483648", "-1", "0", "1", "10", "9", "2147483647" }),
        new("tinyint", "TINYINT", new[] { "-128", "0", "127" }),
        new("tinyint1", "TINYINT(1)", new[] { "0", "1" }),
        new("utinyint", "TINYINT UNSIGNED", new[] { "0", "255" }),
        new("smallint", "SMALLINT", new[] { "-32768", "32767" }),
        new("mediumint", "MEDIUMINT", new[] { "-8388608", "8388607" }),
        new("bigint", "BIGINT", new[] { "-9223372036854775808", "0", "9223372036854775807" }),
        new("ubigint", "BIGINT UNSIGNED", new[] { "0", "18446744073709551615" }),
        new("year", "YEAR", new[] { "1901", "2024", "2155" }),
        new("bit", "BIT(8)", new[] { "b'0'", "b'11111111'", "b'101'" }),
        new("decimal", "DECIMAL(10,2)", new[] { "-99999999.99", "-0.50", "0", "0.50", "99999999.99" }),
        new("decimal28", "DECIMAL(30,28)", new[] { "1.0000000000000000000000000001", "-0.5", "0" }),
        new("double", "DOUBLE", DoubleValues),
        new("float", "FLOAT", new[] { "0.1", "1" }, Refused: true),
        new("date", "DATE", new[] { "'1000-01-01'", "'2024-01-02'", "'9999-12-31'" }),
        new("datetime", "DATETIME(6)", new[] { "'1000-01-01 00:00:00'", "'2024-01-02 03:04:05.123456'", "'9999-12-31 23:59:59.999999'" }),
        new("datetime0", "DATETIME", new[] { "'2024-01-02 03:04:05'", "'2024-01-02 03:04:06'" }),
        new("timestamp", "TIMESTAMP(6)", new[] { "'1970-01-02 00:00:00'", "'2038-01-19 03:14:07.999999'" }),
        new("time", "TIME(6)", new[] { "'-838:59:59'", "'838:59:59'", "'00:00:00'", "'-00:00:00.5'", "'03:04:05.123'", "'-01:00:00'", "'24:00:00'" }),
        new("char", "CHAR(5) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", new[] { "'ab'", "' a'", "'abcde'", "'é'" }),
        new("guid", "CHAR(36)", new[] { "'A0EEBC99-9C0B-4EF8-BB6D-6BB9BD380A11'", "'a0eebc99-9c0b-4ef8-bb6d-6bb9bd380a12'" }),
        new("varchar", "VARCHAR(20) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", new[] { "'a b'", "'é'", "'a/b'" }),
        new("binary", "BINARY(4)", new[] { "x'00010203'", "x'FBFFBF00'" }),
        new("varbinary", "VARBINARY(10)", new[] { "x'00'", "x'FBFF'", "x'0102030405'" }),
        new("enum", "ENUM('b','a','c d')", new[] { "'a'", "'b'", "'c d'" }),
    };
}

public sealed class SqlServerS3ListingKeyTypeTests : S3ListingKeyTypeTestBase
{
    protected override IIntegrationTestDatabase CreateDatabase() => new SqlServerTestDatabase();
    protected override bool Available => SqlServerTestDatabase.ConnectionString is not null;
    protected override string BodyType => "NVARCHAR(MAX)";
    protected override (string? Double, string? Single) FloatTypes => (null, null);
    protected override bool StoresSubnormals => false;
    protected override string FloatLiteral(string roundTripText, bool single) =>
        $"CAST({(roundTripText.Contains('E') ? roundTripText : roundTripText + "E0")} AS {(single ? "REAL" : "FLOAT")})";

    protected override IReadOnlyList<KeyTypeCase> Cases => new KeyTypeCase[]
    {
        new("int", "INT", new[] { "-2147483648", "-1", "0", "1", "10", "9", "2147483647" }),
        new("tinyint", "TINYINT", new[] { "0", "255" }),
        new("smallint", "SMALLINT", new[] { "-32768", "32767" }),
        new("bigint", "BIGINT", new[] { "-9223372036854775808", "0", "9223372036854775807" }),
        new("bit", "BIT", new[] { "0", "1" }),
        new("decimal", "DECIMAL(10,2)", new[] { "-99999999.99", "-0.50", "0", "0.50", "99999999.99" }),
        new("decimal28", "DECIMAL(28,10)", new[] { "123456789012345678.0123456789", "-0.0000000001", "0" }),
        new("money", "MONEY", new[] { "-922337203685477.5808", "0", "12.5", "922337203685477.5807" }),
        new("smallmoney", "SMALLMONEY", new[] { "-214748.3648", "1.25", "214748.3647" }),
        new("float", "FLOAT", new[] { "0.1E0", "1.5E0" }, Refused: true),
        new("real", "REAL", new[] { "CAST(0.1E0 AS REAL)" }, Refused: true),
        new("date", "DATE", new[] { "'0001-01-01'", "'2024-01-02'", "'9999-12-31'" }),
        new("datetime", "DATETIME", new[] { "'1753-01-01T00:00:00'", "'2024-01-02T03:04:05.997'", "'9999-12-31T23:59:59.997'" }),
        new("datetime2", "DATETIME2", new[] { "'0001-01-01'", "'2024-01-02T03:04:05.1234567'", "'9999-12-31T23:59:59.9999999'" }),
        new("smalldatetime", "SMALLDATETIME", new[] { "'1900-01-01T00:00:00'", "'2024-01-02T03:04:00'" }),
        new("datetimeoffset", "DATETIMEOFFSET", new[] { "'2024-01-02T03:04:05.5-05:30'", "'2024-01-02T03:04:05+00:00'", "'0001-01-01T14:00:00+14:00'", "'9999-12-31T09:59:59.9999999-14:00'" }),
        new("time", "TIME", new[] { "'00:00:00'", "'23:59:59.9999999'", "'03:04:05.12'" }),
        new("guid", "UNIQUEIDENTIFIER", new[] { "'A0EEBC99-9C0B-4EF8-BB6D-6BB9BD380A11'", "'00000000-0000-0000-0000-000000000000'", "'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF'" }),
        new("char", "CHAR(5)", new[] { "'ab'", "' a'", "'abcde'" }),
        new("nchar", "NCHAR(5)", new[] { "N'ab'", "N'é'", "N'\U0001F600'" }),
        new("varchar", "VARCHAR(20)", new[] { "'a b'", "'a/b'", "'~'" }),
        new("nvarchar", "NVARCHAR(20)", new[] { "N'\u00E9'", "N'\U0001F600'", "N'\uE000'", "N'\u4E2D'", "N'a~'" }),
        new("binary", "BINARY(4)", new[] { "0x00010203", "0xFBFFBF00" }),
        new("varbinary", "VARBINARY(10)", new[] { "0x00", "0xFBFF", "0x0102030405" }),
    };
}

/// <summary>
/// The key SQL avoids constructs newer than each engine's floor, so older servers list too:
/// no <c>STRING_AGG</c> (SQL Server 2017) or <c>_UTF8</c> collation (2019), and no
/// <c>REGEXP_REPLACE</c> on MySQL (8.0 only; its <c>$1</c> backreference is also spelled
/// <c>\1</c> on MariaDB).
/// </summary>
public sealed class S3ObjectKeySqlVersionFloorTests
{
    private static readonly ObjectKeySegmentColumn TextKey =
        new("t.k", "t.k", false, new ColumnDto { ColumnName = "k", DataType = "varchar" }, null);

    [Fact]
    public void Sql_server_text_key_needs_neither_string_agg_nor_a_utf8_collation()
    {
        var sql = BifrostQL.SqlServer.SqlServerDialect.Instance.ObjectKeyExpression(new[] { TextKey });
        var tokens = System.Text.RegularExpressions.Regex.Split(sql, @"[^A-Za-z0-9_]+");
        tokens.Should().Contain("UNICODE", "the expression this fact inspects is the UTF-8 walk");
        tokens.Should().NotContain("STRING_AGG");
        tokens.Should().NotContain(t => t.EndsWith("_UTF8", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MySql_text_key_needs_no_regexp_replace()
    {
        var sql = BifrostQL.MySql.MySqlDialect.Instance.ObjectKeyExpression(new[] { TextKey });
        var tokens = System.Text.RegularExpressions.Regex.Split(sql, @"[^A-Za-z0-9_]+");
        tokens.Should().Contain("UNHEX", "the expression this fact inspects is the byte-wise escape");
        tokens.Should().NotContain(t => t.StartsWith("REGEXP", StringComparison.OrdinalIgnoreCase));
    }
}
