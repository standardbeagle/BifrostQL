using System.Reflection;
using BifrostQL.Core.Model;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Schema;
using FluentAssertions;

namespace BifrostQL.Core.Test.Model;

/// <summary>
/// Tests for ManyToManyLink detection, metadata configuration, schema generation,
/// and SQL query generation through junction tables.
/// </summary>
public sealed class ManyToManyLinkTests
{
    private static readonly ISqlDialect Dialect = SqlServerDialect.Instance;

    private static readonly MethodInfo SchemaTextFromModelMethod = typeof(DbSchema).Assembly
        .GetType("BifrostQL.Core.Schema.SchemaGenerator")!
        .GetMethod("SchemaTextFromModel", BindingFlags.Static | BindingFlags.Public)!;

    private static string GetSchemaText(IDbModel model, bool includeDynamicJoins = false)
        => (string)SchemaTextFromModelMethod.Invoke(null, new object[] { model, includeDynamicJoins })!;

    #region Auto-Detection via Foreign Keys

    [Fact]
    public void AutoDetect_JunctionWithTwoFKs_CreatesManyToManyLinks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithForeignKey("FK_UserRoles_Users", "UserRoles", "UserId", "Users", "Id")
            .WithForeignKey("FK_UserRoles_Roles", "UserRoles", "RoleId", "Roles", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        var roles = model.GetTableFromDbName("Roles");

        users.ManyToManyLinks.Should().ContainKey("Roles");
        var usersToRoles = users.ManyToManyLinks["Roles"];
        usersToRoles.SourceTable.DbName.Should().Be("Users");
        usersToRoles.JunctionTable.DbName.Should().Be("UserRoles");
        usersToRoles.TargetTable.DbName.Should().Be("Roles");
        usersToRoles.SourceColumn.ColumnName.Should().Be("Id");
        usersToRoles.JunctionSourceColumn.ColumnName.Should().Be("UserId");
        usersToRoles.JunctionTargetColumn.ColumnName.Should().Be("RoleId");
        usersToRoles.TargetColumn.ColumnName.Should().Be("Id");

        roles.ManyToManyLinks.Should().ContainKey("Users");
        var rolesToUsers = roles.ManyToManyLinks["Users"];
        rolesToUsers.SourceTable.DbName.Should().Be("Roles");
        rolesToUsers.JunctionTable.DbName.Should().Be("UserRoles");
        rolesToUsers.TargetTable.DbName.Should().Be("Users");
        rolesToUsers.SourceColumn.ColumnName.Should().Be("Id");
        rolesToUsers.JunctionSourceColumn.ColumnName.Should().Be("RoleId");
        rolesToUsers.JunctionTargetColumn.ColumnName.Should().Be("UserId");
        rolesToUsers.TargetColumn.ColumnName.Should().Be("Id");
    }

