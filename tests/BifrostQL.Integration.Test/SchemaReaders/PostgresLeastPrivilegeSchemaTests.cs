using System.Text.Json;
using BifrostQL.Core.Model;
using BifrostQL.Core.Modules;
using BifrostQL.Core.Resolvers;
using BifrostQL.Core.Schema;
using BifrostQL.Model;
using BifrostQL.Ngsql;
using FluentAssertions;
using GraphQL;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace BifrostQL.Integration.Test.SchemaReaders;

/// <summary>
/// The schema read on a real PostgreSQL under two logins: the role that owns the tables, and an
/// application login holding only SELECT and DML. information_schema's constraint views filter
/// on table ownership, so reading keys through them gave the application login a model with no
/// primary keys: an update bound no key and either changed 0 rows silently or failed with
/// "has no primary key columns". Only runs when BIFROST_TEST_POSTGRES is set; the connection
/// must be able to create roles and databases.
/// </summary>
[Collection("PostgresLeastPrivilegeSchema")]
public sealed class PostgresLeastPrivilegeSchemaTests : IAsyncLifetime
{
    private const string Password = "least_privilege_test";

    /// <summary>
    /// The pre-pg_constraint constraint and foreign-key queries, kept as the oracle for what the
    /// owning login must keep seeing. It must never be used to judge a non-owning login. Rows are
    /// compared with duplicates removed: the views join on the constraint name alone, so two
    /// tables declaring one name multiply each other's rows.
    /// </summary>
    private const string InformationSchemaConstraints = """
        SELECT DISTINCT ccu.table_schema, ccu.table_name, ccu.column_name, ccu.constraint_schema,
                        ccu.constraint_name, tc.constraint_type
        FROM information_schema.constraint_column_usage ccu
        INNER JOIN information_schema.table_constraints tc ON
            tc.constraint_catalog = ccu.constraint_catalog AND
            tc.constraint_schema = ccu.constraint_schema AND
            tc.constraint_name = ccu.constraint_name
        WHERE ccu.table_schema NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
        UNION
        SELECT schemaname, tablename, a.attname, schemaname, indexname, 'UNIQUE'
        FROM pg_indexes pi
        JOIN pg_class t ON t.relname = pi.tablename AND t.relnamespace = (SELECT oid FROM pg_namespace WHERE nspname = pi.schemaname)
        JOIN pg_index ix ON ix.indexrelid = to_regclass(pi.schemaname || '.' || pi.indexname)
        JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = ANY(ix.indkey)
        WHERE ix.indisunique = true
          AND ix.indisprimary = false
          AND to_regclass(pi.schemaname || '.' || pi.indexname) IS NOT NULL
          AND schemaname NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
        """;

    private const string InformationSchemaForeignKeys = """
        SELECT kcu.constraint_name, kcu.table_schema, kcu.table_name, kcu.column_name,
               ccu.table_schema, ccu.table_name, ccu.column_name
        FROM information_schema.table_constraints tc
        INNER JOIN information_schema.key_column_usage kcu ON
            tc.constraint_catalog = kcu.constraint_catalog AND
            tc.constraint_schema  = kcu.constraint_schema  AND
            tc.constraint_name    = kcu.constraint_name
        INNER JOIN information_schema.referential_constraints rc ON
            tc.constraint_catalog = rc.constraint_catalog AND
            tc.constraint_schema  = rc.constraint_schema  AND
            tc.constraint_name    = rc.constraint_name
        INNER JOIN information_schema.key_column_usage ccu ON
            rc.unique_constraint_catalog = ccu.constraint_catalog AND
            rc.unique_constraint_schema  = ccu.constraint_schema  AND
            rc.unique_constraint_name    = ccu.constraint_name    AND
            kcu.position_in_unique_constraint = ccu.ordinal_position
        WHERE tc.constraint_type = 'FOREIGN KEY'
          AND tc.table_schema NOT IN ('pg_catalog', 'information_schema', 'ag_catalog')
        ORDER BY 2, 3, 1, kcu.ordinal_position
        """;

