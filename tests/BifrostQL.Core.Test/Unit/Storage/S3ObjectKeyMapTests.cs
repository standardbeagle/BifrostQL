using BifrostQL.Core.Model;
using BifrostQL.Core.Storage;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Core.Test.Unit.Storage;

/// <summary>
/// Pins the deterministic bucket/object-key contract the S3 front door maps onto
/// file-bearing tables/columns.
///
/// The load-bearing property is INJECTIVITY: two distinct rows must never collapse
/// onto one object key. <see cref="FileMetadata.GenerateFileKey"/> — the pre-existing
/// key generator — cannot be used for this, because it (a) is non-deterministic
/// (timestamp + random suffix) so a key cannot be mapped back to a row, and (b)
/// sanitizes by replacing every invalid character with '_', which collapses the
/// distinct record ids "a/b" and "a_b" onto the same path segment.
/// </summary>
public sealed class S3ObjectKeyMapTests
{
    private static ColumnDto Column(string name, bool isPrimaryKey = false, string dataType = "nvarchar") =>
        new()
        {
            ColumnName = name,
            GraphQlName = name,
            NormalizedName = name.ToLowerInvariant(),
            DataType = dataType,
            IsPrimaryKey = isPrimaryKey,
            Metadata = new Dictionary<string, object?>(),
        };

    private static DbTable Table(string dbName, params ColumnDto[] columns)
    {
        var lookup = columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        return new DbTable
        {
            DbName = dbName,
            GraphQlName = dbName,
            NormalizedName = dbName.ToLowerInvariant(),
            TableSchema = "dbo",
            TableType = "TABLE",
            ColumnLookup = lookup,
            GraphQlLookup = columns.ToDictionary(c => c.GraphQlName, StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object?>(),
        };
    }

    private static DbModel Model(params IDbTable[] tables) =>
        new() { Tables = tables, Metadata = new Dictionary<string, object?>() };

    private static readonly ColumnDto FileColumn = Column("file_data");

    // ---- bucket naming -------------------------------------------------------

    [Fact]
    public void BucketNameFor_LowercasesTheTableName()
    {
        var table = Table("Documents", Column("id", isPrimaryKey: true, dataType: "int"), FileColumn);

        S3ObjectKeyMap.BucketNameFor(table).Should().Be("documents",
            "S3 bucket names are lowercase; the table name is the only source of the bucket name");
    }

    [Theory]
    [InlineData("order_items")]  // '_' is not legal in an S3 bucket name
    [InlineData("ab")]           // shorter than the 3-character minimum
    [InlineData("-leading")]     // must start alphanumeric
    [InlineData("trailing-")]    // must end alphanumeric
    [InlineData("192.168.1.1")]  // an IP-address-shaped name is reserved
    public void BucketNameFor_RejectsATableNameThatIsNotALegalBucketName(string tableName)
    {
        var table = Table(tableName, Column("id", isPrimaryKey: true, dataType: "int"), FileColumn);

        var act = () => S3ObjectKeyMap.BucketNameFor(table);

        act.Should().Throw<InvalidOperationException>(
            "a table that cannot be addressed as a bucket must be rejected honestly, never silently rewritten " +
            "into some other bucket name that could collide with a different table");
    }

    [Fact]
    public void ResolveBucket_ThrowsOnAmbiguity_RatherThanPickingOneTable()
    {
        // Two tables whose names differ only by case collapse onto one lowercase
        // bucket name. Picking either one would silently expose the wrong table's
        // rows; the collision must be surfaced.
        var model = Model(
            Table("Docs", Column("id", isPrimaryKey: true, dataType: "int"), FileColumn),
            Table("docs", Column("id", isPrimaryKey: true, dataType: "int"), FileColumn));

        var act = () => S3ObjectKeyMap.ResolveBucket(model, "docs");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ambiguous*");
    }

    [Fact]
    public void ResolveBucket_ThrowsOnUnknownBucket()
    {
        var model = Model(Table("documents", Column("id", isPrimaryKey: true, dataType: "int"), FileColumn));

        var act = () => S3ObjectKeyMap.ResolveBucket(model, "nope");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ResolveBucket_FindsTheTableCaseInsensitively()
    {
        var table = Table("Documents", Column("id", isPrimaryKey: true, dataType: "int"), FileColumn);
        var model = Model(table);

        S3ObjectKeyMap.ResolveBucket(model, "documents").DbName.Should().Be("Documents");
    }

    // ---- key mapping ---------------------------------------------------------

    [Fact]
    public void KeyFor_RoundTripsASingleColumnKey()
    {
        var id = Column("id", isPrimaryKey: true, dataType: "int");
        var table = Table("documents", id, FileColumn);

        var key = S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { 42 });
        var parsed = S3ObjectKeyMap.ParseKey(table, key);

        parsed.Column.ColumnName.Should().Be("file_data");
        parsed.PrimaryKey.Should().Equal(new object?[] { 42 });
    }

    [Fact]
    public void KeyFor_RoundTripsACompositeKey()
    {
        var tenant = Column("tenant_id", isPrimaryKey: true, dataType: "int");
        var doc = Column("doc_id", isPrimaryKey: true, dataType: "bigint");
        var table = Table("documents", tenant, doc, FileColumn);

        var key = S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { 7, 9007199254740993L });
        var parsed = S3ObjectKeyMap.ParseKey(table, key);

        parsed.PrimaryKey.Should().Equal(new object?[] { 7, 9007199254740993L },
            "a BigInt key component must survive the key round-trip without precision loss");
    }

