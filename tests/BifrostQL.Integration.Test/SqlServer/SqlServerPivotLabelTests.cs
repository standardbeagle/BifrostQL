using BifrostQL.Core.QueryModel;
using BifrostQL.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BifrostQL.Integration.Test.SqlServer;

/// <summary>
/// SQL Server pivots run the portable CASE WHEN cross-tab, binding each pivot value as a
/// parameter, so every label shape the retired native T-SQL PIVOT mishandled pivots end to
/// end: non-string values (PIVOT matched <c>CAST(col AS NVARCHAR(MAX))</c> against .NET
/// text, so a <c>bit</c>'s cells read NULL), labels that cannot be identifiers (empty, over
/// 128 characters), and labels that one collation folds together.
/// </summary>
public sealed class SqlServerPivotLabelTests
{
    private static readonly Guid First = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    [SkippableTheory]
    [InlineData("flag")]
    [InlineData("token")]
    [InlineData("label")]
    public async Task Pivot_cells_match_their_values_for_each_pivot_column_type(string pivotColumn)
    {
        var connString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        Skip.If(string.IsNullOrEmpty(connString), "BIFROST_TEST_SQLSERVER not set");

        var table = $"bq_pivot_{Guid.NewGuid():N}";
        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"""
            CREATE TABLE [dbo].[{table}] (id int PRIMARY KEY, region nvarchar(20) NOT NULL,
                flag bit NOT NULL, token uniqueidentifier NOT NULL, label nvarchar(20) NOT NULL);
            INSERT INTO [dbo].[{table}] VALUES
                (1, N'north', 1, '{First}', N'a'),
                (2, N'north', 0, NEWID(), N'b');
            """);
        try
        {
            // The pivot values reach the generator as CLR values, as the reader hands them over.
            var pivotValues = new List<object?>();
            await using (var distinct = new SqlCommand($"SELECT DISTINCT [{pivotColumn}] FROM [dbo].[{table}]", conn))
            await using (var reader = await distinct.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    pivotValues.Add(reader.GetValue(0));

            var config = PivotQueryConfig.Create(pivotColumn, "id", "COUNT", new[] { "region" });
            var pivot = PivotSqlGenerator.GeneratePivot(SqlServerDialect.Instance, config, $"[dbo].[{table}]", pivotValues);

            await using var cmd = new SqlCommand(pivot.Sql, conn);
            foreach (var p in pivot.Parameters)
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            await using var rows = await cmd.ExecuteReaderAsync();
            (await rows.ReadAsync()).Should().BeTrue();
            for (var position = 0; position < pivotValues.Count; position++)
            {
                var label = pivotValues[position]!.ToString()!;
                rows[PivotSqlGenerator.ColumnAlias(position)].Should().Be(1, $"one row has {pivotColumn} = {label}");
            }
        }
        finally
        {
            await ExecAsync(conn, $"DROP TABLE [dbo].[{table}]");
        }
    }