    private static readonly string[] Schema =
    {
        // A foreign-key chain: time_entries -> people -> organizations.
        "CREATE TABLE organizations (id INTEGER GENERATED ALWAYS AS IDENTITY PRIMARY KEY, name TEXT NOT NULL UNIQUE)",
        """
        CREATE TABLE people (
            id SERIAL PRIMARY KEY,
            org_id INTEGER NOT NULL REFERENCES organizations(id),
            email TEXT NOT NULL CONSTRAINT people_email_unique UNIQUE,
            secret_id INTEGER,
            CONSTRAINT people_email_present CHECK (length(email) > 0)
        )
        """,
        """
        CREATE TABLE time_entries (
            id SERIAL PRIMARY KEY,
            person_id INTEGER NOT NULL CONSTRAINT fk_person REFERENCES people(id),
            note TEXT,
            stopped_at TIMESTAMPTZ,
            total INTEGER GENERATED ALWAYS AS (0) STORED
        )
        """,
        "CREATE UNIQUE INDEX one_running_entry ON time_entries (person_id) WHERE stopped_at IS NULL",
        // A key declared in another order than its columns, and a foreign key onto it declared
        // in yet another: pairs are positional, so a read in column order pairs the wrong ones.
        """
        CREATE TABLE week_cells (
            person_id INTEGER NOT NULL,
            week INTEGER NOT NULL,
            day INTEGER NOT NULL,
            hours INTEGER,
            CONSTRAINT week_cells_pk PRIMARY KEY (week, person_id, day)
        )
        """,
        """
        CREATE TABLE cell_notes (
            id SERIAL PRIMARY KEY,
            c_day INTEGER NOT NULL,
            c_person INTEGER NOT NULL,
            c_week INTEGER NOT NULL,
            CONSTRAINT cell_notes_cell FOREIGN KEY (c_day, c_person, c_week) REFERENCES week_cells (day, person_id, week)
        )
        """,
        // A foreign-key name shared by two tables, which is legal: names are per table.
        "CREATE TABLE timer_marks (id SERIAL PRIMARY KEY, person_id INTEGER NOT NULL CONSTRAINT fk_person REFERENCES people(id))",
        // Names that need quoting.
        """CREATE TABLE "Mixed Case" ("Some Key" INTEGER PRIMARY KEY, "Other Col" TEXT CONSTRAINT "Other Unique" UNIQUE)""",
        // A partitioned table with a key, and foreign keys into and out of it.
        "CREATE TABLE ledger (id INTEGER NOT NULL, booked DATE NOT NULL, person_id INTEGER REFERENCES people(id), PRIMARY KEY (id, booked)) PARTITION BY RANGE (booked)",
        "CREATE TABLE ledger_2026 PARTITION OF ledger FOR VALUES FROM ('2026-01-01') TO ('2027-01-01')",
        "CREATE TABLE ledger_refs (id SERIAL PRIMARY KEY, ledger_id INTEGER, ledger_booked DATE, FOREIGN KEY (ledger_id, ledger_booked) REFERENCES ledger (id, booked))",
        // Foreign key onto a plain unique index: information_schema has no constraint to name
        // as its target, so it does not report one. The reader keeps that behaviour.
        "CREATE TABLE codes (id SERIAL PRIMARY KEY, code TEXT NOT NULL)",
        "CREATE UNIQUE INDEX codes_code_index ON codes (code)",
        "CREATE TABLE code_uses (id SERIAL PRIMARY KEY, code TEXT REFERENCES codes (code))",
        // Kinds information_schema leaves out: exclusion constraints, and a domain's check.
        "CREATE DOMAIN positive_hours AS INTEGER CHECK (VALUE > 0)",
        "CREATE TABLE booked_rooms (id SERIAL PRIMARY KEY, room INTEGER, hours positive_hours, CONSTRAINT one_room EXCLUDE USING btree (room WITH =))",
        // A second schema, whose key the first schema's table references.
        "CREATE SCHEMA audit",
        "CREATE TABLE audit.reviews (id SERIAL PRIMARY KEY, entry_id INTEGER REFERENCES public.time_entries(id))",
        // Reachable but read-only for the application login.
        "CREATE TABLE statuses (id SERIAL PRIMARY KEY, org_id INTEGER REFERENCES organizations(id))",
        // Never granted to the application login, and referenced by a table that is.
        "CREATE TABLE secrets (id SERIAL PRIMARY KEY, body TEXT)",
        "ALTER TABLE people ADD CONSTRAINT people_secret FOREIGN KEY (secret_id) REFERENCES secrets(id)",
        // Granted column by column, leaving out half of the composite key.
        "CREATE TABLE partial_grants (a INTEGER NOT NULL, b INTEGER NOT NULL, v TEXT, PRIMARY KEY (a, b))",
    };

    private static readonly string[] Grants =
    {
        "GRANT USAGE ON SCHEMA public TO {app}",
        "GRANT USAGE ON SCHEMA audit TO {app}",
        """
        GRANT SELECT, INSERT, UPDATE, DELETE ON
            organizations, people, time_entries, week_cells, cell_notes, timer_marks, "Mixed Case",
            ledger, ledger_2026, ledger_refs, codes, code_uses, booked_rooms, audit.reviews
        TO {app}
        """,
        "GRANT SELECT ON statuses TO {app}",
        "GRANT SELECT (a, v), UPDATE (v) ON partial_grants TO {app}",
        "GRANT USAGE ON ALL SEQUENCES IN SCHEMA public, audit TO {app}",
    };

