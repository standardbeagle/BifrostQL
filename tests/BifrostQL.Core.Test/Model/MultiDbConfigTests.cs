using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Core.QueryModel.TestFixtures;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

public class MultiDbConfigTests
{
    #region MultiDbConfig - Basic Configuration

    [Fact]
    public void AddDatabase_ValidConfig_AddsDatabase()
    {
        var config = new MultiDbConfig();

        config.AddDatabase(db =>
        {
            db.Alias = "userDb";
            db.ConnectionString = "Server=localhost;Database=Users;";
        });

        config.Databases.Should().HaveCount(1);
        config.Databases[0].Alias.Should().Be("userDb");
        config.Databases[0].ConnectionString.Should().Be("Server=localhost;Database=Users;");
    }

    [Fact]
    public void AddDatabase_MultipleDatabases_AddsAll()
    {
        var config = new MultiDbConfig();

        config
            .AddDatabase(db =>
            {
                db.Alias = "userDb";
                db.ConnectionString = "Server=localhost;Database=Users;";
            })
            .AddDatabase(db =>
            {
                db.Alias = "orderDb";
                db.ConnectionString = "Server=localhost;Database=Orders;";
            });

        config.Databases.Should().HaveCount(2);
        config.Databases[0].Alias.Should().Be("userDb");
        config.Databases[1].Alias.Should().Be("orderDb");
    }

    [Fact]
    public void AddDatabase_FluentChaining_ReturnsSameInstance()
    {
        var config = new MultiDbConfig();

        var result = config
            .AddDatabase(db =>
            {
                db.Alias = "db1";
                db.ConnectionString = "conn1";
            })
            .AddDatabase(db =>
            {
                db.Alias = "db2";
                db.ConnectionString = "conn2";
            });

        result.Should().BeSameAs(config);
    }

    #endregion

    #region MultiDbConfig - Alias Validation

