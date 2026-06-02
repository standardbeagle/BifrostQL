using BifrostQL.Core.Model;
using BifrostQL.Model;
using BifrostQL.Sqlite;
using BifrostQL.UI.NativeBridge;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Xunit;

namespace BifrostQL.UI.Tests;

/// <summary>
/// Verifies <see cref="BuilderSchemaProjection.Project"/> against a real SQLite
/// model, including the acceptance requirement that composite foreign keys are
/// projected as parallel column arrays. Uses a temp file (not :memory:) so the
/// schema reader sees the tables created on a prior connection.
/// </summary>
public sealed class BuilderSchemaProjectionTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteDbConnFactory _factory;

    public BuilderSchemaProjectionTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"bifrost-builder-{Guid.NewGuid():N}.db");
        _factory = new SqliteDbConnFactory($"Data Source={_dbPath}");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    private async Task<IDbModel> SeedAndLoadAsync()
    {
        var ddl = new[]
        {
            "CREATE TABLE parent (tenant_id INTEGER NOT NULL, id INTEGER NOT NULL, name TEXT, PRIMARY KEY (tenant_id, id))",
            "CREATE TABLE child (id INTEGER PRIMARY KEY, tenant_id INTEGER NOT NULL, parent_id INTEGER NOT NULL, " +
            "FOREIGN KEY (tenant_id, parent_id) REFERENCES parent(tenant_id, id))",
        };

        await using (var conn = _factory.GetConnection())
        {
            await conn.OpenAsync();
            foreach (var stmt in ddl)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var loader = new DbModelLoader(_factory, new MetadataLoader(Array.Empty<string>()));
        return await loader.LoadAsync();
    }

    [Fact]
    public async Task Project_ReturnsTablesAndColumns()
    {
        var model = await SeedAndLoadAsync();

        var dto = BuilderSchemaProjection.Project(model);

        dto.Tables.Select(t => t.Name).Should().Contain(new[] { "parent", "child" });

        // Qualified name carries whatever schema the provider reports (SQLite uses
        // "main"), so resolve it from the projection rather than assuming.
        var childQ = dto.Tables.Single(t => t.Name == "child").Qualified;

        var childCols = dto.Columns.Where(c => c.Table == childQ).Select(c => c.Name).ToList();
        childCols.Should().Contain(new[] { "id", "tenant_id", "parent_id" });

        // child.id is the primary key.
        dto.Columns.Should().ContainSingle(c => c.Table == childQ && c.Name == "id" && c.IsPrimaryKey);
    }

    private async Task<IDbModel> SeedJunctionAsync()
    {
        var ddl = new[]
        {
            "CREATE TABLE students (id INTEGER PRIMARY KEY, name TEXT)",
            "CREATE TABLE courses (id INTEGER PRIMARY KEY, title TEXT)",
            "CREATE TABLE enrollments (student_id INTEGER NOT NULL, course_id INTEGER NOT NULL, " +
            "PRIMARY KEY (student_id, course_id), " +
            "FOREIGN KEY (student_id) REFERENCES students(id), " +
            "FOREIGN KEY (course_id) REFERENCES courses(id))",
        };

        await using (var conn = _factory.GetConnection())
        {
            await conn.OpenAsync();
            foreach (var stmt in ddl)
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = stmt;
                await cmd.ExecuteNonQueryAsync();
            }
        }

        var loader = new DbModelLoader(_factory, new MetadataLoader(Array.Empty<string>()));
        return await loader.LoadAsync();
    }

    [Fact]
    public async Task Project_ManyToMany_ProjectsThroughJunction()
    {
        var model = await SeedJunctionAsync();

        var dto = BuilderSchemaProjection.Project(model);

        var studentsQ = dto.Tables.Single(t => t.Name == "students").Qualified;
        var coursesQ = dto.Tables.Single(t => t.Name == "courses").Qualified;
        var enrollQ = dto.Tables.Single(t => t.Name == "enrollments").Qualified;

        // A students<->courses bridge through enrollments, projected in both directions.
        var link = dto.ManyToMany.Should()
            .ContainSingle(m => m.SourceTable == studentsQ && m.TargetTable == coursesQ).Subject;
        link.JunctionTable.Should().Be(enrollQ);
        link.SourceColumns.Should().Equal("id");
        link.JunctionSourceColumns.Should().Equal("student_id");
        link.JunctionTargetColumns.Should().Equal("course_id");
        link.TargetColumns.Should().Equal("id");

        dto.ManyToMany.Should().Contain(m => m.SourceTable == coursesQ && m.TargetTable == studentsQ);
    }

    [Fact]
    public async Task Project_CompositeForeignKey_HasParallelColumnArrays()
    {
        var model = await SeedAndLoadAsync();

        var dto = BuilderSchemaProjection.Project(model);

        var childQ = dto.Tables.Single(t => t.Name == "child").Qualified;
        var parentQ = dto.Tables.Single(t => t.Name == "parent").Qualified;

        // The child->parent FK is composite (tenant_id + parent_id -> tenant_id + id).
        var rel = dto.Relationships.Should().ContainSingle(r => r.LeftTable == childQ).Subject;

        rel.LeftColumns.Should().HaveCount(2);
        rel.RightColumns.Should().HaveCount(2);
        rel.RightTable.Should().Be(parentQ);
        // Column pairs are parallel and reference the parent PK columns.
        rel.LeftColumns.Should().Contain(new[] { "tenant_id", "parent_id" });
        rel.RightColumns.Should().Contain(new[] { "tenant_id", "id" });
    }
}
