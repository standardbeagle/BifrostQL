using Microsoft.Data.Sqlite;

namespace BifrostQL.Samples.HostedSpa;

/// <summary>
/// Creates and seeds the small SQLite database used by the HostedSpa sample so the
/// sample runs without any external database setup.
/// </summary>
internal static class SampleDatabase
{
    /// <summary>
    /// Resolves the SQLite database file path from the configured connection string.
    /// Falls back to <c>hostedspa-sample.db</c> under <paramref name="contentRootPath"/>
    /// when no connection string or <c>Data Source</c> is configured. Relative
    /// <c>Data Source</c> values are resolved against the content root.
    /// </summary>
    /// <param name="connectionString">The configured <c>bifrost</c> connection string, if any.</param>
    /// <param name="contentRootPath">The host content root used to resolve relative paths.</param>
    /// <returns>An absolute path to the SQLite database file.</returns>
    public static string ResolveDbPath(string? connectionString, string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);

        var dataSource = string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new SqliteConnectionStringBuilder(connectionString).DataSource;

        if (string.IsNullOrWhiteSpace(dataSource))
            dataSource = "hostedspa-sample.db";

        return Path.IsPathRooted(dataSource)
            ? dataSource
            : Path.Combine(contentRootPath, dataSource);
    }

    /// <summary>
    /// Creates the sample database file if it does not already exist, seeding a
    /// <c>widgets</c> table plus the slice of the Membership Manager schema the
    /// sidecar workflow endpoints operate on (<c>tenants</c>, <c>app_users</c>,
    /// <c>members</c>, <c>membership_plans</c>, <c>member_memberships</c>,
    /// <c>dues_invoices</c>, <c>dues_payments</c>, <c>audit_log</c>,
    /// <c>events</c>, <c>event_attendance</c>). Existing files are left
    /// untouched.
    /// </summary>
    /// <param name="dbPath">Absolute path to the SQLite database file.</param>
    public static void EnsureCreated(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        if (File.Exists(dbPath))
            return;

        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE widgets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                color TEXT NOT NULL
            );
            INSERT INTO widgets (name, color) VALUES ('Sprocket', 'red');
            INSERT INTO widgets (name, color) VALUES ('Gear', 'blue');
            INSERT INTO widgets (name, color) VALUES ('Cog', 'green');

            -- Membership Manager slice (see src/BifrostQL.UI/Schemas/membership-manager.sql).
            -- Only the tables the workflow endpoints touch are created here so the
            -- HostedSpa sample exercises the sidecar endpoints without an external db.
            CREATE TABLE tenants (
                tenant_id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                slug TEXT NOT NULL UNIQUE,
                plan TEXT NOT NULL DEFAULT 'standard',
                is_active INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE app_users (
                user_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                email TEXT NOT NULL,
                display_name TEXT NOT NULL,
                is_active INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(tenant_id, email)
            );
            CREATE TABLE audit_log (
                audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                actor_user_id INTEGER REFERENCES app_users(user_id) ON DELETE SET NULL,
                action TEXT NOT NULL,
                entity_type TEXT NOT NULL,
                entity_id TEXT NOT NULL,
                summary TEXT,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE members (
                member_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                user_id INTEGER REFERENCES app_users(user_id) ON DELETE SET NULL,
                household_id INTEGER,
                first_name TEXT NOT NULL,
                last_name TEXT NOT NULL,
                email TEXT,
                phone TEXT,
                status TEXT NOT NULL DEFAULT 'active',
                joined_on TEXT NOT NULL DEFAULT (datetime('now')),
                deleted_at TEXT
            );
            CREATE TABLE membership_plans (
                plan_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                name TEXT NOT NULL,
                description TEXT,
                billing_period TEXT NOT NULL DEFAULT 'annual',
                price_cents INTEGER NOT NULL DEFAULT 0,
                is_active INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE member_memberships (
                member_membership_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
                plan_id INTEGER NOT NULL REFERENCES membership_plans(plan_id),
                start_date TEXT NOT NULL,
                end_date TEXT,
                status TEXT NOT NULL DEFAULT 'active'
            );
            CREATE TABLE dues_invoices (
                invoice_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
                member_membership_id INTEGER REFERENCES member_memberships(member_membership_id) ON DELETE SET NULL,
                amount_cents INTEGER NOT NULL,
                issued_on TEXT NOT NULL DEFAULT (datetime('now')),
                due_on TEXT NOT NULL,
                status TEXT NOT NULL DEFAULT 'open'
            );
            CREATE TABLE dues_payments (
                payment_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                invoice_id INTEGER NOT NULL REFERENCES dues_invoices(invoice_id) ON DELETE CASCADE,
                amount_cents INTEGER NOT NULL,
                paid_on TEXT NOT NULL DEFAULT (datetime('now')),
                method TEXT NOT NULL DEFAULT 'card'
            );
            CREATE TABLE events (
                event_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                title TEXT NOT NULL,
                description TEXT,
                location TEXT,
                starts_at TEXT NOT NULL,
                ends_at TEXT,
                capacity INTEGER,
                created_at TEXT NOT NULL DEFAULT (datetime('now'))
            );
            CREATE TABLE event_attendance (
                attendance_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                event_id INTEGER NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
                member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
                checked_in_at TEXT NOT NULL DEFAULT (datetime('now')),
                UNIQUE(event_id, member_id)
            );

            INSERT INTO tenants (tenant_id, name, slug) VALUES (1, 'Riverside Tennis Club', 'riverside-tennis');
            INSERT INTO app_users (user_id, tenant_id, email, display_name)
                VALUES (1, 1, 'officer@riverside-tennis.example', 'Club Officer');
            INSERT INTO members (member_id, tenant_id, user_id, first_name, last_name, email, status, joined_on)
                VALUES (1, 1, 1, 'Carol', 'Reyes', 'carol@example.com', 'active', '2024-01-10');
            INSERT INTO membership_plans (plan_id, tenant_id, name, billing_period, price_cents)
                VALUES (1, 1, 'Individual', 'annual', 12000);
            INSERT INTO member_memberships (member_membership_id, tenant_id, member_id, plan_id, start_date, end_date, status)
                VALUES (1, 1, 1, 1, '2024-01-10', '2025-01-10', 'expired');
            INSERT INTO dues_invoices (invoice_id, tenant_id, member_id, member_membership_id, amount_cents, issued_on, due_on, status)
                VALUES (1, 1, 1, 1, 12000, '2024-12-15', '2025-01-10', 'open');
            INSERT INTO events (event_id, tenant_id, title, description, location, starts_at, ends_at, capacity, created_at)
                VALUES (1, 1, 'Spring Open House', 'Season kick-off social', 'Main Clubhouse', '2025-03-01 10:00:00', '2025-03-01 14:00:00', 60, '2025-01-15 09:00:00');
            """;
        command.ExecuteNonQuery();
    }
}
