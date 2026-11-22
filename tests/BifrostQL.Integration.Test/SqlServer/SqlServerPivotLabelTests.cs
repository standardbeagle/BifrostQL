using BifrostQL.Core.QueryModel;
using BifrostQL.SqlServer;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BifrostQL.Integration.Test.SqlServer;

/// <summary>
/// The native T-SQL PIVOT matches <c>CAST(col AS NVARCHAR(MAX))</c> against labels
/// built in .NET. For a <c>bit</c> column the two texts differ ("1"/"0" vs
/// "True"/"False"), so every cell came back NULL. A non-string pivot column now takes
/// the portable CASE WHEN pivot, which binds each value as a parameter. The string
/// column is the paired control that keeps the native PIVOT. The GUID column passes
/// either way under the container's case-insensitive collation (CAST gives upper
/// case, .NET lower); it pins that the CASE WHEN route serves it too.
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
    /// The native PIVOT names each label as a bracketed identifier in its <c>IN (...)</c>
    /// list. A label longer than 128 characters is error 103, and an empty one renders
    /// <c>[]</c>, error 1038. Such labels take the CASE WHEN pivot, which binds them as
    /// parameters. "short" is the control column in every case.
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

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