    [Fact]
    public void KeyFor_IsInjective_ForKeyComponentsContainingTheSeparator()
    {
        // The pre-existing FileMetadata.GenerateFileKey sanitizer maps '/' to '_',
        // which would make these three distinct rows share one key.
        var keys = new[]
        {
            S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { "a/b" }),
            S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { "a_b" }),
            S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { "a", "b" }),
        };

        keys.Should().OnlyHaveUniqueItems("distinct rows must never collapse onto one object key");
    }

    [Fact]
    public void KeyFor_IsDeterministic()
    {
        var first = S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { 42 });
        var second = S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { 42 });

        first.Should().Be(second,
            "an S3 GET must address the same object every time; a timestamp/random-suffixed key cannot");
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData(".")]
    public void KeyFor_EncodesTraversalAttemptsInAKeyComponent(string hostileId)
    {
        var key = S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { hostileId });

        key.Should().NotStartWith("/", "an object key must never be absolute");
        key.Split('/').Should().NotContain(new[] { "..", "." },
            "no key component may survive encoding as a traversal segment");
    }

    [Fact]
    public void KeyFor_EncodedTraversalStillRoundTrips()
    {
        var id = Column("id", isPrimaryKey: true, dataType: "nvarchar");
        var table = Table("documents", id, FileColumn);

        var key = S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { "../../etc/passwd" });

        S3ObjectKeyMap.ParseKey(table, key).PrimaryKey.Should().Equal(new object?[] { "../../etc/passwd" },
            "encoding must be reversible, not lossy — a lossy escape is how two rows collide");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void KeyFor_RejectsAnEmptyOrNullKeyComponent(string? component)
    {
        // An empty component would produce an empty path segment, which
        // Path.Combine silently swallows — collapsing ("", "b") onto ("b").
        var act = () => S3ObjectKeyMap.KeyFor(FileColumn, new object?[] { component });

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParseKey_RejectsWrongKeyArity()
    {
        var tenant = Column("tenant_id", isPrimaryKey: true, dataType: "int");
        var doc = Column("doc_id", isPrimaryKey: true, dataType: "int");
        var table = Table("documents", tenant, doc, FileColumn);

        // One component supplied for a two-column key.
        var act = () => S3ObjectKeyMap.ParseKey(table, "file_data/7");

        act.Should().Throw<InvalidOperationException>(
            "a composite key must be addressed in full; a short key must not be padded or partially matched");
    }

    [Fact]
    public void ParseKey_RejectsAnUnknownColumn()
    {
        var table = Table("documents", Column("id", isPrimaryKey: true, dataType: "int"), FileColumn);

        var act = () => S3ObjectKeyMap.ParseKey(table, "not_a_column/1");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParseKey_RejectsAKeyComponentThatIsNotTheKeyColumnsType()
    {
        var table = Table("documents", Column("id", isPrimaryKey: true, dataType: "int"), FileColumn);

        var act = () => S3ObjectKeyMap.ParseKey(table, "file_data/not-an-int");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ParseKey_RejectsATableWithNoPrimaryKey()
    {
        var table = Table("documents", FileColumn);

        var act = () => S3ObjectKeyMap.ParseKey(table, "file_data/1");

        act.Should().Throw<InvalidOperationException>();
    }
}