    private string? _master;
    private string _dbName = null!;
    private string _owner = null!;
    private string _app = null!;
    private string _ownerConn = null!;
    private string _appConn = null!;

    private bool Available => _master is not null;

    public async Task InitializeAsync()
    {
        _master = Environment.GetEnvironmentVariable("BIFROST_TEST_POSTGRES");
        if (_master is null) return;

        var suffix = Guid.NewGuid().ToString("N");
        _dbName = $"bifrost_lp_{suffix}";
        _owner = $"bifrost_lp_owner_{suffix}";
        _app = $"bifrost_lp_app_{suffix}";

        await ExecAsync(_master, $"CREATE ROLE {_owner} LOGIN PASSWORD '{Password}'");
        await ExecAsync(_master, $"CREATE ROLE {_app} LOGIN PASSWORD '{Password}'");
        await ExecAsync(_master, $"CREATE DATABASE {_dbName} OWNER {_owner}");
        _ownerConn = ConnectionFor(_owner);
        _appConn = ConnectionFor(_app);

        foreach (var statement in Schema)
            await ExecAsync(_ownerConn, statement);
        foreach (var statement in Grants)
            await ExecAsync(_ownerConn, statement.Replace("{app}", _app));
        await ExecAsync(_ownerConn, "INSERT INTO organizations (name) VALUES ('acme')");
        await ExecAsync(_ownerConn, "INSERT INTO people (org_id, email) VALUES (1, 'a@acme.test')");
        await ExecAsync(_ownerConn, "INSERT INTO time_entries (person_id, note) VALUES (1, 'first')");
    }

    public async Task DisposeAsync()
    {
        if (_master is null) return;
        NpgsqlConnection.ClearAllPools();
        await ExecAsync(_master, $"DROP DATABASE IF EXISTS {_dbName} WITH (FORCE)");
        await ExecAsync(_master, $"DROP ROLE IF EXISTS {_owner}");
        await ExecAsync(_master, $"DROP ROLE IF EXISTS {_app}");
    }

    [SkippableFact]
    public async Task AppLogin_SeesThePrimaryKeysItsTablesDeclare()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var app = await ReadSchemaAsync(_appConn);

