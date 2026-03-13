using FluentAssertions;
using BifrostQL.Core.QueryModel;
using BifrostQL.Core.QueryModel.TestFixtures;
using BifrostQL.Core.Model;
using BifrostQL.Core.Resolvers;
using Xunit;

namespace BifrostQL.Core.QueryModel;

/// <summary>
/// Tests that underscore-separated FK columns (workspace_id, member_id, etc.)
/// correctly produce joins and valid SQL through the full pipeline:
/// Model → ConnectLinks → AddSqlParameterized
/// </summary>
public sealed class UnderscoreFkJoinTest
{
    /// <summary>
    /// Minimal project-tracker model built via name-based heuristic (BuildWithForeignKeys path).
    /// </summary>
    private static IDbModel ProjectTrackerModel() => DbModelTestFixture.Create()
        .WithTable("workspaces", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("workspace_id", "int")
            .WithColumn("name", "nvarchar"))
        .WithTable("members", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("member_id", "int")
            .WithColumn("workspace_id", "int")
            .WithColumn("name", "nvarchar"))
        .WithTable("projects", t => t
            .WithSchema("dbo")
            .WithPrimaryKey("project_id", "int")
            .WithColumn("workspace_id", "int")
            .WithColumn("name", "nvarchar"))
        .WithForeignKey("FK_members_workspaces", "members", "workspace_id", "workspaces", "workspace_id")
        .WithForeignKey("FK_projects_workspaces", "projects", "workspace_id", "workspaces", "workspace_id")
        .Build();

    #region Links exist after name-based heuristic

    [Fact]
    public void Model_UnderscoreFk_CreatesSingleLinks()
    {
        var model = ProjectTrackerModel();
        var members = model.GetTableFromDbName("members");
        var projects = model.GetTableFromDbName("projects");

        members.SingleLinks.Should().ContainKey("workspaces");
        projects.SingleLinks.Should().ContainKey("workspaces");
    }

    [Fact]
    public void Model_UnderscoreFk_CreatesMultiLinks()
    {
        var model = ProjectTrackerModel();
        var workspaces = model.GetTableFromDbName("workspaces");

        workspaces.MultiLinks.Should().ContainKey("members");
        workspaces.MultiLinks.Should().ContainKey("projects");
    }

    #endregion

    #region ConnectLinks finds underscore-FK single links

