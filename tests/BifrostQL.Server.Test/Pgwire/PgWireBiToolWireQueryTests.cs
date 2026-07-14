using System.Security.Claims;
using BifrostQL.Server.Auth;
using FluentAssertions;
using Xunit;

namespace BifrostQL.Server.Test.Pgwire
{
    /// <summary>
    /// Reproduces the wire query SEQUENCES a PostgreSQL BI client issues on connect + "chart a
    /// table" — schema/table/column introspection followed by a data SELECT — and drives them
    /// through the ACTUAL pgwire handler against a real transformer-pipeline executor, proving
    /// our catalog emulation (slice 4) and read path answer what those tools send AND that the
    /// data path stays tenant-scoped even for a BI tool.
    ///
    /// <para><b>Honesty about scope.</b> Real Grafana and Metabase are NOT started here — this
    /// is an in-process reproduction of the query shapes their postgres drivers issue, so the
    /// automated gate stays green with no external services. Each query below is labeled
    /// <b>verified-real</b> (a query string taken from a tool/driver whose behavior is known and
    /// documented — psql's <c>\dt</c>, <c>version()</c>) or <b>representative</b> (a standard
    /// libpq/JDBC information_schema/pg_catalog introspection whose exact per-tool string this
    /// repo cannot verify from source, but whose shape Grafana/Metabase sync and query editors
    /// issue). The end-to-end manual smoke against real Grafana + Metabase is the runbook at
    /// <c>docs/src/content/docs/guides/pgwire-bi-smoke.md</c>; it is deliberately NOT wired into
    /// <c>dotnet test</c>.</para>
    /// </summary>
    public sealed class PgWireBiToolWireQueryTests : IAsyncLifetime
    {
        private PgWireRealDbHarness _harness = null!;

        private static readonly string[] MetadataRules =
        {
            "*.sensors { tenant-filter: tenant_id }",
        };

        private static readonly string[] SeedSql =
        {
            "DROP TABLE IF EXISTS sensors",
            """
            CREATE TABLE sensors (
                id INTEGER PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                label TEXT NOT NULL,
                reading INTEGER NOT NULL
            )
            """,
            """
            INSERT INTO sensors(id, tenant_id, label, reading) VALUES
                (1, 'tenant-a', 'a-temp', 21),
                (2, 'tenant-a', 'a-humidity', 45),
                (3, 'tenant-b', 'b-temp', 19)
            """,
        };

        // VERIFIED-REAL: the exact query psql (v16) issues for the \dt meta-command — a
        // pg_class ⋈ pg_namespace join with CASE-mapped relkind, pg_get_userbyid(relowner),
        // the relkind IN filter, system-namespace exclusions, pg_table_is_visible(oid), and
        // positional ORDER BY 1,2. Taken verbatim from psql source (same string slice 4 pins).
        private const string PsqlDtQuery =
            "SELECT n.nspname as \"Schema\",\n" +
            "  c.relname as \"Name\",\n" +
            "  CASE c.relkind WHEN 'r' THEN 'table' WHEN 'v' THEN 'view' WHEN 'm' THEN 'materialized view' WHEN 'S' THEN 'sequence' WHEN 'f' THEN 'foreign table' WHEN 'p' THEN 'partitioned table' END as \"Type\",\n" +
            "  pg_catalog.pg_get_userbyid(c.relowner) as \"Owner\"\n" +
            "FROM pg_catalog.pg_class c\n" +
            "     LEFT JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace\n" +
            "WHERE c.relkind IN ('r','p','')\n" +
            "      AND n.nspname <> 'pg_catalog'\n" +
            "      AND n.nspname !~ '^pg_toast'\n" +
            "      AND n.nspname <> 'information_schema'\n" +
            "  AND pg_catalog.pg_table_is_visible(c.oid)\n" +
            "ORDER BY 1,2;";

        public async Task InitializeAsync()
            => _harness = await PgWireRealDbHarness.StartAsync(nameof(PgWireBiToolWireQueryTests), MetadataRules, SeedSql);

