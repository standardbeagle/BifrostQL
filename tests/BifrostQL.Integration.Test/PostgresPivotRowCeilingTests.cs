using BifrostQL.Integration.Test.Infrastructure;
using FluentAssertions;

namespace BifrostQL.Integration.Test;

/// <summary>
/// The pivot cross-tab fetched <c>max-query-rows + 1</c> rows for its truncation
/// sentinel. At a ceiling of <see cref="int.MaxValue"/> that sum overflowed to a negative
/// LIMIT, which PostgreSQL refuses. The pivot now takes its window from
/// <c>RowProbeWindow</c>, as every other capped surface does, so the sentinel fits under
/// the ceiling.
/// </summary>
public sealed class PostgresPivotRowCeilingTests : IAsyncLifetime
{
    private PostgresGraphQlHost? _host;

    public async Task InitializeAsync()
    {
        if (PostgresGraphQlHost.Master is null) return;
        _host = await PostgresGraphQlHost.StartAsync(
            "bifrost_pivot_row_ceiling",
            """
            CREATE TABLE orders (id INT PRIMARY KEY, status TEXT NOT NULL, amount INT NOT NULL);
            INSERT INTO orders (id, status, amount) VALUES (1, 'open', 10), (2, 'closed', 20), (3, 'open', 30);
            """,
            new[] { $":root {{ max-query-rows: {int.MaxValue} }}" });
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [SkippableFact]
    public async Task Pivot_UnderAnIntMaxValueCeiling_ReturnsEveryRowUntruncated()
    {
        Skip.If(_host is null, PostgresGraphQlHost.SkipReason);

        using var reply = await _host!.PostAsync(
            "{ ordersPivot(rowKeys: [id], pivotColumn: status, valueColumn: amount, aggregate: sum) }");

        PostgresGraphQlHost.AssertNoErrors(reply);
        var pivot = reply.RootElement.GetProperty("data").GetProperty("ordersPivot");
        pivot.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetInt32())
            .Should().Equal(1, 2, 3);
        pivot.GetProperty("truncated").GetBoolean().Should().BeFalse();
    }
}
