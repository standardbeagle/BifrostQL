using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// PostgreSQL truncates identifiers to 63 bytes (NAMEDATALEN - 1). The pivot used each
/// pivot value as its result-set column alias, so a label over 63 bytes came back under a
/// truncated name: its cell read as null, and two labels sharing a 63-byte prefix produced
/// two identically named columns. The cross-tab now aliases its columns positionally and
/// maps each position back to its label in the payload.
/// </summary>
public sealed class PostgresPivotLongLabelTests : IAsyncLifetime
{
    // Two labels of 67 bytes that agree on their first 63.
    private static readonly string SharedPrefix = new('p', 63);
    private static readonly string LabelOne = SharedPrefix + "_one";
    private static readonly string LabelTwo = SharedPrefix + "_two";

    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_pivot_long_label",
            $"""
            CREATE TABLE orders (id INT PRIMARY KEY, region TEXT NOT NULL, status TEXT NOT NULL, amount INT NOT NULL);
            INSERT INTO orders (id, region, status, amount) VALUES
                (1, 'east', '{LabelOne}', 10),
                (2, 'east', '{LabelTwo}', 20),
                (3, 'east', 'short', 30);
            """,
            Array.Empty<string>());
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Pivot_LabelsLongerThan63Bytes_KeepTheirOwnCells()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        using var reply = await _host!.PostAsync(
            "{ ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: amount, aggregate: sum) }");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var pivot = reply.RootElement.GetProperty("data").GetProperty("ordersPivot");
        pivot.GetProperty("columns").EnumerateArray().Select(c => c.GetString())
            .Should().BeEquivalentTo(new[] { LabelOne, LabelTwo, "short" });

        var cells = pivot.GetProperty("rows")[0].GetProperty("cells");
        cells.GetProperty(LabelOne).GetInt32().Should().Be(10);
        cells.GetProperty(LabelTwo).GetInt32().Should().Be(20);
        cells.GetProperty("short").GetInt32().Should().Be(30);
    }

    [SkippableFact]
    public async Task Pivot_OneLabelLongerThan63Bytes_IsNotASilentNullCell()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        // No collision here: only one long label is in scope, so a truncated alias shows
        // up purely as the cell reading null.
        using var reply = await _host!.PostAsync(
            $$"""{ ordersPivot(rowKeys: [region], pivotColumn: status, valueColumn: amount, aggregate: sum, filter: { status: { _neq: "{{LabelTwo}}" } }) }""");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var cells = reply.RootElement.GetProperty("data").GetProperty("ordersPivot")
            .GetProperty("rows")[0].GetProperty("cells");
        cells.GetProperty(LabelOne).GetInt32().Should().Be(10);
        cells.GetProperty("short").GetInt32().Should().Be(30);
    }
}