    [Fact]
    public void AutoDetect_JunctionWithExtraDataColumns_IsNotDetected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int")
                .WithColumn("AssignedDate", "datetime2"))
            .WithForeignKey("FK_UserRoles_Users", "UserRoles", "UserId", "Users", "Id")
            .WithForeignKey("FK_UserRoles_Roles", "UserRoles", "RoleId", "Roles", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    [Fact]
    public void AutoDetect_TableWithOnlyOneFK_IsNotDetected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_Users", "Orders", "UserId", "Users", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    [Fact]
    public void AutoDetect_TableWithThreeFKs_IsNotDetected()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Orgs", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("UserRoleOrgs", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int")
                .WithColumn("OrgId", "int"))
            .WithForeignKey("FK_URO_Users", "UserRoleOrgs", "UserId", "Users", "Id")
            .WithForeignKey("FK_URO_Roles", "UserRoleOrgs", "RoleId", "Roles", "Id")
            .WithForeignKey("FK_URO_Orgs", "UserRoleOrgs", "OrgId", "Orgs", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    [Fact]
    public void AutoDetect_CompositeFKs_AreSkipped()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int")
                .WithColumn("TenantId", "int"))
            .WithForeignKey("FK_UserRoles_Users",
                "dbo", "UserRoles", new[] { "UserId", "TenantId" },
                "dbo", "Users", new[] { "Id", "Name" })
            .WithForeignKey("FK_UserRoles_Roles", "UserRoles", "RoleId", "Roles", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    [Fact]
    public void AutoDetect_SelfReferencing_CreatesBidirectionalLink()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Tags", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("TagRelations", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("ParentTagId", "int")
                .WithColumn("ChildTagId", "int"))
            .WithForeignKey("FK_TR_Parent", "TagRelations", "ParentTagId", "Tags", "Id")
            .WithForeignKey("FK_TR_Child", "TagRelations", "ChildTagId", "Tags", "Id")
            .Build();

        var tags = model.GetTableFromDbName("Tags");
        // Self-referencing M:N: Tags -> TagRelations -> Tags
        // Only one entry since both source and target are the same table
        tags.ManyToManyLinks.Should().ContainKey("Tags");
    }

    [Fact]
    public void AutoDetect_JunctionPKColumnsAreAlsoFKs_IsDetected()
    {
        // Junction table where PK columns are also the FK columns (composite PK)
        var model = DbModelTestFixture.Create()
            .WithTable("Students", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Courses", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Title", "nvarchar"))
            .WithTable("Enrollments", t => t
                .WithSchema("dbo")
                .WithColumn("StudentId", "int", isPrimaryKey: true)
                .WithColumn("CourseId", "int", isPrimaryKey: true))
            .WithForeignKey("FK_Enroll_Students", "Enrollments", "StudentId", "Students", "Id")
            .WithForeignKey("FK_Enroll_Courses", "Enrollments", "CourseId", "Courses", "Id")
            .Build();

        var students = model.GetTableFromDbName("Students");
        var courses = model.GetTableFromDbName("Courses");

        students.ManyToManyLinks.Should().ContainKey("Courses");
        courses.ManyToManyLinks.Should().ContainKey("Students");
    }

    #endregion

    #region Metadata Configuration

    [Fact]
    public void Metadata_ManyToManyConfig_CreatesLink()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("many-to-many", "Roles:UserRoles"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithForeignKey("FK_UserRoles_Users", "UserRoles", "UserId", "Users", "Id")
            .WithForeignKey("FK_UserRoles_Roles", "UserRoles", "RoleId", "Roles", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().ContainKey("Roles");
    }

    [Fact]
    public void Metadata_MultipleM2MEntries_CreatesMultipleLinks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("many-to-many", "Roles:UserRoles, Groups:UserGroups"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Groups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithTable("UserGroups", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("GroupId", "int"))
            .WithForeignKey("FK_UR_Users", "UserRoles", "UserId", "Users", "Id")
            .WithForeignKey("FK_UR_Roles", "UserRoles", "RoleId", "Roles", "Id")
            .WithForeignKey("FK_UG_Users", "UserGroups", "UserId", "Users", "Id")
            .WithForeignKey("FK_UG_Groups", "UserGroups", "GroupId", "Groups", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().ContainKey("Roles");
        users.ManyToManyLinks.Should().ContainKey("Groups");
    }

    [Fact]
    public void Metadata_InvalidTargetTable_IsSkipped()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("many-to-many", "NonExistent:UserRoles"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithForeignKey("FK_UR_Users", "UserRoles", "UserId", "Users", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    [Fact]
    public void Metadata_InvalidJunctionTable_IsSkipped()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("many-to-many", "Roles:NonExistent"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    [Fact]
    public void Metadata_MalformedEntry_IsSkipped()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar")
                .WithMetadata("many-to-many", "JustOneValue"))
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    #endregion

    #region ManyToManyLink Model

    [Fact]
    public void ManyToManyLink_ToString_FormatsCorrectly()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithForeignKey("FK_UR_Users", "UserRoles", "UserId", "Users", "Id")
            .WithForeignKey("FK_UR_Roles", "UserRoles", "RoleId", "Roles", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        var link = users.ManyToManyLinks["Roles"];
        link.ToString().Should().Contain("Users.Id");
        link.ToString().Should().Contain("UserRoles");
        link.ToString().Should().Contain("Roles.Id");
    }

    [Fact]
    public void ManyToManyLinks_EmptyByDefault()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .Build();

        var users = model.GetTableFromDbName("Users");
        users.ManyToManyLinks.Should().BeEmpty();
    }

    #endregion

    #region ConnectLinks - M:N Join Resolution

    [Fact]
    public void ConnectLinks_ManyToMany_CreatesChainedJoins()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithSingleLink("UserRoles", "UserId", "Users", "Id", "Users")
            .WithSingleLink("UserRoles", "RoleId", "Roles", "Id", "Roles")
            .WithMultiLink("Users", "Id", "UserRoles", "UserId", "UserRoles")
            .WithMultiLink("Roles", "Id", "UserRoles", "RoleId", "UserRoles")
            .WithManyToManyLink("Users", "Id", "UserRoles", "UserId", "RoleId", "Roles", "Id")
            .Build();

        var usersTable = model.GetTableFromDbName("Users");

        var rolesLink = new GqlObjectQuery
        {
            GraphQlName = "Roles",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Links = { rolesLink }
        };

        query.ConnectLinks(model);

        // First hop: Users -> UserRoles (junction)
        query.Joins.Should().ContainSingle();
        var junctionJoin = query.Joins[0];
        junctionJoin.ConnectedTable.TableName.Should().Be("UserRoles");
        junctionJoin.FromColumn.Should().Be("Id");
        junctionJoin.ConnectedColumn.Should().Be("UserId");

        // Second hop: UserRoles -> Roles (target)
        junctionJoin.ConnectedTable.Joins.Should().ContainSingle();
        var targetJoin = junctionJoin.ConnectedTable.Joins[0];
        targetJoin.ConnectedTable.TableName.Should().Be("Roles");
        targetJoin.FromColumn.Should().Be("RoleId");
        targetJoin.ConnectedColumn.Should().Be("Id");
    }

    [Fact]
    public void ConnectLinks_ManyToMany_TargetQueryHasUserColumns()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithSingleLink("UserRoles", "UserId", "Users", "Id", "Users")
            .WithSingleLink("UserRoles", "RoleId", "Roles", "Id", "Roles")
            .WithMultiLink("Users", "Id", "UserRoles", "UserId", "UserRoles")
            .WithManyToManyLink("Users", "Id", "UserRoles", "UserId", "RoleId", "Roles", "Id")
            .Build();

        var usersTable = model.GetTableFromDbName("Users");

        var rolesLink = new GqlObjectQuery
        {
            GraphQlName = "Roles",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            ScalarColumns = { new GqlObjectColumn("Id") },
            Links = { rolesLink }
        };

        query.ConnectLinks(model);

        // The target query (Roles) should have the user-requested columns
        var targetQuery = query.Joins[0].ConnectedTable.Joins[0].ConnectedTable;
        targetQuery.ScalarColumns.Should().Contain(c => c.DbDbName == "Id");
        targetQuery.ScalarColumns.Should().Contain(c => c.DbDbName == "Label");
    }

    [Fact]
    public void ConnectLinks_ManyToMany_WithAlias_PreservesAlias()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithSingleLink("UserRoles", "UserId", "Users", "Id", "Users")
            .WithSingleLink("UserRoles", "RoleId", "Roles", "Id", "Roles")
            .WithMultiLink("Users", "Id", "UserRoles", "UserId", "UserRoles")
            .WithManyToManyLink("Users", "Id", "UserRoles", "UserId", "RoleId", "Roles", "Id")
            .Build();

        var usersTable = model.GetTableFromDbName("Users");

        var rolesLink = new GqlObjectQuery
        {
            GraphQlName = "Roles",
            Alias = "assignedRoles",
            ScalarColumns = { new GqlObjectColumn("Id") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            Links = { rolesLink }
        };

        query.ConnectLinks(model);

        // The alias should be on the target join (second hop), not the junction
        var targetJoin = query.Joins[0].ConnectedTable.Joins[0];
        targetJoin.Alias.Should().Be("assignedRoles");
    }

    #endregion

    #region SQL Generation

    [Fact]
    public void AddSqlParameterized_ManyToMany_GeneratesJunctionSQL()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithSingleLink("UserRoles", "UserId", "Users", "Id", "Users")
            .WithSingleLink("UserRoles", "RoleId", "Roles", "Id", "Roles")
            .WithMultiLink("Users", "Id", "UserRoles", "UserId", "UserRoles")
            .WithManyToManyLink("Users", "Id", "UserRoles", "UserId", "RoleId", "Roles", "Id")
            .Build();

        var usersTable = model.GetTableFromDbName("Users");

        var rolesLink = new GqlObjectQuery
        {
            GraphQlName = "Roles",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = usersTable,
            TableName = "Users",
            GraphQlName = "Users",
            Path = "Users",
            ScalarColumns = { new GqlObjectColumn("Id"), new GqlObjectColumn("Name") },
            Links = { rolesLink }
        };

        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();

        query.AddSqlParameterized(model, Dialect, sqls, parameters);

        // Main query for Users
        sqls.Should().ContainKey("Users");
        sqls["Users"].Sql.Should().Contain("[Users]");

        // Junction join: Users -> UserRoles
        var junctionKey = sqls.Keys.FirstOrDefault(k => k.Contains("UserRoles"));
        junctionKey.Should().NotBeNull();
        sqls[junctionKey!].Sql.Should().Contain("[UserRoles]");
        sqls[junctionKey!].Sql.Should().Contain("INNER JOIN");

        // Target join: UserRoles -> Roles
        var rolesKey = sqls.Keys.FirstOrDefault(k => k.Contains("Roles") && k != junctionKey);
        rolesKey.Should().NotBeNull();
        sqls[rolesKey!].Sql.Should().Contain("[Roles]");
        sqls[rolesKey!].Sql.Should().Contain("INNER JOIN");
    }

    #endregion

    #region Schema Generation

    [Fact]
    public void SchemaGeneration_ManyToMany_AddsFieldToSourceType()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("UserRoles", t => t
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithSingleLink("UserRoles", "UserId", "Users", "Id", "Users")
            .WithSingleLink("UserRoles", "RoleId", "Roles", "Id", "Roles")
            .WithMultiLink("Users", "Id", "UserRoles", "UserId", "UserRoles")
            .WithManyToManyLink("Users", "Id", "UserRoles", "UserId", "RoleId", "Roles", "Id")
            .Build();

        var schema = GetSchemaText(model);

        // The Users type should have a Roles field returning [Roles]
        schema.Should().Contain("Roles(filter: TableFilterRolesInput) : [Roles]");
    }

    [Fact]
    public void SchemaGeneration_NoManyToMany_NoExtraFields()
    {
        var model = StandardTestFixtures.UsersWithOrders();

        var schema = GetSchemaText(model);

        // Users type should NOT have a many-to-many field for Orders
        // (it has a multi-link instead)
        var usersTypeSection = ExtractTypeBlock(schema, "Users");
        usersTypeSection.Should().NotContain("ManyToMany");
    }

    #endregion

    #region Existing Tests Regression

    [Fact]
    public void ExistingLinks_NotAffectedByManyToMany()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Roles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithTable("UserRoles", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("RoleId", "int"))
            .WithForeignKey("FK_Orders_Users", "Orders", "UserId", "Users", "Id")
            .WithForeignKey("FK_UR_Users", "UserRoles", "UserId", "Users", "Id")
            .WithForeignKey("FK_UR_Roles", "UserRoles", "RoleId", "Roles", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");

        // Existing single/multi links are preserved
        users.MultiLinks.Should().ContainKey("Orders");
        users.MultiLinks.Should().ContainKey("UserRoles");

        // M:N link is also created
        users.ManyToManyLinks.Should().ContainKey("Roles");
    }

    [Fact]
    public void NoForeignKeys_NoAutoDetection_NoManyToManyLinks()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("Users", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("Orders", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("Id")
                .WithColumn("UserId", "int")
                .WithColumn("Total", "decimal"))
            .WithForeignKey("FK_Orders_Users", "Orders", "UserId", "Users", "Id")
            .Build();

        var users = model.GetTableFromDbName("Users");
        var orders = model.GetTableFromDbName("Orders");

        users.ManyToManyLinks.Should().BeEmpty();
        orders.ManyToManyLinks.Should().BeEmpty();
    }

    #endregion

    #region Test Fixture Builder

    [Fact]
    public void TestFixture_WithManyToManyLink_CreatesLink()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("A", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("B", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("AB", t => t
                .WithPrimaryKey("Id")
                .WithColumn("AId", "int")
                .WithColumn("BId", "int"))
            .WithManyToManyLink("A", "Id", "AB", "AId", "BId", "B", "Id")
            .Build();

        var tableA = model.GetTableFromDbName("A");
        tableA.ManyToManyLinks.Should().ContainKey("B");
        tableA.ManyToManyLinks["B"].JunctionTable.DbName.Should().Be("AB");
    }

    [Fact]
    public void TestFixture_WithManyToManyLink_CustomName()
    {
        var model = DbModelTestFixture.Create()
            .WithTable("A", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Name", "nvarchar"))
            .WithTable("B", t => t
                .WithPrimaryKey("Id")
                .WithColumn("Label", "nvarchar"))
            .WithTable("AB", t => t
                .WithPrimaryKey("Id")
                .WithColumn("AId", "int")
                .WithColumn("BId", "int"))
            .WithManyToManyLink("A", "Id", "AB", "AId", "BId", "B", "Id", "customLink")
            .Build();

        var tableA = model.GetTableFromDbName("A");
        tableA.ManyToManyLinks.Should().ContainKey("customLink");
    }

    #endregion

    /// <summary>
    /// Extracts the type block from a GraphQL schema string.
    /// </summary>
    private static string ExtractTypeBlock(string schema, string typeName)
    {
        var startIndex = schema.IndexOf($"type {typeName} {{", StringComparison.Ordinal);
        if (startIndex < 0) return "";
        var endIndex = schema.IndexOf("}", startIndex, StringComparison.Ordinal);
        if (endIndex < 0) return "";
        return schema.Substring(startIndex, endIndex - startIndex + 1);
    }
}