        KeyColumns(app, "public", "people").Should().Equal("id");
        KeyColumns(app, "public", "time_entries").Should().Equal("id");
        KeyColumns(app, "audit", "reviews").Should().Equal("id");
        KeyColumns(app, "public", "statuses").Should().Equal(new[] { "id" }, "a SELECT grant reaches the table, so its key is known");
        KeyColumns(app, "public", "Mixed Case").Should().Equal("Some Key");
        KeyColumns(app, "public", "week_cells").Should().BeEquivalentTo("week", "person_id", "day");
    }

    [SkippableFact]
    public async Task AppLogin_ListsACompositeKeyInDeclaredOrder()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var app = await ReadSchemaAsync(_appConn);

        app.ColumnConstraints.Keys
            .Where(column => column.Table == "week_cells")
            .Select(column => column.Column)
            .Should().Equal(new[] { "week", "person_id", "day" }, "the listing follows conkey, not the table's column order");
    }

    [SkippableFact]
    public async Task AppLogin_SeesForeignKeys_WithColumnsPairedByPosition()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var app = await ReadSchemaAsync(_appConn);

        var cell = app.ForeignKeys.Should().ContainSingle(fk => fk.ConstraintName == "cell_notes_cell").Subject;
        cell.ChildTableName.Should().Be("cell_notes");
        cell.ChildColumnNames.Should().Equal("c_day", "c_person", "c_week");
        cell.ParentTableName.Should().Be("week_cells");
        cell.ParentColumnNames.Should().Equal("day", "person_id", "week");

        app.ForeignKeys.Should().Contain(fk => fk.ChildTableName == "people" && fk.ParentTableName == "organizations");
        app.ForeignKeys.Should().Contain(fk => fk.ChildTableName == "time_entries" && fk.ParentTableName == "people");
        app.ForeignKeys.Should().Contain(fk => fk.ChildTableSchema == "audit" && fk.ParentTableSchema == "public");
        app.ForeignKeys.Should().Contain(fk => fk.ChildTableName == "statuses",
            "a read-only table's foreign keys still shape its query surface");
    }

    [SkippableFact]
    public async Task AppLogin_SeesNoConstraintOnATableItWasNeverGranted()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var app = await ReadSchemaAsync(_appConn);

        app.ForeignKeys.Should().NotContain(fk => fk.ParentTableName == "secrets" || fk.ChildTableName == "secrets",
            "a link to a table outside the model must not be offered");
        app.ColumnConstraints.Keys.Should().NotContain(column => column.Table == "secrets" && !IsIndexConstraint(app, column));
    }

    [SkippableFact]
    public async Task AppLogin_DoesNotTreatTheGrantedHalfOfACompositeKeyAsTheKey()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var app = await ReadSchemaAsync(_appConn);

        KeyColumns(app, "public", "partial_grants").Should().BeEmpty(
            "column b is hidden, and a WHERE on a alone would rewrite every row sharing it");
    }

    [SkippableFact]
    public async Task AppLogin_ReadsTheSameConstraintsAndColumnsAsTheOwner_OnFullyGrantedTables()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var owner = await ReadSchemaAsync(_ownerConn);
        var app = await ReadSchemaAsync(_appConn);
        string[] granted = { "organizations", "people", "time_entries", "week_cells", "cell_notes", "ledger", "codes" };

        foreach (var table in granted)
        {
            ColumnFacts(app, table).Should().Equal(ColumnFacts(owner, table), $"the owner and the app login read {table} alike");
            ConstraintFacts(app, table).Should().BeEquivalentTo(ConstraintFacts(owner, table).Where(fact => !fact.Contains("people_secret")));
            app.Indexes.Where(index => index.TableName == table).Select(index => index.Name)
                .Should().BeEquivalentTo(owner.Indexes.Where(index => index.TableName == table).Select(index => index.Name));
        }
    }

    [SkippableFact]
    public async Task OwnerLogin_ReadsExactlyWhatInformationSchemaReports()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var read = await ReadSchemaAsync(_ownerConn);

        var expectedConstraints = await QueryRowsAsync(_ownerConn, InformationSchemaConstraints);
        var actualConstraints = read.ColumnConstraints.Values.SelectMany(rows => rows)
            .Select(c => string.Join("|", c.TableSchema, c.TableName, c.ColumnName, c.ConstraintSchema, c.ConstraintName, c.ConstraintType))
            .Distinct();
        actualConstraints.Should().BeEquivalentTo(expectedConstraints);
        expectedConstraints.Should().Contain(row => row.Contains("|PRIMARY KEY"), "the oracle must be reading something");

        var expectedForeignKeys = (await QueryRowsAsync(_ownerConn, InformationSchemaForeignKeys))
            .Select(row => row.Split('|'))
            .GroupBy(row => (Schema: row[1], Table: row[2], Name: row[0]))
            .Select(fk =>
            {
                var pairs = fk.Select(row => (Child: row[3], Parent: row[6])).Distinct().ToList();
                return string.Join("|", fk.Key.Schema, fk.Key.Table, fk.Key.Name, string.Join(",", pairs.Select(pair => pair.Child)),
                    fk.First()[4], fk.First()[5], string.Join(",", pairs.Select(pair => pair.Parent)));
            });
        var actualForeignKeys = read.ForeignKeys.Select(fk => string.Join("|",
            fk.ChildTableSchema, fk.ChildTableName, fk.ConstraintName, string.Join(",", fk.ChildColumnNames),
            fk.ParentTableSchema, fk.ParentTableName, string.Join(",", fk.ParentColumnNames)));
        actualForeignKeys.Should().Equal(expectedForeignKeys, "same foreign keys, same order, same column pairing");
    }

    [SkippableFact]
    public async Task ConstraintNameSharedAcrossTables_DoesNotLabelAnotherTablesColumnAsAKey()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        // Constraint names are per table. The information_schema join matched on the name alone,
        // so a check named like another table's primary key made its column read as a key.
        await ExecAsync(_ownerConn, "CREATE TABLE shared_a (id INTEGER CONSTRAINT shared_name PRIMARY KEY)");
        await ExecAsync(_ownerConn, "CREATE TABLE shared_b (id INTEGER PRIMARY KEY, n INTEGER CONSTRAINT shared_name CHECK (n > 0))");

        var read = await ReadSchemaAsync(_ownerConn);

        KeyColumns(read, "public", "shared_b").Should().Equal("id");
    }

    [SkippableFact]
    public async Task AppLogin_UpdatesARow_ThroughTheLoadedModel()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var result = await ExecuteAsync("mutation { time_entries(update: { id: 1, note: \"edited\" }) }");

        AssertNoErrors(result);
        (await ScalarAsync("SELECT note FROM time_entries WHERE id = 1")).Should().Be("edited");
    }

    [SkippableFact]
    public async Task AppLogin_UpsertsBothWays_ThroughTheLoadedModel()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var result = await ExecuteAsync("""
            mutation {
              time_entries_batch(actions: [
                { upsert: { id: 1, person_id: 1, note: "upserted" } },
                { upsert: { id: 50, person_id: 1, note: "fresh", stopped_at: "2026-11-09T00:00:00Z" } }
              ])
            }
            """);

        AssertNoErrors(result);
        (await ScalarAsync("SELECT note FROM time_entries WHERE id = 1")).Should().Be("upserted");
        (await ScalarAsync("SELECT note FROM time_entries WHERE id = 50")).Should().Be("fresh");
    }

    [SkippableFact]
    public async Task AppLogin_WalksTheForeignKeyChain_ThroughTheLoadedModel()
    {
        Skip.If(!Available, "BIFROST_TEST_POSTGRES environment variable not set");

        var result = await ExecuteAsync("{ time_entries { data { note people { email organizations { name } } } } }");

        AssertNoErrors(result);
        using var json = JsonDocument.Parse(new GraphQL.SystemTextJson.GraphQLSerializer().Serialize(result));
        var entry = json.RootElement.GetProperty("data").GetProperty("time_entries").GetProperty("data")[0];
        entry.GetProperty("people").GetProperty("email").GetString().Should().Be("a@acme.test");
        entry.GetProperty("people").GetProperty("organizations").GetProperty("name").GetString().Should().Be("acme");
    }

    // ---- harness ----

    private string ConnectionFor(string role) => new NpgsqlConnectionStringBuilder(_master)
    {
        Database = _dbName,
        Username = role,
        Password = Password,
    }.ConnectionString;

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object?> ScalarAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_ownerConn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<List<string>> QueryRowsAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i))));
        return rows;
    }

    private static async Task<SchemaData> ReadSchemaAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return await new PostgresSchemaReader().ReadSchemaAsync(conn);
    }

    private static string[] KeyColumns(SchemaData read, string schema, string table)
        => read.RawColumns
            .Where(column => column.TableSchema == schema && column.TableName == table && column.IsPrimaryKey)
            .Select(column => column.ColumnName)
            .ToArray();

    private static bool IsIndexConstraint(SchemaData read, ColumnRef column)
        => read.ColumnConstraints[column].All(constraint => constraint.ConstraintType == "UNIQUE");

    private static string[] ColumnFacts(SchemaData read, string table)
        => read.RawColumns.Where(column => column.TableSchema == "public" && column.TableName == table)
            .OrderBy(column => column.OrdinalPosition)
            .Select(column => string.Join("|", column.ColumnName, column.DataType, column.IsNullable, column.IsIdentity,
                column.IsComputed, column.HasDefault, column.IsPrimaryKey, column.IsUnique))
            .ToArray();

    private static IEnumerable<string> ConstraintFacts(SchemaData read, string table)
        => read.ColumnConstraints.Where(entry => entry.Key.Schema == "public" && entry.Key.Table == table)
            .SelectMany(entry => entry.Value)
            .Select(c => string.Join("|", c.ColumnName, c.ConstraintName, c.ConstraintType))
            .Distinct();

    private static void AssertNoErrors(ExecutionResult result)
        => result.Errors.Should().BeNullOrEmpty(string.Join("; ", result.Errors?.Select(e => e.Message) ?? Array.Empty<string>()));

    /// <summary>A generated request on a model the application login loaded itself.</summary>
    private async Task<ExecutionResult> ExecuteAsync(string document)
    {
        var connFactory = new PostgresDbConnFactory(_appConn);
        var model = await new DbModelLoader(connFactory, new MetadataLoader(Array.Empty<string>())).LoadAsync();
        var schema = DbSchema.FromModel(model);
        var services = new ServiceCollection();
        services.AddSingleton<IMutationTransformers>(new MutationTransformersWrap());
        services.AddSingleton<IFilterTransformers>(new FilterTransformersWrap { Transformers = Array.Empty<IFilterTransformer>() });
        await using var provider = services.BuildServiceProvider();

        return await new DocumentExecuter().ExecuteAsync(options =>
        {
            options.Schema = schema;
            options.Query = document;
            options.RequestServices = provider;
            options.UserContext = new Dictionary<string, object?>();
            options.Extensions = new Inputs(new Dictionary<string, object?>
            {
                ["connFactory"] = connFactory,
                ["model"] = model,
                ["tableReaderFactory"] = new SqlExecutionManager(model, schema, NullQueryTransformerService.Instance),
            });
        });
    }
}