    [Fact]
    public void AddDatabase_DuplicateAlias_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "userDb";
            db.ConnectionString = "conn1";
        });

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = "userDb";
            db.ConnectionString = "conn2";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*userDb*already configured*");
    }

    [Fact]
    public void AddDatabase_DuplicateAliasCaseInsensitive_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "UserDb";
            db.ConnectionString = "conn1";
        });

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = "userdb";
            db.ConnectionString = "conn2";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*already configured*");
    }

    [Fact]
    public void AddDatabase_EmptyAlias_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = "";
            db.ConnectionString = "conn1";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Alias*required*");
    }

    [Fact]
    public void AddDatabase_NullAlias_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();

        var act = () => config.AddDatabase(db =>
        {
            db.ConnectionString = "conn1";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Alias*required*");
    }

    [Theory]
    [InlineData("1startsWithDigit")]
    [InlineData("has-dash")]
    [InlineData("has space")]
    [InlineData("has.dot")]
    public void AddDatabase_InvalidGraphQlIdentifier_ThrowsArgumentException(string alias)
    {
        var config = new MultiDbConfig();

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = alias;
            db.ConnectionString = "conn1";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*not a valid GraphQL identifier*");
    }

    [Theory]
    [InlineData("validName")]
    [InlineData("_underscoreStart")]
    [InlineData("db1")]
    [InlineData("myDatabase_v2")]
    public void AddDatabase_ValidGraphQlIdentifier_Succeeds(string alias)
    {
        var config = new MultiDbConfig();

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = alias;
            db.ConnectionString = "conn1";
        });

        act.Should().NotThrow();
    }

    #endregion

    #region MultiDbConfig - ConnectionString Validation

    [Fact]
    public void AddDatabase_EmptyConnectionString_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = "db1";
            db.ConnectionString = "";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ConnectionString*required*");
    }

    [Fact]
    public void AddDatabase_NullConnectionString_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = "db1";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*ConnectionString*required*");
    }

    #endregion

    #region MultiDbConfig - Default Database

    [Fact]
    public void AddDatabase_SingleDefault_Succeeds()
    {
        var config = new MultiDbConfig();

        config.AddDatabase(db =>
        {
            db.Alias = "primary";
            db.ConnectionString = "conn1";
            db.IsDefault = true;
        });

        config.GetDefaultDatabase().Should().NotBeNull();
        config.GetDefaultDatabase()!.Alias.Should().Be("primary");
    }

    [Fact]
    public void AddDatabase_MultipleDefaults_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "db1";
            db.ConnectionString = "conn1";
            db.IsDefault = true;
        });

        var act = () => config.AddDatabase(db =>
        {
            db.Alias = "db2";
            db.ConnectionString = "conn2";
            db.IsDefault = true;
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*Only one database*default*db1*");
    }

    [Fact]
    public void GetDefaultDatabase_NoneSet_ReturnsNull()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "db1";
            db.ConnectionString = "conn1";
        });

        config.GetDefaultDatabase().Should().BeNull();
    }

    #endregion

    #region MultiDbConfig - GetDatabase

    [Fact]
    public void GetDatabase_ExistingAlias_ReturnsConfig()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "userDb";
            db.ConnectionString = "conn1";
        });

        var result = config.GetDatabase("userDb");
        result.ConnectionString.Should().Be("conn1");
    }

    [Fact]
    public void GetDatabase_CaseInsensitive_ReturnsConfig()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "UserDb";
            db.ConnectionString = "conn1";
        });

        var result = config.GetDatabase("userdb");
        result.ConnectionString.Should().Be("conn1");
    }

    [Fact]
    public void GetDatabase_NonExistent_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "db1";
            db.ConnectionString = "conn1";
        });

        var act = () => config.GetDatabase("nonexistent");
        act.Should().Throw<ArgumentException>()
            .WithMessage("*nonexistent*");
    }

    #endregion

    #region MultiDbConfig - Access Control

    [Fact]
    public void IsAccessAllowed_NoRestrictions_ReturnsTrue()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "publicDb";
            db.ConnectionString = "conn1";
        });

        config.IsAccessAllowed("publicDb", new[] { "user" }).Should().BeTrue();
    }

    [Fact]
    public void IsAccessAllowed_EmptyUserRoles_ReturnsTrueWhenNoRestriction()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "publicDb";
            db.ConnectionString = "conn1";
        });

        config.IsAccessAllowed("publicDb", Array.Empty<string>()).Should().BeTrue();
    }

    [Fact]
    public void IsAccessAllowed_MatchingRole_ReturnsTrue()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "adminDb";
            db.ConnectionString = "conn1";
            db.AllowedRoles = new[] { "admin", "superuser" };
        });

        config.IsAccessAllowed("adminDb", new[] { "user", "admin" }).Should().BeTrue();
    }

    [Fact]
    public void IsAccessAllowed_NoMatchingRole_ReturnsFalse()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "adminDb";
            db.ConnectionString = "conn1";
            db.AllowedRoles = new[] { "admin" };
        });

        config.IsAccessAllowed("adminDb", new[] { "user", "reader" }).Should().BeFalse();
    }

    [Fact]
    public void IsAccessAllowed_RoleCaseInsensitive_ReturnsTrue()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "adminDb";
            db.ConnectionString = "conn1";
            db.AllowedRoles = new[] { "Admin" };
        });

        config.IsAccessAllowed("adminDb", new[] { "admin" }).Should().BeTrue();
    }

    [Fact]
    public void IsAccessAllowed_EmptyUserRolesWithRestriction_ReturnsFalse()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "adminDb";
            db.ConnectionString = "conn1";
            db.AllowedRoles = new[] { "admin" };
        });

        config.IsAccessAllowed("adminDb", Array.Empty<string>()).Should().BeFalse();
    }

    #endregion

    #region MultiDbConfig - CrossJoin Configuration

    [Fact]
    public void AddCrossJoin_ValidConfig_AddsCrossJoin()
    {
        var config = new MultiDbConfig();
        config
            .AddDatabase(db =>
            {
                db.Alias = "userDb";
                db.ConnectionString = "conn1";
            })
            .AddDatabase(db =>
            {
                db.Alias = "orderDb";
                db.ConnectionString = "conn2";
            })
            .AddCrossJoin(j =>
            {
                j.SourceAlias = "orderDb";
                j.TargetAlias = "userDb";
                j.SourceTable = "Orders";
                j.SourceColumn = "UserId";
                j.TargetTable = "Users";
                j.TargetColumn = "Id";
            });

        config.CrossJoins.Should().HaveCount(1);
        config.CrossJoins[0].SourceAlias.Should().Be("orderDb");
        config.CrossJoins[0].TargetAlias.Should().Be("userDb");
    }

    [Fact]
    public void AddCrossJoin_SameDatabase_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "db1";
            db.ConnectionString = "conn1";
        });

        var act = () => config.AddCrossJoin(j =>
        {
            j.SourceAlias = "db1";
            j.TargetAlias = "db1";
            j.SourceTable = "A";
            j.SourceColumn = "Col";
            j.TargetTable = "B";
            j.TargetColumn = "Col";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*two different databases*");
    }

    [Fact]
    public void AddCrossJoin_UnknownSource_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "db1";
            db.ConnectionString = "conn1";
        });

        var act = () => config.AddCrossJoin(j =>
        {
            j.SourceAlias = "unknown";
            j.TargetAlias = "db1";
            j.SourceTable = "A";
            j.SourceColumn = "Col";
            j.TargetTable = "B";
            j.TargetColumn = "Col";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*unknown*not configured*");
    }

    [Fact]
    public void AddCrossJoin_UnknownTarget_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config.AddDatabase(db =>
        {
            db.Alias = "db1";
            db.ConnectionString = "conn1";
        });

        var act = () => config.AddCrossJoin(j =>
        {
            j.SourceAlias = "db1";
            j.TargetAlias = "unknown";
            j.SourceTable = "A";
            j.SourceColumn = "Col";
            j.TargetTable = "B";
            j.TargetColumn = "Col";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*unknown*not configured*");
    }

    [Fact]
    public void AddCrossJoin_MissingSourceTable_ThrowsArgumentException()
    {
        var config = new MultiDbConfig();
        config
            .AddDatabase(db => { db.Alias = "db1"; db.ConnectionString = "c1"; })
            .AddDatabase(db => { db.Alias = "db2"; db.ConnectionString = "c2"; });

        var act = () => config.AddCrossJoin(j =>
        {
            j.SourceAlias = "db1";
            j.TargetAlias = "db2";
            j.SourceColumn = "Col";
            j.TargetTable = "B";
            j.TargetColumn = "Col";
        });

        act.Should().Throw<ArgumentException>()
            .WithMessage("*SourceTable*required*");
    }

    [Fact]
    public void GetCrossJoinsFrom_ReturnsMatchingJoins()
    {
        var config = new MultiDbConfig();
        config
            .AddDatabase(db => { db.Alias = "db1"; db.ConnectionString = "c1"; })
            .AddDatabase(db => { db.Alias = "db2"; db.ConnectionString = "c2"; })
            .AddDatabase(db => { db.Alias = "db3"; db.ConnectionString = "c3"; })
            .AddCrossJoin(j =>
            {
                j.SourceAlias = "db1";
                j.TargetAlias = "db2";
                j.SourceTable = "A"; j.SourceColumn = "Id";
                j.TargetTable = "B"; j.TargetColumn = "AId";
            })
            .AddCrossJoin(j =>
            {
                j.SourceAlias = "db1";
                j.TargetAlias = "db3";
                j.SourceTable = "A"; j.SourceColumn = "Id";
                j.TargetTable = "C"; j.TargetColumn = "AId";
            })
            .AddCrossJoin(j =>
            {
                j.SourceAlias = "db2";
                j.TargetAlias = "db3";
                j.SourceTable = "B"; j.SourceColumn = "Id";
                j.TargetTable = "C"; j.TargetColumn = "BId";
            });

        config.GetCrossJoinsFrom("db1").Should().HaveCount(2);
        config.GetCrossJoinsFrom("db2").Should().HaveCount(1);
        config.GetCrossJoinsFrom("db3").Should().HaveCount(0);
    }

    #endregion

    #region DatabaseFieldConfig - Defaults

    [Fact]
    public void DatabaseFieldConfig_Defaults_AreCorrect()
    {
        var config = new DatabaseFieldConfig();

        config.IsDefault.Should().BeFalse();
        config.AllowedRoles.Should().BeEmpty();
        config.Metadata.Should().BeEmpty();
        config.Alias.Should().BeNull();
        config.ConnectionString.Should().BeNull();
    }

    #endregion
}

public class CrossDatabaseJoinResolverTests
{
    #region Join - Inner

    [Fact]
    public void InnerJoin_MatchingKeys_MergesRows()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Name"] = "Alice" },
            new() { ["Id"] = 2, ["Name"] = "Bob" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1, ["Total"] = 100m },
            new() { ["UserId"] = 2, ["Total"] = 200m },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "order_", CrossJoinType.Inner);

        result.Should().HaveCount(2);
        result[0]["Name"].Should().Be("Alice");
        result[0]["order_Total"].Should().Be(100m);
        result[1]["Name"].Should().Be("Bob");
        result[1]["order_Total"].Should().Be(200m);
    }

    [Fact]
    public void InnerJoin_NoMatchingKeys_ReturnsEmpty()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Name"] = "Alice" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 99, ["Total"] = 100m },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "order_", CrossJoinType.Inner);

        result.Should().BeEmpty();
    }

    [Fact]
    public void InnerJoin_OneToMany_ProducesMultipleRows()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Name"] = "Alice" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1, ["OrderId"] = 10 },
            new() { ["UserId"] = 1, ["OrderId"] = 20 },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Inner);

        result.Should().HaveCount(2);
        result[0]["o_OrderId"].Should().Be(10);
        result[1]["o_OrderId"].Should().Be(20);
        result.All(r => (string)r["Name"]! == "Alice").Should().BeTrue();
    }

    [Fact]
    public void InnerJoin_EmptyLeft_ReturnsEmpty()
    {
        var left = new List<Dictionary<string, object?>>();
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1, ["Total"] = 100m },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Inner);

        result.Should().BeEmpty();
    }

    [Fact]
    public void InnerJoin_EmptyRight_ReturnsEmpty()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Name"] = "Alice" },
        };
        var right = new List<Dictionary<string, object?>>();

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Inner);

        result.Should().BeEmpty();
    }

    [Fact]
    public void InnerJoin_NullKeyValue_SkipsRow()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = null, ["Name"] = "Ghost" },
            new() { ["Id"] = 1, ["Name"] = "Alice" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1, ["Total"] = 100m },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Inner);

        result.Should().HaveCount(1);
        result[0]["Name"].Should().Be("Alice");
    }

    [Fact]
    public void InnerJoin_MissingKeyColumn_SkipsRow()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Name"] = "NoIdColumn" },
            new() { ["Id"] = 1, ["Name"] = "Alice" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1, ["Total"] = 100m },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Inner);

        result.Should().HaveCount(1);
        result[0]["Name"].Should().Be("Alice");
    }

    [Fact]
    public void InnerJoin_StringKeys_MatchesByStringValue()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Code"] = "US", ["Country"] = "United States" },
            new() { ["Code"] = "UK", ["Country"] = "United Kingdom" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["CountryCode"] = "US", ["City"] = "New York" },
            new() { ["CountryCode"] = "US", ["City"] = "Los Angeles" },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Code", right, "CountryCode", "city_", CrossJoinType.Inner);

        result.Should().HaveCount(2);
        result.All(r => (string)r["Country"]! == "United States").Should().BeTrue();
    }

    #endregion

    #region Join - Left

    [Fact]
    public void LeftJoin_UnmatchedLeft_IncludesWithNulls()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Name"] = "Alice" },
            new() { ["Id"] = 2, ["Name"] = "Bob" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1, ["Total"] = 100m },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Left);

        result.Should().HaveCount(2);
        result[0]["o_Total"].Should().Be(100m);
        result[1]["o_Total"].Should().BeNull();
        result[1]["o_UserId"].Should().BeNull();
    }

    [Fact]
    public void LeftJoin_EmptyRight_ReturnsLeftRows()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = 1, ["Name"] = "Alice" },
        };
        var right = new List<Dictionary<string, object?>>();

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Left);

        result.Should().HaveCount(1);
        result[0]["Name"].Should().Be("Alice");
    }

    [Fact]
    public void LeftJoin_NullKey_IncludesWithNulls()
    {
        var left = new List<Dictionary<string, object?>>
        {
            new() { ["Id"] = null, ["Name"] = "Ghost" },
        };
        var right = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1, ["Total"] = 100m },
        };

        var result = CrossDatabaseJoinResolver.Join(
            left, "Id", right, "UserId", "o_", CrossJoinType.Left);

        result.Should().HaveCount(1);
        result[0]["Name"].Should().Be("Ghost");
        result[0]["o_Total"].Should().BeNull();
    }

    #endregion

    #region CollectJoinKeys

    [Fact]
    public void CollectJoinKeys_ReturnsDistinctNonNullKeys()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = 1 },
            new() { ["UserId"] = 2 },
            new() { ["UserId"] = 1 },
            new() { ["UserId"] = null },
            new() { ["UserId"] = 3 },
        };

        var keys = CrossDatabaseJoinResolver.CollectJoinKeys(rows, "UserId");

        keys.Should().HaveCount(3);
        keys.Should().Contain(1);
        keys.Should().Contain(2);
        keys.Should().Contain(3);
    }

    [Fact]
    public void CollectJoinKeys_EmptyRows_ReturnsEmpty()
    {
        var rows = new List<Dictionary<string, object?>>();

        var keys = CrossDatabaseJoinResolver.CollectJoinKeys(rows, "UserId");

        keys.Should().BeEmpty();
    }

    [Fact]
    public void CollectJoinKeys_AllNulls_ReturnsEmpty()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["UserId"] = null },
            new() { ["UserId"] = null },
        };

        var keys = CrossDatabaseJoinResolver.CollectJoinKeys(rows, "UserId");

        keys.Should().BeEmpty();
    }

    [Fact]
    public void CollectJoinKeys_MissingColumn_ReturnsEmpty()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Name"] = "Alice" },
        };

        var keys = CrossDatabaseJoinResolver.CollectJoinKeys(rows, "UserId");

        keys.Should().BeEmpty();
    }

    #endregion
}