    [Fact]
    public void ConnectLinks_UnderscoreFk_SingleLink_CreatesJoin()
    {
        var model = ProjectTrackerModel();
        var members = model.GetTableFromDbName("members");

        var workspacesLink = new GqlObjectQuery
        {
            GraphQlName = "workspaces",
            ScalarColumns = { new GqlObjectColumn("workspace_id", "id"), new GqlObjectColumn("name", "label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = members,
            TableName = "members",
            GraphQlName = "members",
            Path = "members",
            ScalarColumns = { new GqlObjectColumn("member_id"), new GqlObjectColumn("workspace_id"), new GqlObjectColumn("name") },
            Links = { workspacesLink }
        };

        query.ConnectLinks(model);

        query.Joins.Should().ContainSingle();
        var join = query.Joins[0];
        join.Name.Should().Be("workspaces");
        join.QueryType.Should().Be(QueryType.Single);
        join.FromColumn.Should().Be("workspace_id");
        join.ConnectedColumn.Should().Be("workspace_id");
        join.ConnectedTable.TableName.Should().Be("workspaces");
    }

    #endregion

    #region SQL generation produces valid batch SQL

    [Fact]
    public void AddSqlParameterized_UnderscoreFk_GeneratesMainAndJoinSql()
    {
        var model = ProjectTrackerModel();
        var members = model.GetTableFromDbName("members");
        var dialect = SqlServerDialect.Instance;

        var workspacesLink = new GqlObjectQuery
        {
            GraphQlName = "workspaces",
            ScalarColumns = { new GqlObjectColumn("workspace_id", "id"), new GqlObjectColumn("name", "label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = members,
            TableName = "members",
            SchemaName = "dbo",
            GraphQlName = "members",
            Path = "members",
            IncludeResult = true,
            ScalarColumns = { new GqlObjectColumn("member_id"), new GqlObjectColumn("workspace_id"), new GqlObjectColumn("name") },
            Links = { workspacesLink }
        };

        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        // Should have: main query, count query, and join query
        sqls.Should().ContainKey("members");
        sqls.Should().ContainKey("members=>count");
        sqls.Should().ContainKey("members->workspaces");

        // Main query should select workspace_id (needed for join)
        sqls["members"].Sql.Should().Contain("[workspace_id]");

        // Join query should have src_id, and join to workspaces
        var joinSql = sqls["members->workspaces"].Sql;
        joinSql.Should().Contain("[src_id]");
        joinSql.Should().Contain("INNER JOIN");
        joinSql.Should().Contain("[workspaces]");
        joinSql.Should().Contain("[id]");
        joinSql.Should().Contain("[label]");
    }

    [Fact]
    public void AddSqlParameterized_UnderscoreFk_JoinSqlSelectsAliasedColumns()
    {
        var model = ProjectTrackerModel();
        var projects = model.GetTableFromDbName("projects");
        var dialect = SqlServerDialect.Instance;

        var workspacesLink = new GqlObjectQuery
        {
            GraphQlName = "workspaces",
            ScalarColumns = { new GqlObjectColumn("workspace_id", "id"), new GqlObjectColumn("name", "label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = projects,
            TableName = "projects",
            SchemaName = "dbo",
            GraphQlName = "projects",
            Path = "projects",
            IncludeResult = true,
            ScalarColumns = { new GqlObjectColumn("project_id"), new GqlObjectColumn("workspace_id"), new GqlObjectColumn("name") },
            Links = { workspacesLink }
        };

        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        var joinSql = sqls["projects->workspaces"].Sql;

        // The join SQL should alias workspace_id as id and name as label
        joinSql.Should().Contain("[b].[workspace_id] AS [id]");
        joinSql.Should().Contain("[b].[name] AS [label]");
    }

    #endregion

    #region Full ColumnNames includes FK column for join

    [Fact]
    public void FullColumnNames_IncludesFkColumnForJoin()
    {
        var model = ProjectTrackerModel();
        var members = model.GetTableFromDbName("members");

        var workspacesLink = new GqlObjectQuery
        {
            GraphQlName = "workspaces",
            ScalarColumns = { new GqlObjectColumn("workspace_id", "id"), new GqlObjectColumn("name", "label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = members,
            TableName = "members",
            GraphQlName = "members",
            Path = "members",
            ScalarColumns = { new GqlObjectColumn("member_id"), new GqlObjectColumn("name") },
            Links = { workspacesLink }
        };

        query.ConnectLinks(model);

        // FullColumnNames should include workspace_id even though it's not in ScalarColumns,
        // because it's needed as the join FromColumn
        var colNames = query.FullColumnNames.Select(c => c.DbDbName).ToList();
        colNames.Should().Contain("workspace_id");
    }

    #endregion

    #region Multi-link (OneToMany) also works for underscore FK

    [Fact]
    public void ConnectLinks_UnderscoreFk_MultiLink_CreatesJoin()
    {
        var model = ProjectTrackerModel();
        var workspaces = model.GetTableFromDbName("workspaces");

        var membersLink = new GqlObjectQuery
        {
            GraphQlName = "members",
            ScalarColumns = { new GqlObjectColumn("member_id"), new GqlObjectColumn("name") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = workspaces,
            TableName = "workspaces",
            GraphQlName = "workspaces",
            Path = "workspaces",
            ScalarColumns = { new GqlObjectColumn("workspace_id"), new GqlObjectColumn("name") },
            Links = { membersLink }
        };

        query.ConnectLinks(model);

        query.Joins.Should().ContainSingle();
        var join = query.Joins[0];
        join.Name.Should().Be("members");
        join.QueryType.Should().Be(QueryType.Join);
        join.FromColumn.Should().Be("workspace_id");
        join.ConnectedColumn.Should().Be("workspace_id");
    }

    #endregion

    #region Classroom assignments → courses (exact reproduction of runtime error)

    [Fact]
    public void FullColumnNames_ClassroomAssignments_DuplicateCourseId_BothAliasesSurvive()
    {
        // Classroom schema: assignments has course_id FK → courses.
        // courses.Columns.First() = course_id (PK), so labelColumn = course_id.
        // Frontend generates: course_id courses { id: course_id label: course_id }
        // Both aliases point to same DB column. DistinctBy must keep both.
        var model = DbModelTestFixture.Create()
            .WithTable("courses", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("course_id", "int")
                .WithColumn("name", "nvarchar")
                .WithColumn("credits", "int"))
            .WithTable("assignments", t => t
                .WithSchema("dbo")
                .WithPrimaryKey("assignment_id", "int")
                .WithColumn("course_id", "int")
                .WithColumn("title", "nvarchar"))
            .WithForeignKey("FK_assignments_courses", "assignments", "course_id", "courses", "course_id")
            .Build();

        var coursesLink = new GqlObjectQuery
        {
            GraphQlName = "courses",
            ScalarColumns = { new GqlObjectColumn("course_id", "id"), new GqlObjectColumn("course_id", "label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = model.GetTableFromDbName("assignments"),
            TableName = "assignments",
            SchemaName = "dbo",
            GraphQlName = "assignments",
            Path = "assignments",
            IncludeResult = true,
            ScalarColumns = { new GqlObjectColumn("assignment_id"), new GqlObjectColumn("course_id"), new GqlObjectColumn("title") },
            Links = { coursesLink }
        };

        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, SqlServerDialect.Instance, sqls, parameters);

        sqls.Should().ContainKey("assignments->courses");
        var joinSql = sqls["assignments->courses"].Sql;
        joinSql.Should().Contain("[id]", "id alias for course_id PK");
        joinSql.Should().Contain("[label]", "label alias for course_id PK");
    }

    #endregion

    #region Duplicate alias for same DB column (frontend label = PK pattern)

    [Fact]
    public void FullColumnNames_DuplicateDbColumn_DifferentAliases_BothSurvive()
    {
        // When labelColumn == PK, frontend generates: workspaces { id: workspace_id label: workspace_id }
        // Both aliases point to the same DB column. DistinctBy must NOT drop the second one.
        var model = ProjectTrackerModel();
        var members = model.GetTableFromDbName("members");

        var workspacesLink = new GqlObjectQuery
        {
            GraphQlName = "workspaces",
            // Both aliases point to workspace_id (same DB column)
            ScalarColumns = { new GqlObjectColumn("workspace_id", "id"), new GqlObjectColumn("workspace_id", "label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = members,
            TableName = "members",
            GraphQlName = "members",
            Path = "members",
            ScalarColumns = { new GqlObjectColumn("member_id"), new GqlObjectColumn("workspace_id"), new GqlObjectColumn("name") },
            Links = { workspacesLink }
        };

        query.ConnectLinks(model);

        // The connected table's FullColumnNames must include both aliases
        var joinQuery = query.Joins[0].ConnectedTable;
        var aliases = joinQuery.FullColumnNames.Select(c => c.GraphQlDbName).ToList();
        aliases.Should().Contain("id", "first alias for workspace_id");
        aliases.Should().Contain("label", "second alias for workspace_id");
    }

    [Fact]
    public void AddSqlParameterized_DuplicateDbColumn_DifferentAliases_BothInJoinSql()
    {
        var model = ProjectTrackerModel();
        var members = model.GetTableFromDbName("members");
        var dialect = SqlServerDialect.Instance;

        var workspacesLink = new GqlObjectQuery
        {
            GraphQlName = "workspaces",
            ScalarColumns = { new GqlObjectColumn("workspace_id", "id"), new GqlObjectColumn("workspace_id", "label") }
        };

        var query = new GqlObjectQuery
        {
            DbTable = members,
            TableName = "members",
            SchemaName = "dbo",
            GraphQlName = "members",
            Path = "members",
            IncludeResult = true,
            ScalarColumns = { new GqlObjectColumn("member_id"), new GqlObjectColumn("workspace_id"), new GqlObjectColumn("name") },
            Links = { workspacesLink }
        };

        query.ConnectLinks(model);

        var sqls = new Dictionary<string, ParameterizedSql>();
        var parameters = new SqlParameterCollection();
        query.AddSqlParameterized(model, dialect, sqls, parameters);

        var joinSql = sqls["members->workspaces"].Sql;
        // Both aliases must appear in the join SQL
        joinSql.Should().Contain("[id]", "first alias for workspace_id");
        joinSql.Should().Contain("[label]", "second alias for workspace_id");
    }

    #endregion
}
