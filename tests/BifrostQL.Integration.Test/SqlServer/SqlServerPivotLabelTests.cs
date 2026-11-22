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

    /// <summary>
    /// SQL Server compares identifiers with trailing spaces ignored, so two labels that
    /// differ only by them name one PIVOT output column twice (error 8156) and the whole
    /// pivot failed. Discovery's DISTINCT folds such values together, but a NULL takes the
    /// null label <c>_null_</c> after discovery, so a literal <c>"_null_ "</c> beside a NULL
    /// still yields the pair. A whitespace-only label is a valid identifier (<c>[   ]</c>
    /// runs), and is the paired control that keeps the native PIVOT.
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
    /// The native PIVOT is kept for labels that are valid, distinct identifiers: the
    /// trailing-space route must not send every pivot to CASE WHEN.
    /// </summary>
    [Fact]
    public void Distinct_labels_keep_the_native_pivot_and_trailing_space_twins_do_not()
    {
        var config = PivotQueryConfig.Create("status", "id", "COUNT", new[] { "region" });

        SqlServerDialect.Instance.BuildNativePivot(config, "[dbo].[t]", new object?[] { "a", "   ", null })
            .Should().NotBeNull();
        SqlServerDialect.Instance.BuildNativePivot(config, "[dbo].[t]", new object?[] { "a", "a  " })
            .Should().BeNull();
        SqlServerDialect.Instance.BuildNativePivot(config, "[dbo].[t]", new object?[] { null, "_null_ " })
            .Should().BeNull();
    }

    /// <summary>
    /// PIVOT output columns are identifiers, compared under the database collation. In an
    /// accent-insensitive database, <c>cafe</c> and <c>café</c> from an accent-sensitive
    /// column survive discovery's DISTINCT as two values but name one output column twice
    /// (error 8156), so the whole pivot failed. Such labels take the CASE WHEN pivot.
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
    /// Labels equal under case, accent, kana or width folding may name one identifier under
    /// some collation, so each pair takes the CASE WHEN pivot. Over-routing is harmless.
    /// </summary>
    [Theory]
    [InlineData("cafe", "café")]
    [InlineData("ｶﾀ", "カタ")]
    [InlineData("かた", "カタ")]
    [InlineData("A", "Ａ")]
    [InlineData("cafe\u0301", "café")]
    [InlineData("co\u00ADop", "coop")]
    public void Labels_equal_under_collation_folding_leave_the_native_pivot(string first, string second)
    {
        var config = PivotQueryConfig.Create("status", "id", "COUNT", new[] { "region" });

        SqlServerDialect.Instance.BuildNativePivot(config, "[dbo].[t]", new object?[] { first, second })
            .Should().BeNull();
    }

    /// <summary>
    /// Controls for the folding theory: distinct printable-ASCII labels, and a lone non-ASCII
    /// label (nothing to collide with), keep the native PIVOT.
    /// </summary>
    [Theory]
    [InlineData("cafe", "cafes")]
    [InlineData("café")]
    public void Labels_that_cannot_collide_keep_the_native_pivot(params string[] labels)
    {
        var config = PivotQueryConfig.Create("status", "id", "COUNT", new[] { "region" });

        SqlServerDialect.Instance.BuildNativePivot(config, "[dbo].[t]", labels.Cast<object?>().ToArray())
            .Should().NotBeNull();
    }

    private const string InvariantChildVariable = "BIFROST_TEST_INVARIANT_GLOBALIZATION_CHILD";

    /// <summary>
    /// Under InvariantGlobalization (DOTNET_SYSTEM_GLOBALIZATION_INVARIANT) the culture
    /// comparer ignores IgnoreNonSpace, IgnoreKanaType and IgnoreWidth, and String.Normalize
    /// leaves non-ASCII text unchanged, so a fold built on either let cafe/café reach native
    /// PIVOT and fail with error 8156. This runs the folding theory in a child test host with
    /// that switch set, plus a probe proving the child really lacks culture data.
    /// </summary>
    [SkippableFact]
    public async Task Labels_equal_under_collation_folding_leave_the_native_pivot_under_invariant_globalization()
    {
        // The child must never spawn another child.
        Skip.If(Environment.GetEnvironmentVariable(InvariantChildVariable) == "1", "already the invariant-globalization child");

        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("test");
        psi.ArgumentList.Add(typeof(SqlServerPivotLabelTests).Assembly.Location);
        psi.ArgumentList.Add("--filter");
        psi.ArgumentList.Add(
            $"FullyQualifiedName={typeof(SqlServerPivotLabelTests).FullName}.{nameof(Labels_equal_under_collation_folding_leave_the_native_pivot)}" +
            $"|FullyQualifiedName={typeof(SqlServerPivotLabelTests).FullName}.{nameof(Invariant_child_lacks_culture_folding)}");
        psi.Environment["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1";
        psi.Environment[InvariantChildVariable] = "1";

        using var child = System.Diagnostics.Process.Start(psi)!;
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
        var output = await stdout + await stderr;

        child.ExitCode.Should().Be(0, output);
        output.Should().MatchRegex(@"Failed:\s+0, Passed:\s+7\b", output);
    }

    [SkippableFact]
    public void Invariant_child_lacks_culture_folding()
    {
        Skip.If(Environment.GetEnvironmentVariable(InvariantChildVariable) != "1", "runs only in the invariant-globalization child");

        string.Compare("cafe", "café", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.CompareOptions.IgnoreNonSpace)
            .Should().NotBe(0, "the child must run without culture data, or it proves nothing");
    }

    private static async Task ExecAsync(SqlConnection conn, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