public class MultiDbSchemaGeneratorTests
{
    #region Schema Generation

    [Fact]
    public void GenerateSchema_SingleDatabase_ProducesValidStructure()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var databases = new Dictionary<string, IDbModel>
        {
            ["userDb"] = model,
        };

        var schema = MultiDbSchemaGenerator.GenerateSchema(databases);

        schema.Should().Contain("type multiDatabase");
        schema.Should().Contain("userDb: userDbQuery");
        schema.Should().Contain("type userDbQuery");
        schema.Should().Contain("type multiDatabaseInput");
        schema.Should().Contain("userDb: userDbMutation");
        schema.Should().Contain("type userDbMutation");
    }

    [Fact]
    public void GenerateSchema_MultipleDatabases_IncludesAllFields()
    {
        var userModel = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var orderModel = DbModelTestFixture.Create()
            .WithTable("Orders", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Total", "decimal"))
            .Build();

        var databases = new Dictionary<string, IDbModel>
        {
            ["userDb"] = userModel,
            ["orderDb"] = orderModel,
        };

        var schema = MultiDbSchemaGenerator.GenerateSchema(databases);

        schema.Should().Contain("userDb: userDbQuery");
        schema.Should().Contain("orderDb: orderDbQuery");
        schema.Should().Contain("type userDbQuery");
        schema.Should().Contain("type orderDbQuery");
    }

    [Fact]
    public void GenerateSchema_EmptyDatabases_ThrowsArgumentException()
    {
        var databases = new Dictionary<string, IDbModel>();

        var act = () => MultiDbSchemaGenerator.GenerateSchema(databases);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*At least one database*");
    }

    #endregion

    #region Type Name Generation

    [Fact]
    public void GetDbQueryTypeName_ReturnsExpectedFormat()
    {
        MultiDbSchemaGenerator.GetDbQueryTypeName("userDb").Should().Be("userDbQuery");
    }

    [Fact]
    public void GetDbMutationTypeName_ReturnsExpectedFormat()
    {
        MultiDbSchemaGenerator.GetDbMutationTypeName("orderDb").Should().Be("orderDbMutation");
    }

    #endregion

    #region Schema Info

    [Fact]
    public void GetSchemaInfo_ReturnsTableCountsPerDatabase()
    {
        var userModel = DbModelTestFixture.Create()
            .WithTable("Users", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t.WithPrimaryKey("Id").WithColumn("Name", "nvarchar"))
            .Build();

        var orderModel = DbModelTestFixture.Create()
            .WithTable("Orders", t => t.WithPrimaryKey("Id").WithColumn("Total", "decimal"))
            .Build();

        var databases = new Dictionary<string, IDbModel>
        {
            ["userDb"] = userModel,
            ["orderDb"] = orderModel,
        };

        var info = MultiDbSchemaGenerator.GetSchemaInfo(databases);

        info["userDb"].Should().Be(2);
        info["orderDb"].Should().Be(1);
    }

    #endregion
}
