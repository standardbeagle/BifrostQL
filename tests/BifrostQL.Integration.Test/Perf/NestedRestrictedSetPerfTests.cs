using Xunit.Abstractions;

namespace BifrostQL.Integration.Test.Perf;

/// <summary>
/// Three-level read where the middle collection is paged per parent:
/// <c>customers(limit:100) { orders(limit:5) { lines } }</c> over customers that each own
/// many orders. The <c>lines</c> statement's restricted parent-id set decides how many
/// order ids it joins: every order of the 100 customers, or only the 500 returned.
/// </summary>
public sealed class NestedRestrictedSetPerfTests(ITestOutputHelper output)
{
    private const int Customers = 200;
    private const int OrdersPerCustomer = 2000;
    private const int LinesPerOrder = 2;

    private const string Query =
        "{ customers(limit: 100, sort: [id_asc]) { data { id orders(limit: 5, sort: [id_asc]) { data { id lines(limit: 10) { data { id qty } } } } } } }";

    [PerfFact]
    public Task Postgres() => MeasureAsync(PerfEngine.Postgres,
        $"""
        CREATE TABLE customers (id INT PRIMARY KEY, name TEXT NOT NULL);
        CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT NOT NULL REFERENCES customers(id));
        CREATE TABLE lines (id INT PRIMARY KEY, order_id INT NOT NULL REFERENCES orders(id), qty INT NOT NULL);
        CREATE INDEX ix_orders_customer ON orders(customer_id);
        CREATE INDEX ix_lines_order ON lines(order_id);
        INSERT INTO customers SELECT g, 'c' || g FROM generate_series(1, {Customers}) g;
        INSERT INTO orders SELECT g, 1 + (g - 1) / {OrdersPerCustomer} FROM generate_series(1, {Customers * OrdersPerCustomer}) g;
        INSERT INTO lines SELECT g, 1 + (g - 1) / {LinesPerOrder}, g % 7 FROM generate_series(1, {Customers * OrdersPerCustomer * LinesPerOrder}) g;
        ANALYZE;
        """);

    [PerfFact]
    public Task SqlServer() => MeasureAsync(PerfEngine.SqlServer,
        $"""
        CREATE TABLE customers (id INT PRIMARY KEY, name NVARCHAR(20) NOT NULL);
        CREATE TABLE orders (id INT PRIMARY KEY, customer_id INT NOT NULL REFERENCES customers(id));
        CREATE TABLE lines (id INT PRIMARY KEY, order_id INT NOT NULL REFERENCES orders(id), qty INT NOT NULL);
        CREATE INDEX ix_orders_customer ON orders(customer_id);
        CREATE INDEX ix_lines_order ON lines(order_id);
        WITH n AS (SELECT TOP ({Customers * OrdersPerCustomer * LinesPerOrder}) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS g
                   FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c)
        SELECT g INTO #n FROM n;
        INSERT INTO customers SELECT g, CONCAT('c', g) FROM #n WHERE g <= {Customers};
        INSERT INTO orders SELECT g, 1 + (g - 1) / {OrdersPerCustomer} FROM #n WHERE g <= {Customers * OrdersPerCustomer};
        INSERT INTO lines SELECT g, 1 + (g - 1) / {LinesPerOrder}, g % 7 FROM #n;
        UPDATE STATISTICS orders; UPDATE STATISTICS lines;
        """);

    private async Task MeasureAsync(PerfEngine engine, string ddl)
    {
        Skip.If(PerfDatabase.MasterFor(engine) is null, $"{engine} test connection string not set");
        await using var db = await PerfDatabase.CreateAsync(engine, "bifrost_perf_nested", ddl);
        await using var runner = await PerfGraphQlRunner.CreateAsync(db.Factory, Array.Empty<string>());

        var (median, p90, commands) = await runner.MeasureAsync(Query, warmup: 3, runs: 15);
        output.WriteLine($"{engine} customers(100)>orders(5)>lines: {commands} commands/request, median {median:F1} ms, p90 {p90:F1} ms");
    }
}