        public async Task DisposeAsync() => await _harness.DisposeAsync();

        [Fact]
        public async Task Psql_ConnectSequence_ListsRelationsAndReportsVersion()
        {
            var identity = TenantPrincipal("user-a", "tenant-a");

            // VERIFIED-REAL: psql \dt relation list.
            var dt = await _harness.QueryAsync(identity, PsqlDtQuery);
            dt.HasError.Should().BeFalse();
            dt.Fields.Select(f => f.Name).Should().Equal("Schema", "Name", "Type", "Owner");
            dt.Rows.Select(r => r[1]).Should().Contain("sensors");
            dt.Rows.First(r => r[1] == "sensors")[2].Should().Be("table"); // relkind 'r' → table

            // VERIFIED-REAL: psql/libpq version probe.
            var version = await _harness.QueryAsync(identity, "SELECT version()");
            version.HasError.Should().BeFalse();
            version.Rows.Should().ContainSingle().Which[0].Should().Contain("BifrostQL");
        }

        [Fact]
        public async Task Grafana_PostgresDatasource_DiscoversSchemaThenChartsTenantScopedData()
        {
            var identity = TenantPrincipal("user-a", "tenant-a");

            // REPRESENTATIVE: Grafana's postgres query-editor populates its table dropdown from
            // information_schema.tables (exact per-version string unverified from this repo).
            var tables = await _harness.QueryAsync(identity, "SELECT table_name FROM information_schema.tables");
            tables.HasError.Should().BeFalse();
            tables.Rows.Select(r => r[0]).Should().Contain("sensors");

            // REPRESENTATIVE: column dropdown from information_schema.columns for the chosen table.
            var columns = await _harness.QueryAsync(identity,
                "SELECT column_name, data_type FROM information_schema.columns WHERE table_name = 'sensors'");
            columns.HasError.Should().BeFalse();
            columns.Rows.Select(r => r[0]).Should().Contain(new[] { "id", "label", "reading" });

            // CHART A TABLE (data SELECT): the load-bearing property — even a BI tool's data
            // query is tenant-filtered on the wire. tenant-a sees only its own sensors.
            var chart = await _harness.QueryAsync(identity, "SELECT id, label, reading FROM sensors");
            chart.HasError.Should().BeFalse();
            chart.Rows.Select(r => r[1]).Should().BeEquivalentTo(new[] { "a-temp", "a-humidity" });
            chart.Rows.Select(r => r[1]).Should().NotContain("b-temp");
        }

        [Fact]
        public async Task Metabase_JdbcSync_IntrospectsThenChartsTenantScopedData()
        {
            var identity = TenantPrincipal("user-b", "tenant-b");

            // REPRESENTATIVE: Metabase's JDBC sync enumerates relations via pg_catalog.pg_class
            // (exact driver string unverified from this repo).
            var relations = await _harness.QueryAsync(identity, "SELECT relname, relkind FROM pg_catalog.pg_class");
            relations.HasError.Should().BeFalse();
            var sensors = relations.Rows.First(r => r[0] == "sensors");
            sensors[1].Should().Be("r"); // ordinary table

            // REPRESENTATIVE: column sync via information_schema.columns.
            var columns = await _harness.QueryAsync(identity,
                "SELECT column_name FROM information_schema.columns WHERE table_name = 'sensors'");
            columns.HasError.Should().BeFalse();
            columns.Rows.Select(r => r[0]).Should().Contain(new[] { "id", "label", "reading" });

            // CHART A TABLE (data SELECT): tenant-b sees only its own sensor over the wire.
            var chart = await _harness.QueryAsync(identity, "SELECT id, label, reading FROM sensors");
            chart.HasError.Should().BeFalse();
            chart.Rows.Should().ContainSingle().Which[1].Should().Be("b-temp");
        }

        private static ClaimsPrincipal TenantPrincipal(string userId, string tenantId) =>
            new(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(LocalAuthClaims.Tenant, tenantId),
            }, authenticationType: "pgwire"));
    }
}