    /// <summary>
    /// The retired native PIVOT named each label as a bracketed identifier, so a label
    /// longer than 128 characters was error 103 and an empty one error 1038. A bound
    /// parameter has neither limit. "short" is the control column in every case.
    /// </summary>
    [SkippableTheory]
    [InlineData(129)]
    [InlineData(0)]
    [InlineData(128)]
    public async Task Pivot_label_that_cannot_be_an_identifier_keeps_its_cell(int labelLength)
    {
        var connString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        Skip.If(string.IsNullOrEmpty(connString), "BIFROST_TEST_SQLSERVER not set");

        var label = new string('x', labelLength);
        var table = $"bq_pivot_{Guid.NewGuid():N}";
        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"""
            CREATE TABLE [dbo].[{table}] (id int PRIMARY KEY, region nvarchar(20) NOT NULL, status nvarchar(200) NOT NULL);
            INSERT INTO [dbo].[{table}] VALUES (1, N'north', N'{label}'), (2, N'north', N'short');
            """);
        try
        {
            var pivotValues = new List<object?> { label, "short" };
            var config = PivotQueryConfig.Create("status", "id", "COUNT", new[] { "region" });
            var pivot = PivotSqlGenerator.GeneratePivot(SqlServerDialect.Instance, config, $"[dbo].[{table}]", pivotValues);

            await using var cmd = new SqlCommand(pivot.Sql, conn);
            foreach (var p in pivot.Parameters)
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            await using var rows = await cmd.ExecuteReaderAsync();
            (await rows.ReadAsync()).Should().BeTrue();
            rows[PivotSqlGenerator.ColumnAlias(0)].Should().Be(1, $"one row has the {labelLength}-character label");
            rows[PivotSqlGenerator.ColumnAlias(1)].Should().Be(1, "one row has the label 'short'");
        }
        finally
        {
            await ExecAsync(conn, $"DROP TABLE [dbo].[{table}]");
        }
    }

    /// <summary>
    /// The retired native PIVOT compared its output identifiers with trailing spaces
    /// ignored, so two labels that differ only by them failed with error 8156. Discovery's
    /// DISTINCT folds such values together, but a NULL takes the null label <c>_null_</c>
    /// after discovery, so a literal <c>"_null_ "</c> beside a NULL still yields the pair;
    /// the NULL arm is <c>IS NULL</c>, so each cell counts its own rows. A whitespace-only
    /// label is the control.
    /// </summary>
    [SkippableTheory]
    [InlineData("_null_ ", true)]
    [InlineData("   ", false)]
    public async Task Pivot_labels_that_differ_only_by_trailing_spaces_keep_their_cells(string literal, bool collides)
    {
        var connString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        Skip.If(string.IsNullOrEmpty(connString), "BIFROST_TEST_SQLSERVER not set");

        var table = $"bq_pivot_{Guid.NewGuid():N}";
        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"""
            CREATE TABLE [dbo].[{table}] (id int PRIMARY KEY, region nvarchar(20) NOT NULL, status nvarchar(20) NULL);
            INSERT INTO [dbo].[{table}] VALUES (1, N'north', N'{literal}'), (2, N'north', NULL), (3, N'north', NULL);
            """);
        try
        {
            var pivotValues = new List<object?>();
            await using (var distinct = new SqlCommand($"SELECT DISTINCT [status] FROM [dbo].[{table}]", conn))
            await using (var reader = await distinct.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    pivotValues.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            pivotValues.Should().HaveCount(2);

            var config = PivotQueryConfig.Create("status", "id", "COUNT", new[] { "region" });
            var labels = pivotValues.Select(v => v?.ToString() ?? config.NullLabel).ToList();
            (labels[0].TrimEnd() == labels[1].TrimEnd()).Should().Be(collides);
            var pivot = PivotSqlGenerator.GeneratePivot(SqlServerDialect.Instance, config, $"[dbo].[{table}]", pivotValues);

            await using var cmd = new SqlCommand(pivot.Sql, conn);
            foreach (var p in pivot.Parameters)
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            await using var rows = await cmd.ExecuteReaderAsync();
            (await rows.ReadAsync()).Should().BeTrue();
            for (var position = 0; position < pivotValues.Count; position++)
                rows[PivotSqlGenerator.ColumnAlias(position)].Should().Be(
                    pivotValues[position] == null ? 2 : 1, $"cell {position} counts the rows of its own value");
        }
        finally
        {
            await ExecAsync(conn, $"DROP TABLE [dbo].[{table}]");
        }
    }

    /// <summary>
    /// The retired native PIVOT named output columns by label, compared under the database
    /// collation. In an accent-insensitive database, <c>cafe</c> and <c>café</c> from an
    /// accent-sensitive column survive discovery's DISTINCT as two values but named one
    /// output column twice (error 8156). The cross-tab names no column by label.
    /// </summary>
    [SkippableFact]
    public async Task Pivot_labels_that_collide_only_under_an_accent_insensitive_collation_keep_their_cells()
    {
        var master = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        Skip.If(string.IsNullOrEmpty(master), "BIFROST_TEST_SQLSERVER not set");

        var database = $"bq_pivot_ai_{Guid.NewGuid():N}";
        await using (var conn = new SqlConnection(master))
        {
            await conn.OpenAsync();
            await ExecAsync(conn, $"CREATE DATABASE [{database}] COLLATE Latin1_General_CI_AI");
        }
        try
        {
            var connString = new SqlConnectionStringBuilder(master) { InitialCatalog = database }.ConnectionString;
            await using var conn = new SqlConnection(connString);
            await conn.OpenAsync();
            await ExecAsync(conn, """
                CREATE TABLE [dbo].[t] (id int PRIMARY KEY, region nvarchar(20) NOT NULL,
                    status nvarchar(20) COLLATE Latin1_General_CI_AS NOT NULL);
                INSERT INTO [dbo].[t] VALUES (1, N'north', N'cafe'), (2, N'north', N'café'), (3, N'north', N'café');
                """);

            var pivotValues = new List<object?>();
            await using (var distinct = new SqlCommand("SELECT DISTINCT [status] FROM [dbo].[t] ORDER BY [status]", conn))
            await using (var reader = await distinct.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    pivotValues.Add(reader.GetString(0));
            pivotValues.Should().Equal("cafe", "café");

            var config = PivotQueryConfig.Create("status", "id", "COUNT", new[] { "region" });
            var pivot = PivotSqlGenerator.GeneratePivot(SqlServerDialect.Instance, config, "[dbo].[t]", pivotValues);

            await using var cmd = new SqlCommand(pivot.Sql, conn);
            foreach (var p in pivot.Parameters)
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            await using var rows = await cmd.ExecuteReaderAsync();
            (await rows.ReadAsync()).Should().BeTrue();
            rows[PivotSqlGenerator.ColumnAlias(0)].Should().Be(1, "one row has 'cafe'");
            rows[PivotSqlGenerator.ColumnAlias(1)].Should().Be(2, "two rows have 'café'");
        }
        // A failing drop must not replace the test's own failure.
        catch (Exception testFailure)
        {
            await Infrastructure.TestDatabaseDrop.RethrowAfterDropAsync(testFailure, () => Infrastructure.TestDatabaseDrop.SqlServerAsync(master!, database));
        }
        await Infrastructure.TestDatabaseDrop.SqlServerAsync(master!, database);
    }

    /// <summary>
    /// Labels that some collation folds together (accent, kana, width, combining marks,
    /// ignorable characters) are two values in a binary-collated column, and each keeps its
    /// own cell: the cross-tab matches each bound parameter under the column's collation and
    /// never compares labels in .NET.
    /// </summary>
    [SkippableTheory]
    [InlineData("cafe", "café")]
    [InlineData("ｶﾀ", "カタ")]
    [InlineData("かた", "カタ")]
    [InlineData("A", "Ａ")]
    [InlineData("cafe\u0301", "café")]
    [InlineData("co\u00ADop", "coop")]
    public async Task Pivot_labels_equal_only_under_collation_folding_keep_their_cells(string first, string second)
    {
        var connString = Environment.GetEnvironmentVariable("BIFROST_TEST_SQLSERVER");
        Skip.If(string.IsNullOrEmpty(connString), "BIFROST_TEST_SQLSERVER not set");

        var table = $"bq_pivot_{Guid.NewGuid():N}";
        await using var conn = new SqlConnection(connString);
        await conn.OpenAsync();
        await ExecAsync(conn, $"""
            CREATE TABLE [dbo].[{table}] (id int PRIMARY KEY, region nvarchar(20) NOT NULL,
                status nvarchar(20) COLLATE Latin1_General_100_BIN2 NOT NULL);
            """);
        try
        {
            await using (var insert = new SqlCommand(
                $"INSERT INTO [dbo].[{table}] VALUES (1, N'north', @first), (2, N'north', @second), (3, N'north', @second)", conn))
            {
                insert.Parameters.AddWithValue("@first", first);
                insert.Parameters.AddWithValue("@second", second);
                await insert.ExecuteNonQueryAsync();
            }

            var pivotValues = new List<object?>();
            await using (var distinct = new SqlCommand($"SELECT DISTINCT [status] FROM [dbo].[{table}]", conn))
            await using (var reader = await distinct.ExecuteReaderAsync())
                while (await reader.ReadAsync())
                    pivotValues.Add(reader.GetString(0));
            pivotValues.Should().BeEquivalentTo(new[] { first, second });

            var config = PivotQueryConfig.Create("status", "id", "COUNT", new[] { "region" });
            var pivot = PivotSqlGenerator.GeneratePivot(SqlServerDialect.Instance, config, $"[dbo].[{table}]", pivotValues);

            await using var cmd = new SqlCommand(pivot.Sql, conn);
            foreach (var p in pivot.Parameters)
                cmd.Parameters.AddWithValue(p.Name, p.Value ?? DBNull.Value);
            await using var rows = await cmd.ExecuteReaderAsync();
            (await rows.ReadAsync()).Should().BeTrue();
            for (var position = 0; position < pivotValues.Count; position++)
                rows[PivotSqlGenerator.ColumnAlias(position)].Should().Be(
                    (string)pivotValues[position]! == first ? 1 : 2, $"cell {position} counts the rows of its own value");
        }
        finally
        {
            await ExecAsync(conn, $"DROP TABLE [dbo].[{table}]");
        }
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
