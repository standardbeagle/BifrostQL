using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace BifrostQL.Samples.HostedSpa;

/// <summary>
/// Creates and seeds the small SQLite database used by the HostedSpa sample so the
/// sample runs without any external database setup.
/// </summary>
internal static class SampleDatabase
{
    /// <summary>Email of the deterministic first-admin app_user seeded for local auth.</summary>
    public const string FirstAdminEmail = "admin@riverside-tennis.example";

    /// <summary>Plaintext password of the seeded first-admin. Sample-only; not for production.</summary>
    public const string FirstAdminPassword = "ChangeMe!2024";

    /// <summary>Tenant id the seeded first-admin belongs to.</summary>
    public const string FirstAdminTenantId = "1";

    /// <summary>Role granted to the seeded first-admin.</summary>
    public const string FirstAdminRole = "admin";

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
            -- password_hash holds an ASP.NET Core PasswordHasher hash for local-auth login;
            -- roles is a denormalized, delimited role list LocalUserStore reads directly.
            -- MM's canonical role data lives in organization_memberships.role_id -> roles.name;
            -- the denormalized column is the simplest correct source for the sample's
            -- LocalAuthOptions.RolesColumn without LocalUserStore needing a join.
            CREATE TABLE app_users (
                user_id INTEGER PRIMARY KEY AUTOINCREMENT,
                tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
                email TEXT NOT NULL,
                display_name TEXT NOT NULL,
                password_hash TEXT,
                roles TEXT,
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
                status TEXT NOT NULL DEFAULT 'draft',
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
            -- First-admin app_user. password_hash/roles are filled in by SeedFirstAdmin
            -- after this batch so the hash comes from the real ASP.NET Core PasswordHasher.
            INSERT INTO app_users (user_id, tenant_id, email, display_name)
                VALUES (1, 1, 'admin@riverside-tennis.example', 'Club Admin');
            -- A second app_user with no linked member yet, so the identity-linking
            -- sidecar endpoint has an unlinked login to associate.
            INSERT INTO app_users (user_id, tenant_id, email, display_name)
                VALUES (2, 1, 'dana@riverside-tennis.example', 'Dana Lopez');
            INSERT INTO members (member_id, tenant_id, user_id, first_name, last_name, email, status, joined_on)
                VALUES (1, 1, 1, 'Carol', 'Reyes', 'carol@example.com', 'active', '2024-01-10');
            -- A member with no user_id yet — the identity-linking endpoint sets it.
            INSERT INTO members (member_id, tenant_id, user_id, first_name, last_name, email, status, joined_on)
                VALUES (2, 1, NULL, 'Dana', 'Lopez', 'dana@example.com', 'active', '2024-02-01');
            INSERT INTO membership_plans (plan_id, tenant_id, name, billing_period, price_cents)
                VALUES (1, 1, 'Individual', 'annual', 12000);
            INSERT INTO member_memberships (member_membership_id, tenant_id, member_id, plan_id, start_date, end_date, status)
                VALUES (1, 1, 1, 1, '2024-01-10', '2025-01-10', 'expired');
            INSERT INTO dues_invoices (invoice_id, tenant_id, member_id, member_membership_id, amount_cents, issued_on, due_on, status)
                VALUES (1, 1, 1, 1, 12000, '2024-12-15', '2025-01-10', 'open');
            INSERT INTO events (event_id, tenant_id, title, description, location, status, starts_at, ends_at, capacity, created_at)
                VALUES (1, 1, 'Spring Open House', 'Season kick-off social', 'Main Clubhouse', 'published', '2025-03-01 10:00:00', '2025-03-01 14:00:00', 60, '2025-01-15 09:00:00');
            """;
        command.ExecuteNonQuery();

        SeedFirstAdmin(connection);
    }

    /// <summary>
    /// Fills in the first-admin <c>app_user</c> (user_id 1) password hash and role list
    /// so a self-hosted club can sign in with local auth out of the box. The password is
    /// hashed with the same ASP.NET Core <see cref="PasswordHasher{TUser}"/>
    /// <c>LocalUserStore.VerifyCredentialsAsync</c> verifies against, so the seeded hash
    /// and the verification path always agree. The <c>roles</c> column carries the
    /// denormalized <c>admin</c> role LocalUserStore reads directly. The row itself is
    /// inserted in the main seed batch so foreign keys to it resolve.
    /// </summary>
    private static void SeedFirstAdmin(SqliteConnection connection)
    {
        var passwordHash = new PasswordHasher<string>()
            .HashPassword(FirstAdminEmail, FirstAdminPassword);

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE app_users
            SET password_hash = $passwordHash, roles = $roles
            WHERE user_id = 1;
            """;
        command.Parameters.AddWithValue("$passwordHash", passwordHash);
        command.Parameters.AddWithValue("$roles", FirstAdminRole);
        command.ExecuteNonQuery();
    }
}
