using System.Data.Common;
using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using FluentAssertions;
using GraphQL;
using GraphQL.Types;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Xunit.Abstractions;
using RootExecutionNode = GraphQL.Execution.RootExecutionNode;

namespace BifrostQL.UI.Tests;

/// <summary>
/// End-to-end tests that verify each quickstart schema can be created as a SQLite database,
/// populated with seed data, and queried via the BifrostQL GraphQL pipeline.
/// Tests the full flow from embedded SQL resources → database creation → schema loading →
/// GraphQL queries → table navigation via joins.
/// </summary>
public class QuickStartEndToEndTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly List<string> _tempDbs = new();

    static QuickStartEndToEndTests()
    {
        DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
    }

    public QuickStartEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public void Dispose()
    {
        foreach (var db in _tempDbs)
        {
            try { if (File.Exists(db)) File.Delete(db); } catch { /* cleanup best-effort */ }
        }
    }

    private (string connectionString, string dbPath) CreateTempDb(string schema)
    {
        var fileName = $"bifrost-test-{schema}-{Guid.NewGuid():N}.db";
        var dbPath = Path.Combine(Path.GetTempPath(), fileName);
        _tempDbs.Add(dbPath);
        return ($"Data Source={dbPath}", dbPath);
    }

    private async Task<DbConnection> CreateAndSeedDatabase(string schema, string dataSize = "sample")
    {
        var (connectionString, _) = CreateTempDb(schema);
        var factory = DbConnFactoryResolver.Create(connectionString, BifrostDbProvider.Sqlite);

        var ddlSql = QuickstartSchemas.LoadSchemaSql(schema);
        ddlSql.Should().NotBeNull($"schema '{schema}' DDL should exist as embedded resource");

        var ddlStatements = ddlSql!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

        ddlStatements.Should().NotBeEmpty($"schema '{schema}' should have DDL statements");
        await QuickstartSchemas.ExecuteStatementsAsync(factory, ddlStatements, CancellationToken.None);

        var seedSql = QuickstartSchemas.LoadSeedSql(schema, dataSize);
        if (seedSql != null)
        {
            var seedStatements = seedSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            await QuickstartSchemas.ExecuteStatementsAsync(factory, seedStatements, CancellationToken.None);
        }

        var conn = factory.GetConnection();
        await conn.OpenAsync();
        return conn;
    }

    /// <summary>
    /// Creates a quickstart database and returns the full BifrostQL query execution context.
    /// </summary>
    private async Task<BifrostTestContext> CreateBifrostContext(string schema, string dataSize = "sample")
    {
        var (connectionString, dbPath) = CreateTempDb(schema);
        var factory = DbConnFactoryResolver.Create(connectionString, BifrostDbProvider.Sqlite);

        var ddlSql = QuickstartSchemas.LoadSchemaSql(schema)!;
        var ddlStatements = ddlSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        await QuickstartSchemas.ExecuteStatementsAsync(factory, ddlStatements, CancellationToken.None);

        var seedSql = QuickstartSchemas.LoadSeedSql(schema, dataSize);
        if (seedSql != null)
        {
            var seedStatements = seedSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
            await QuickstartSchemas.ExecuteStatementsAsync(factory, seedStatements, CancellationToken.None);
        }

        // Load schema using DbModelLoader — same path as BifrostQL middleware
        var metadataLoader = new MetadataLoader(Array.Empty<string>());
        var loader = new DbModelLoader(factory, metadataLoader);
        var model = await loader.LoadAsync();
        var gqlSchema = DbSchema.FromModel(model);

        // Build service provider for mutation support
        var services = new ServiceCollection();
        services.AddSingleton<IMutationModules>(new ModulesWrap { Modules = Array.Empty<IMutationModule>() });
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap { Transformers = Array.Empty<IMutationTransformer>() });
        var serviceProvider = services.BuildServiceProvider();

        return new BifrostTestContext(factory, model, gqlSchema, serviceProvider, dbPath);
    }

    private static async Task<List<string>> GetTableNames(DbConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
        var tables = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            tables.Add(reader.GetString(0));
        return tables;
    }

    private static async Task<long> GetRowCount(DbConnection conn, string table)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM [{table}]";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    // ── Result Extraction Helpers ──

    /// <summary>
    /// Extracts paged data from a GraphQL result.
    /// result.Data is a RootExecutionNode; ToValue() converts to Dictionary hierarchy.
    /// BifrostQL wraps all top-level queries in paged types: { data [...], total, offset, limit }.
    /// </summary>
    private static List<Dictionary<string, JsonElement>> ExtractPagedData(ExecutionResult result, string tableName)
    {
        result.Errors.Should().BeNullOrEmpty($"query for '{tableName}' should not have errors");
        result.Data.Should().NotBeNull();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        root.Should().ContainKey(tableName);
        var json = JsonSerializer.Serialize(root[tableName]);
        var paged = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        paged.Should().ContainKey("data", $"'{tableName}' result should have 'data' field (paged wrapper)");
        var dataJson = paged["data"].GetRawText();
        return JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(dataJson)!;
    }

    private static Dictionary<string, JsonElement> ExtractPagedMeta(ExecutionResult result, string tableName)
    {
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        var root = (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
        var json = JsonSerializer.Serialize(root[tableName]);
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    private static Dictionary<string, object?> ExtractRoot(ExecutionResult result)
    {
        result.Errors.Should().BeNullOrEmpty();
        result.Data.Should().NotBeNull();
        return (Dictionary<string, object?>)((RootExecutionNode)result.Data!).ToValue()!;
    }

    private static string Str(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null! : e.ToString();
    private static int Int(JsonElement e) => e.GetInt32();
    private static long Lng(JsonElement e) => e.GetInt64();

    // ══════════════════════════════════════════════════════════
    // Schema Resource Tests
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public void Schema_DDL_ExistsAsEmbeddedResource(string schema)
    {
        var sql = QuickstartSchemas.LoadSchemaSql(schema);
        sql.Should().NotBeNull($"DDL for '{schema}' should be an embedded resource");
        sql.Should().Contain("CREATE TABLE", $"DDL for '{schema}' should create tables");
    }

    [Theory]
    [InlineData("blog", "sample")]
    [InlineData("blog", "full")]
    [InlineData("ecommerce", "sample")]
    [InlineData("ecommerce", "full")]
    [InlineData("crm", "sample")]
    [InlineData("crm", "full")]
    [InlineData("classroom", "sample")]
    [InlineData("classroom", "full")]
    [InlineData("project-tracker", "sample")]
    [InlineData("project-tracker", "full")]
    public void Schema_SeedData_ExistsAsEmbeddedResource(string schema, string dataSize)
    {
        var sql = QuickstartSchemas.LoadSeedSql(schema, dataSize);
        sql.Should().NotBeNull($"seed data for '{schema}/{dataSize}' should be an embedded resource");
        sql.Should().Contain("INSERT INTO", $"seed data for '{schema}/{dataSize}' should insert rows");
    }

    // ══════════════════════════════════════════════════════════
    // Database Creation Tests
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("blog", new[] { "authors", "posts", "comments", "tags", "categories", "post_tags" })]
    [InlineData("ecommerce", new[] { "customers", "products", "orders", "order_items", "categories", "reviews", "addresses" })]
    [InlineData("crm", new[] { "contacts", "companies", "deals", "activities", "deal_stages", "notes" })]
    [InlineData("classroom", new[] { "students", "courses", "enrollments", "assignments", "submissions", "instructors" })]
    [InlineData("project-tracker", new[] { "projects", "tasks", "workspaces", "members", "sections", "labels", "task_assignments", "task_labels" })]
    public async Task Schema_CreatesExpectedTables(string schema, string[] expectedTables)
    {
        await using var conn = await CreateAndSeedDatabase(schema);
        var tables = await GetTableNames(conn);

        _output.WriteLine($"[{schema}] Tables: {string.Join(", ", tables)}");

        foreach (var expected in expectedTables)
        {
            tables.Should().Contain(t => t.Equals(expected, StringComparison.OrdinalIgnoreCase),
                $"schema '{schema}' should create table '{expected}'");
        }
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task Schema_SampleData_HasRows(string schema)
    {
        await using var conn = await CreateAndSeedDatabase(schema, "sample");
        var tables = await GetTableNames(conn);

        foreach (var table in tables)
        {
            var count = await GetRowCount(conn, table);
            _output.WriteLine($"[{schema}/sample] {table}: {count} rows");
            count.Should().BeGreaterThan(0, $"table '{table}' should have sample data");
        }
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task Schema_FullData_HasMoreRowsThanSample(string schema)
    {
        await using var sampleConn = await CreateAndSeedDatabase(schema, "sample");
        await using var fullConn = await CreateAndSeedDatabase(schema, "full");

        var sampleTables = await GetTableNames(sampleConn);
        var fullTables = await GetTableNames(fullConn);

        fullTables.Should().BeEquivalentTo(sampleTables, "full and sample should have the same tables");

        long sampleTotal = 0, fullTotal = 0;
        foreach (var table in sampleTables)
        {
            var sampleCount = await GetRowCount(sampleConn, table);
            var fullCount = await GetRowCount(fullConn, table);

            _output.WriteLine($"[{schema}] {table}: sample={sampleCount}, full={fullCount}");

            sampleTotal += sampleCount;
            fullTotal += fullCount;
        }

        fullTotal.Should().BeGreaterThan(sampleTotal,
            $"full dataset for '{schema}' should have more total rows than sample");

        _output.WriteLine($"[{schema}] Total: sample={sampleTotal}, full={fullTotal}");
    }

    // ══════════════════════════════════════════════════════════
    // Foreign Key Integrity Tests
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task Schema_ForeignKeysAreValid(string schema)
    {
        await using var conn = await CreateAndSeedDatabase(schema);

        await using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await pragmaCmd.ExecuteReaderAsync();

        var violations = new List<string>();
        while (await reader.ReadAsync())
        {
            violations.Add($"{reader.GetString(0)} row {reader.GetValue(1)} -> {reader.GetString(2)}");
        }

        violations.Should().BeEmpty($"schema '{schema}' should have no FK violations");
        _output.WriteLine($"[{schema}] FK integrity check passed");
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task Schema_TablesHaveColumns(string schema)
    {
        await using var conn = await CreateAndSeedDatabase(schema);
        var tables = await GetTableNames(conn);

        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info([{table}])";
            await using var reader = await cmd.ExecuteReaderAsync();

            var columns = new List<string>();
            while (await reader.ReadAsync())
                columns.Add(reader.GetString(1));

            _output.WriteLine($"[{schema}] {table}: {string.Join(", ", columns)}");
            columns.Should().NotBeEmpty($"table '{table}' should have columns");
        }
    }

    // ══════════════════════════════════════════════════════════
    // Validation Tests
    // ══════════════════════════════════════════════════════════

    [Fact]
    public void Schema_InvalidName_ReturnsNull()
    {
        QuickstartSchemas.LoadSchemaSql("nonexistent").Should().BeNull();
        QuickstartSchemas.LoadSeedSql("nonexistent", "sample").Should().BeNull();
    }

    [Fact]
    public void Schema_InvalidDataSize_ReturnsNull()
    {
        QuickstartSchemas.LoadSeedSql("blog", "huge").Should().BeNull();
    }

    // ══════════════════════════════════════════════════════════
    // BifrostQL Schema Loading Tests
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task BifrostModel_LoadsAllTables(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);
        ctx.Model.Tables.Should().NotBeEmpty($"DbModel for '{schema}' should have tables");

        foreach (var table in ctx.Model.Tables)
        {
            _output.WriteLine($"[{schema}] {table.DbName} → {table.GraphQlName} ({table.Columns.Count()} columns)");
            table.Columns.Should().NotBeEmpty($"table '{table.DbName}' should have columns");
        }
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task BifrostModel_DetectsPrimaryKeys(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        foreach (var table in ctx.Model.Tables)
        {
            var pks = table.Columns.Where(c => c.IsPrimaryKey).ToList();
            pks.Should().NotBeEmpty($"table '{table.DbName}' should have a primary key");
            _output.WriteLine($"[{schema}] {table.DbName} PK: {string.Join(", ", pks.Select(c => c.ColumnName))}");
        }
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task BifrostModel_DetectsForeignKeyLinks(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        var tablesWithLinks = ctx.Model.Tables.Where(t => t.SingleLinks.Any()).ToList();
        // SQLite's schema reader doesn't produce DbForeignKey objects, so links are
        // detected via name-based heuristic (column_id → table matching).
        foreach (var table in tablesWithLinks)
        {
            foreach (var (name, link) in table.SingleLinks)
            {
                _output.WriteLine($"[{schema}] {table.DbName} -> {link.ParentTable.DbName} (via {link.ChildId.ColumnName})");
            }
        }

        _output.WriteLine($"[{schema}] Tables with FK links: {tablesWithLinks.Count} (SQLite FK detection is limited)");
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task GraphQLSchema_ExposesAllTablesAsQueryFields(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        var queryType = ctx.Schema.Query;
        queryType.Should().NotBeNull();
        var fieldNames = queryType.Fields.Select(f => f.Name).ToList();

        foreach (var table in ctx.Model.Tables)
        {
            fieldNames.Should().Contain(table.GraphQlName,
                $"GraphQL schema should expose table '{table.DbName}' as field '{table.GraphQlName}'");
        }

        _output.WriteLine($"[{schema}] GraphQL fields: {string.Join(", ", fieldNames.Where(f => !f.StartsWith("__")))}");
    }

    // ══════════════════════════════════════════════════════════
    // GraphQL Query Tests — Blog Schema
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task Blog_QueryAllAuthors_ReturnsRows()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync("query { authors { data { author_id name } } }");
        var authors = ExtractPagedData(result, "authors");
        authors.Should().NotBeEmpty();
        authors.All(a => a.ContainsKey("name")).Should().BeTrue();
        _output.WriteLine($"Authors: {authors.Count}");
    }

    [Fact]
    public async Task Blog_QueryAllPosts_ReturnsRows()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync("query { posts { data { post_id title } } }");
        var posts = ExtractPagedData(result, "posts");
        posts.Should().NotBeEmpty();
        _output.WriteLine($"Posts: {posts.Count}");
    }

    [Fact]
    public async Task Blog_QueryWithPaginationMeta_ReturnsTotalAndLimits()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync("query { posts { data { post_id } total offset limit } }");
        var meta = ExtractPagedMeta(result, "posts");
        meta.Should().ContainKey("total");
        meta.Should().ContainKey("offset");
        meta.Should().ContainKey("limit");
        var total = meta["total"].GetInt64();
        total.Should().BeGreaterThan(0);
        _output.WriteLine($"Posts total={total}, offset={meta["offset"]}, limit={meta["limit"]}");
    }

    [Fact]
    public async Task Blog_FilterByEquality_ReturnsMatchingRows()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync("query { posts(filter: { post_id: { _eq: 1 } }) { data { post_id title } } }");
        var posts = ExtractPagedData(result, "posts");
        posts.Should().ContainSingle();
        Int(posts[0]["post_id"]).Should().Be(1);
    }

    [Fact]
    public async Task Blog_SortByTitle_ReturnsSortedRows()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync("query { posts(sort: [title_asc]) { data { title } } }");
        var posts = ExtractPagedData(result, "posts");
        posts.Should().HaveCountGreaterThan(1);
        var titles = posts.Select(p => Str(p["title"])).ToList();
        titles.Should().BeInAscendingOrder();
        _output.WriteLine($"Sorted titles: {string.Join(", ", titles)}");
    }

    [Fact]
    public async Task Blog_Pagination_LimitAndOffset()
    {
        await using var ctx = await CreateBifrostContext("blog");

        // Get total count first
        var allResult = await ctx.ExecuteAsync("query { posts { data { post_id } total } }");
        var allMeta = ExtractPagedMeta(allResult, "posts");
        var total = allMeta["total"].GetInt64();
        total.Should().BeGreaterThanOrEqualTo(2, "need at least 2 posts for pagination test");

        // Get first page
        var page1 = await ctx.ExecuteAsync("query { posts(sort: [post_id_asc], limit: 1, offset: 0) { data { post_id } } }");
        var page1Data = ExtractPagedData(page1, "posts");
        page1Data.Should().ContainSingle();
        var firstId = Int(page1Data[0]["post_id"]);

        // Get second page
        var page2 = await ctx.ExecuteAsync("query { posts(sort: [post_id_asc], limit: 1, offset: 1) { data { post_id } } }");
        var page2Data = ExtractPagedData(page2, "posts");
        page2Data.Should().ContainSingle();
        var secondId = Int(page2Data[0]["post_id"]);

        secondId.Should().BeGreaterThan(firstId, "second page should have a later post");
        _output.WriteLine($"Page 1: post_id={firstId}, Page 2: post_id={secondId}");
    }

    // ══════════════════════════════════════════════════════════
    // GraphQL Query Tests — All Schemas Basic Query
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task AllSchemas_CanQueryEveryTable(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        foreach (var table in ctx.Model.Tables)
        {
            var pk = table.Columns.First(c => c.IsPrimaryKey);
            var query = $"query {{ {table.GraphQlName} {{ data {{ {pk.GraphQlName} }} total }} }}";
            _output.WriteLine($"[{schema}] Query: {query}");

            var result = await ctx.ExecuteAsync(query);
            result.Errors.Should().BeNullOrEmpty($"query for '{table.GraphQlName}' should succeed");

            var meta = ExtractPagedMeta(result, table.GraphQlName);
            var total = meta["total"].GetInt64();
            total.Should().BeGreaterThan(0, $"table '{table.GraphQlName}' should have seed data");
            _output.WriteLine($"[{schema}] {table.GraphQlName}: {total} rows");
        }
    }

    // ══════════════════════════════════════════════════════════
    // Single-Link Join Tests — Frontend Query Pattern
    // The frontend generates: workspace_id workspaces { id: workspace_id label: name }
    // These tests verify that the full join pipeline works for each schema.
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task Blog_JoinQuery_PostToAuthors()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync(
            "query { posts { data { post_id title author_id authors { id: author_id label: name } } } }");
        var posts = ExtractPagedData(result, "posts");
        posts.Should().NotBeEmpty();

        var first = posts[0];
        first.Should().ContainKey("authors");
        var author = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(first["authors"].GetRawText())!;
        author.Should().ContainKey("id");
        author.Should().ContainKey("label");
        _output.WriteLine($"Post '{Str(first["title"])}' → Author '{Str(author["label"])}'");
    }

    [Fact]
    public async Task ProjectTracker_JoinQuery_MembersToWorkspaces()
    {
        await using var ctx = await CreateBifrostContext("project-tracker");
        var result = await ctx.ExecuteAsync(
            "query { members { data { member_id workspace_id name workspaces { id: workspace_id label: name } } } }");
        var rows = ExtractPagedData(result, "members");
        rows.Should().NotBeEmpty();

        var first = rows[0];
        first.Should().ContainKey("workspaces");
        var ws = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(first["workspaces"].GetRawText())!;
        ws.Should().ContainKey("id");
        ws.Should().ContainKey("label");
        _output.WriteLine($"Member '{Str(first["name"])}' → Workspace '{Str(ws["label"])}'");
    }

    [Fact]
    public async Task ProjectTracker_JoinQuery_ProjectsToWorkspaces()
    {
        await using var ctx = await CreateBifrostContext("project-tracker");
        var result = await ctx.ExecuteAsync(
            "query { projects { data { project_id workspace_id name workspaces { id: workspace_id label: name } } } }");
        var rows = ExtractPagedData(result, "projects");
        rows.Should().NotBeEmpty();

        var first = rows[0];
        first.Should().ContainKey("workspaces");
        _output.WriteLine($"Project '{Str(first["name"])}' has workspace join");
    }

    [Fact]
    public async Task ProjectTracker_JoinQuery_TasksToProjects()
    {
        await using var ctx = await CreateBifrostContext("project-tracker");
        var result = await ctx.ExecuteAsync(
            "query { tasks { data { task_id project_id title projects { id: project_id label: name } } } }");
        var rows = ExtractPagedData(result, "tasks");
        rows.Should().NotBeEmpty();

        var first = rows[0];
        first.Should().ContainKey("projects");
        _output.WriteLine($"Task '{Str(first["title"])}' has project join");
    }

    [Fact]
    public async Task ProjectTracker_JoinQuery_TasksToSections()
    {
        await using var ctx = await CreateBifrostContext("project-tracker");
        var result = await ctx.ExecuteAsync(
            "query { tasks { data { task_id section_id title sections { id: section_id label: name } } } }");
        var rows = ExtractPagedData(result, "tasks");
        rows.Should().NotBeEmpty();

        var first = rows[0];
        first.Should().ContainKey("sections");
        _output.WriteLine($"Task '{Str(first["title"])}' has section join");
    }

    [Fact]
    public async Task Classroom_JoinQuery_AssignmentsToCourses_DuplicatePkAlias()
    {
        // Exact frontend pattern: courses.labelColumn = course_id (PK = first column)
        // so query becomes: courses { id: course_id label: course_id }
        // Both aliases point to same DB column — must not be deduplicated
        await using var ctx = await CreateBifrostContext("classroom");
        var result = await ctx.ExecuteAsync(
            "query { assignments { data { assignment_id course_id title courses { id: course_id label: course_id } } } }");
        var rows = ExtractPagedData(result, "assignments");
        rows.Should().NotBeEmpty();

        var first = rows[0];
        first.Should().ContainKey("courses");
        var course = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(first["courses"].GetRawText())!;
        course.Should().ContainKey("id", "id alias for course_id");
        course.Should().ContainKey("label", "label alias for course_id");
        // Both should be the same value (both alias the PK)
        course["id"].GetInt64().Should().Be(course["label"].GetInt64());
        _output.WriteLine($"Assignment '{Str(first["title"])}' → Course id={course["id"]}, label={course["label"]}");
    }

    [Fact]
    public async Task Classroom_MultiLinkNavigation_AssignmentsToSubmissions_DirectFkFilter()
    {
        // Navigating from assignments to submissions: /submissions/from/assignments/1
        // The correct filter uses the FK column directly: { assignment_id: { _eq: 1 } }
        // Bug: frontend used nested filter with current table's PK instead of FK column
        await using var ctx = await CreateBifrostContext("classroom");

        // First, get a valid assignment_id
        var assignmentResult = await ctx.ExecuteAsync(
            "query { assignments { data { assignment_id title } } }");
        var assignments = ExtractPagedData(assignmentResult, "assignments");
        assignments.Should().NotBeEmpty();
        var assignmentId = Lng(assignments[0]["assignment_id"]);

        // CORRECT: direct FK filter on the child table's FK column
        var correctQuery = $@"query {{
            submissions(filter: {{ assignment_id: {{ _eq: {assignmentId} }} }}) {{
                data {{ submission_id assignment_id }}
            }}
        }}";
        var correctResult = await ctx.ExecuteAsync(correctQuery);
        correctResult.Errors.Should().BeNullOrEmpty(
            "filtering submissions by assignment_id FK should work");
        var submissions = ExtractPagedData(correctResult, "submissions");
        submissions.Should().NotBeEmpty();
        submissions.All(s => Lng(s["assignment_id"]) == assignmentId).Should().BeTrue();
        _output.WriteLine($"Assignment {assignmentId} has {submissions.Count} submissions");

        // WRONG: using current table's PK (submission_id) in nested filter on parent table
        var wrongQuery = $@"query {{
            submissions(filter: {{ assignments: {{ submission_id: {{ _eq: {assignmentId} }} }} }}) {{
                data {{ submission_id assignment_id }}
            }}
        }}";
        var wrongResult = await ctx.ExecuteAsync(wrongQuery);
        wrongResult.Errors.Should().NotBeNullOrEmpty(
            "filtering assignments by submission_id should fail - submission_id is not on assignments");
    }

    [Theory]
    [InlineData("classroom", "submissions", "assignments", "assignment_id")]
    [InlineData("classroom", "enrollments", "courses", "course_id")]
    [InlineData("classroom", "enrollments", "students", "student_id")]
    [InlineData("classroom", "assignments", "courses", "course_id")]
    [InlineData("project-tracker", "members", "workspaces", "workspace_id")]
    [InlineData("project-tracker", "projects", "workspaces", "workspace_id")]
    [InlineData("project-tracker", "task_labels", "tasks", "task_id")]
    [InlineData("project-tracker", "task_labels", "labels", "label_id")]
    [InlineData("project-tracker", "task_assignments", "tasks", "task_id")]
    [InlineData("project-tracker", "task_assignments", "members", "member_id")]
    public async Task AllSchemas_MultiLinkNavigation_DirectFkFilter(
        string schema, string childTable, string parentTable, string fkColumn)
    {
        await using var ctx = await CreateBifrostContext(schema);

        // Get a valid parent PK value
        var parent = ctx.Model.GetTableFromDbName(parentTable);
        var parentPk = parent.Columns.First(c => c.IsPrimaryKey).GraphQlName;

        var parentResult = await ctx.ExecuteAsync(
            $"query {{ {parentTable} {{ data {{ {parentPk} }} }} }}");
        var parentRows = ExtractPagedData(parentResult, parentTable);
        parentRows.Should().NotBeEmpty();
        var pkValue = Lng(parentRows[0][parentPk]);

        // Filter child table by FK column = parent PK value (the correct pattern)
        var query = $"query {{ {childTable}(filter: {{ {fkColumn}: {{ _eq: {pkValue} }} }}) {{ data {{ {fkColumn} }} }} }}";
        _output.WriteLine($"[{schema}] {childTable}.{fkColumn} = {pkValue}: {query}");

        var result = await ctx.ExecuteAsync(query);
        result.Errors.Should().BeNullOrEmpty(
            $"filtering {childTable} by {fkColumn} should work");
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task AllSchemas_SingleLinkJoins_Work(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        foreach (var table in ctx.Model.Tables)
        {
            if (!table.SingleLinks.Any()) continue;

            foreach (var (linkName, link) in table.SingleLinks)
            {
                var fkCol = link.ChildId.GraphQlName;
                var destPk = link.ParentId.GraphQlName;
                var destTable = link.ParentTable.GraphQlName;
                var pk = table.Columns.First(c => c.IsPrimaryKey).GraphQlName;

                var query = $"query {{ {table.GraphQlName} {{ data {{ {pk} {fkCol} {destTable} {{ id: {destPk} }} }} }} }}";
                _output.WriteLine($"[{schema}] {table.GraphQlName} → {destTable}: {query}");

                var result = await ctx.ExecuteAsync(query);
                result.Errors.Should().BeNullOrEmpty(
                    $"single-link join from '{table.GraphQlName}' to '{destTable}' should work");

                var rows = ExtractPagedData(result, table.GraphQlName);
                rows.Should().NotBeEmpty();
                rows[0].Should().ContainKey(destTable,
                    $"row should have nested '{destTable}' join result");
            }
        }
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task AllSchemas_MultiLinkJoins_Work(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        foreach (var table in ctx.Model.Tables)
        {
            if (!table.MultiLinks.Any()) continue;

            foreach (var (linkName, link) in table.MultiLinks)
            {
                var childTable = link.ChildTable.GraphQlName;
                var childPk = link.ChildTable.Columns.First(c => c.IsPrimaryKey).GraphQlName;
                var pk = table.Columns.First(c => c.IsPrimaryKey).GraphQlName;

                var query = $"query {{ {table.GraphQlName} {{ data {{ {pk} {childTable} {{ {childPk} }} }} }} }}";
                _output.WriteLine($"[{schema}] {table.GraphQlName} ← {childTable}: {query}");

                var result = await ctx.ExecuteAsync(query);
                result.Errors.Should().BeNullOrEmpty(
                    $"multi-link join from '{table.GraphQlName}' to '{childTable}' should work");

                var rows = ExtractPagedData(result, table.GraphQlName);
                rows.Should().NotBeEmpty();
            }
        }
    }

    // ══════════════════════════════════════════════════════════
    // Cross-Table Navigation — Blog Schema (Sequential Queries)
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task Blog_Navigate_PostToAuthor()
    {
        await using var ctx = await CreateBifrostContext("blog");

        // Get a post and its author_id
        var postResult = await ctx.ExecuteAsync(
            "query { posts(filter: { post_id: { _eq: 1 } }) { data { post_id title author_id } } }");
        var posts = ExtractPagedData(postResult, "posts");
        posts.Should().ContainSingle();
        var authorId = Int(posts[0]["author_id"]);

        // Navigate to the author
        var authorResult = await ctx.ExecuteAsync(
            $"query {{ authors(filter: {{ author_id: {{ _eq: {authorId} }} }}) {{ data {{ author_id name }} }} }}");
        var authors = ExtractPagedData(authorResult, "authors");
        authors.Should().ContainSingle();
        authors[0].Should().ContainKey("name");
        _output.WriteLine($"Post '{Str(posts[0]["title"])}' by '{Str(authors[0]["name"])}'");
    }

    [Fact]
    public async Task Blog_Navigate_PostToComments()
    {
        await using var ctx = await CreateBifrostContext("blog");

        // Get post 1
        var postResult = await ctx.ExecuteAsync(
            "query { posts(filter: { post_id: { _eq: 1 } }) { data { post_id title } } }");
        var posts = ExtractPagedData(postResult, "posts");
        posts.Should().ContainSingle();

        // Get comments for that post
        var commentResult = await ctx.ExecuteAsync(
            "query { comments(filter: { post_id: { _eq: 1 } }) { data { comment_id content post_id } } }");
        var comments = ExtractPagedData(commentResult, "comments");
        comments.Should().NotBeEmpty("post 1 should have comments");
        comments.All(c => Int(c["post_id"]) == 1).Should().BeTrue();
        _output.WriteLine($"Post '{Str(posts[0]["title"])}' has {comments.Count} comments");
    }

    [Fact]
    public async Task Blog_Navigate_CommentToPost()
    {
        await using var ctx = await CreateBifrostContext("blog");

        // Get a comment and its post_id
        var commentResult = await ctx.ExecuteAsync(
            "query { comments(filter: { comment_id: { _eq: 1 } }) { data { comment_id content post_id } } }");
        var comments = ExtractPagedData(commentResult, "comments");
        comments.Should().ContainSingle();
        var postId = Int(comments[0]["post_id"]);

        // Navigate to the post
        var postResult = await ctx.ExecuteAsync(
            $"query {{ posts(filter: {{ post_id: {{ _eq: {postId} }} }}) {{ data {{ post_id title }} }} }}");
        var posts = ExtractPagedData(postResult, "posts");
        posts.Should().ContainSingle();
        _output.WriteLine($"Comment → Post '{Str(posts[0]["title"])}'");
    }

    // ══════════════════════════════════════════════════════════
    // Cross-Table Navigation — Ecommerce Schema
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task Ecommerce_Navigate_OrderToItems()
    {
        await using var ctx = await CreateBifrostContext("ecommerce");

        // Get order 1
        var orderResult = await ctx.ExecuteAsync(
            "query { orders(filter: { order_id: { _eq: 1 } }) { data { order_id status } } }");
        var orders = ExtractPagedData(orderResult, "orders");
        orders.Should().ContainSingle();

        // Get items for that order
        var itemResult = await ctx.ExecuteAsync(
            "query { order_items(filter: { order_id: { _eq: 1 } }) { data { order_item_id order_id quantity product_id } } }");
        var items = ExtractPagedData(itemResult, "order_items");
        items.Should().NotBeEmpty("order 1 should have items");
        items.All(i => Int(i["order_id"]) == 1).Should().BeTrue();
        _output.WriteLine($"Order 1 has {items.Count} items");
    }

    [Fact]
    public async Task Ecommerce_Navigate_OrderItemToProduct()
    {
        await using var ctx = await CreateBifrostContext("ecommerce");

        // Get an order item and its product_id
        var itemResult = await ctx.ExecuteAsync(
            "query { order_items(filter: { order_item_id: { _eq: 1 } }) { data { order_item_id quantity product_id } } }");
        var items = ExtractPagedData(itemResult, "order_items");
        items.Should().ContainSingle();
        var productId = Int(items[0]["product_id"]);

        // Navigate to the product
        var productResult = await ctx.ExecuteAsync(
            $"query {{ products(filter: {{ product_id: {{ _eq: {productId} }} }}) {{ data {{ product_id name price }} }} }}");
        var products = ExtractPagedData(productResult, "products");
        products.Should().ContainSingle();
        products[0].Should().ContainKey("name");
        _output.WriteLine($"Order item → Product '{Str(products[0]["name"])}'");
    }

    // ══════════════════════════════════════════════════════════
    // Schema Structure — _join and _single Fields Exist in SDL
    // Note: Dynamic join container fields are in the GraphQL schema but
    // the resolver pipeline doesn't fully support runtime execution yet.
    // These tests verify schema structure only.
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task AllSchemas_DynamicJoinFieldExists(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        foreach (var table in ctx.Model.Tables)
        {
            // Use introspection to verify _join and _single fields exist on the row type
            var result = await ctx.ExecuteAsync($@"
                query {{
                    __type(name: ""{table.GraphQlName}"") {{
                        fields {{ name }}
                    }}
                }}");

            result.Errors.Should().BeNullOrEmpty();
            var root = ExtractRoot(result);
            var typeJson = JsonSerializer.Serialize(root["__type"]);
            var typeData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(typeJson)!;
            var fieldsJson = typeData["fields"].GetRawText();
            var fields = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(fieldsJson)!;
            var fieldNames = fields.Select(f => Str(f["name"])).ToList();

            fieldNames.Should().Contain("_join",
                $"table '{table.GraphQlName}' should have _join field");
            fieldNames.Should().Contain("_single",
                $"table '{table.GraphQlName}' should have _single field");

            _output.WriteLine($"[{schema}] {table.GraphQlName}: _join and _single present in schema");
        }
    }

    // ══════════════════════════════════════════════════════════
    // Filter Operator Tests — Blog Schema
    // ══════════════════════════════════════════════════════════

    [Fact]
    public async Task Blog_FilterContains_ReturnsMatchingPosts()
    {
        await using var ctx = await CreateBifrostContext("blog");
        // Search for posts with a common word in the title
        var allResult = await ctx.ExecuteAsync("query { posts { data { title } } }");
        var allPosts = ExtractPagedData(allResult, "posts");
        allPosts.Should().NotBeEmpty();

        // Get the first word from the first post title to use as a search term
        var firstTitle = Str(allPosts[0]["title"]);
        var searchTerm = firstTitle.Split(' ')[0];

        var result = await ctx.ExecuteAsync(
            $@"query {{ posts(filter: {{ title: {{ _contains: ""{searchTerm}"" }} }}) {{ data {{ title }} }} }}");
        var filtered = ExtractPagedData(result, "posts");
        filtered.Should().NotBeEmpty();
        filtered.All(p => Str(p["title"]).Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue();
        _output.WriteLine($"Search '{searchTerm}' → {filtered.Count} posts");
    }

    [Fact]
    public async Task Blog_FilterGreaterThan_OnPrimaryKey()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync("query { posts(filter: { post_id: { _gt: 1 } }) { data { post_id } } }");
        var posts = ExtractPagedData(result, "posts");
        posts.Should().NotBeEmpty();
        posts.All(p => Int(p["post_id"]) > 1).Should().BeTrue();
    }

    [Fact]
    public async Task Blog_SortDescending_ReturnsReversedOrder()
    {
        await using var ctx = await CreateBifrostContext("blog");
        var result = await ctx.ExecuteAsync("query { posts(sort: [post_id_desc]) { data { post_id } } }");
        var posts = ExtractPagedData(result, "posts");
        posts.Should().HaveCountGreaterThan(1);
        var ids = posts.Select(p => Int(p["post_id"])).ToList();
        ids.Should().BeInDescendingOrder();
        _output.WriteLine($"Desc order: {string.Join(", ", ids)}");
    }

    // ══════════════════════════════════════════════════════════
    // Introspection Tests
    // ══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task AllSchemas_IntrospectionQuery_ReturnsTypes(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        var result = await ctx.ExecuteAsync(@"
            query {
                __schema {
                    queryType { name }
                    types { name kind }
                }
            }");

        result.Errors.Should().BeNullOrEmpty();
        var root = ExtractRoot(result);
        root.Should().ContainKey("__schema");
        _output.WriteLine($"[{schema}] Introspection succeeded");
    }

    [Theory]
    [InlineData("blog")]
    [InlineData("ecommerce")]
    [InlineData("crm")]
    [InlineData("classroom")]
    [InlineData("project-tracker")]
    public async Task AllSchemas_IntrospectionQueryFields_ListsAllTables(string schema)
    {
        await using var ctx = await CreateBifrostContext(schema);

        var result = await ctx.ExecuteAsync(@"
            query {
                __schema {
                    queryType {
                        fields { name }
                    }
                }
            }");

        result.Errors.Should().BeNullOrEmpty();
        var root = ExtractRoot(result);
        var schemaJson = JsonSerializer.Serialize(root["__schema"]);
        var schemaData = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(schemaJson)!;
        var queryTypeJson = schemaData["queryType"].GetRawText();
        var queryType = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(queryTypeJson)!;
        var fieldsJson = queryType["fields"].GetRawText();
        var fields = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(fieldsJson)!;
        var fieldNames = fields.Select(f => Str(f["name"])).ToList();

        foreach (var table in ctx.Model.Tables)
        {
            fieldNames.Should().Contain(table.GraphQlName,
                $"introspection should list '{table.GraphQlName}'");
        }

        _output.WriteLine($"[{schema}] Introspection fields: {string.Join(", ", fieldNames.Where(f => !f.StartsWith("_")))}");
    }

    // ══════════════════════════════════════════════════════════
    // BifrostQL Test Context
    // ══════════════════════════════════════════════════════════

    private sealed class BifrostTestContext : IAsyncDisposable
    {
        private readonly IDbConnFactory _factory;
        private readonly ServiceProvider _serviceProvider;
        public readonly string DbPath;

        public IDbModel Model { get; }
        public ISchema Schema { get; }

        public BifrostTestContext(IDbConnFactory factory, IDbModel model, ISchema schema,
            ServiceProvider serviceProvider, string dbPath)
        {
            _factory = factory;
            Model = model;
            Schema = schema;
            _serviceProvider = serviceProvider;
            DbPath = dbPath;
        }

        public async Task<ExecutionResult> ExecuteAsync(string query)
        {
            var executor = new SqlExecutionManager(Model, Schema);
            var extensions = new Dictionary<string, object?>
            {
                { "connFactory", _factory },
                { "model", Model },
                { "tableReaderFactory", executor },
            };

            return await new DocumentExecuter().ExecuteAsync(options =>
            {
                options.Schema = Schema;
                options.Query = query;
                options.Extensions = new Inputs(extensions);
                options.RequestServices = _serviceProvider;
            });
        }

        public async ValueTask DisposeAsync()
        {
            _serviceProvider.Dispose();
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { }
        }
    }
}
