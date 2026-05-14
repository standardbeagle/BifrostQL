-- Membership Manager sample seed data (PostgreSQL)
--
-- Tenant-aware club / association membership model built on the org-model
-- foundation. Self-contained: includes DDL + seed data so it loads cleanly on
-- a fresh PostgreSQL database. The table shape mirrors membership-manager.sql
-- and the SQLite seed sample; only the type names and identity syntax differ.
--
-- Seeds three independent clubs so tenant isolation can be exercised: a
-- cross-tenant read with a tenant_ids claim of {1} must never return rows for
-- clubs 2 or 3.
--
-- Recommended BifrostQL metadata configuration (apply via the Metadata config
-- section — these are NOT part of the DDL). Claim names match the canonical
-- user-context keys from MetadataKeys.Auth: tenant_id, tenant_ids, user_id,
-- roles, permissions.
--
--   Tenant-scoped tables (carry tenant-filter + auto-filter, label as shown):
--     tenants               { label: Clubs, auto-filter: tenant_id:tenant_ids }   -- row is the club itself
--     app_users             { label: App Users,        tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     organization_memberships { label: Staff Roles,   tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     audit_log             { label: Audit Log,        tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read }
--     households            { label: Households,       tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     members               { label: Members,         tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, soft-delete: deleted_at }
--     household_members     { label: Household Members,tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     membership_plans      { label: Membership Plans, tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     member_memberships    { label: Member Memberships,tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     dues_invoices         { label: Dues Invoices,    tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     dues_payments         { label: Dues Payments,    tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     events                { label: Events,          tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     event_rsvps           { label: Event RSVPs,      tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     event_attendance      { label: Event Attendance, tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--
--   Global lookup tables (un-scoped — do NOT add tenant-filter/auto-filter):
--     roles
--     role_permissions
--
-- tenant-filter is the mandatory tenant-isolation mechanism: it constrains
-- every query on a tenant-scoped table to the caller's tenant_id. auto-filter
-- "tenant_id:tenant_ids" additionally reads the plural tenant_ids claim and
-- limits tenant_id to that set, so a user only sees clubs they belong to.
--
-- soft-delete: deleted_at on members means a resigned member is flagged
-- rather than physically removed, preserving dues and attendance history.
--
-- audit_log carries policy-actions: read so it is read-only through the
-- generated CRUD — only server-side workflow endpoints append rows.

-- ============================================================
-- Org-model foundation
-- ============================================================

CREATE TABLE tenants (
    tenant_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name         TEXT NOT NULL,
    slug         TEXT NOT NULL UNIQUE,
    plan         TEXT NOT NULL DEFAULT 'standard',
    is_active    BOOLEAN NOT NULL DEFAULT TRUE,
    created_at   TIMESTAMP NOT NULL DEFAULT now()
);

CREATE TABLE roles (
    role_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name         TEXT NOT NULL UNIQUE,
    description  TEXT,
    is_system    BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE role_permissions (
    role_permission_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    role_id      BIGINT NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
    permission   TEXT NOT NULL,
    UNIQUE(role_id, permission)
);

CREATE TABLE app_users (
    user_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    email        TEXT NOT NULL,
    display_name TEXT NOT NULL,
    is_active    BOOLEAN NOT NULL DEFAULT TRUE,
    created_at   TIMESTAMP NOT NULL DEFAULT now(),
    UNIQUE(tenant_id, email)
);

CREATE TABLE organization_memberships (
    membership_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    user_id      BIGINT NOT NULL REFERENCES app_users(user_id) ON DELETE CASCADE,
    role_id      BIGINT NOT NULL REFERENCES roles(role_id),
    status       TEXT NOT NULL DEFAULT 'active',
    joined_at    TIMESTAMP NOT NULL DEFAULT now(),
    UNIQUE(tenant_id, user_id)
);

-- Audit log — append-only trail of workflow mutations and admin actions
-- (membership status changes, payment edits, role changes). Tenant-scoped so
-- it is filtered to the caller's clubs by the tenant-filter / auto-filter
-- modules. Workflow endpoints write one row here per high-level operation.
CREATE TABLE audit_log (
    audit_id     BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    actor_user_id BIGINT REFERENCES app_users(user_id) ON DELETE SET NULL,
    action       TEXT NOT NULL,
    entity_type  TEXT NOT NULL,
    entity_id    TEXT NOT NULL,
    summary      TEXT,
    created_at   TIMESTAMP NOT NULL DEFAULT now()
);

-- ============================================================
-- Membership domain (all tenant-scoped on tenant_id = club)
-- ============================================================

CREATE TABLE households (
    household_id  BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id     BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    name          TEXT NOT NULL,
    address_line1 TEXT,
    city          TEXT,
    region        TEXT,
    postal_code   TEXT,
    created_at    TIMESTAMP NOT NULL DEFAULT now()
);

CREATE TABLE members (
    member_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    user_id      BIGINT REFERENCES app_users(user_id) ON DELETE SET NULL,
    household_id BIGINT REFERENCES households(household_id) ON DELETE SET NULL,
    first_name   TEXT NOT NULL,
    last_name    TEXT NOT NULL,
    email        TEXT,
    phone        TEXT,
    status       TEXT NOT NULL DEFAULT 'active',
    joined_on    TIMESTAMP NOT NULL DEFAULT now(),
    deleted_at   TIMESTAMP
);

CREATE TABLE household_members (
    household_member_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    household_id BIGINT NOT NULL REFERENCES households(household_id) ON DELETE CASCADE,
    member_id    BIGINT NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    relationship TEXT NOT NULL DEFAULT 'member',
    UNIQUE(household_id, member_id)
);

CREATE TABLE membership_plans (
    plan_id        BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id      BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    name           TEXT NOT NULL,
    description    TEXT,
    billing_period TEXT NOT NULL DEFAULT 'annual',
    price_cents    BIGINT NOT NULL DEFAULT 0,
    is_active      BOOLEAN NOT NULL DEFAULT TRUE
);

CREATE TABLE member_memberships (
    member_membership_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    member_id    BIGINT NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    plan_id      BIGINT NOT NULL REFERENCES membership_plans(plan_id),
    start_date   DATE NOT NULL,
    end_date     DATE,
    status       TEXT NOT NULL DEFAULT 'active'
);

CREATE TABLE dues_invoices (
    invoice_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    member_id    BIGINT NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    member_membership_id BIGINT REFERENCES member_memberships(member_membership_id) ON DELETE SET NULL,
    amount_cents BIGINT NOT NULL,
    issued_on    TIMESTAMP NOT NULL DEFAULT now(),
    due_on       DATE NOT NULL,
    status       TEXT NOT NULL DEFAULT 'open'
);

CREATE TABLE dues_payments (
    payment_id   BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    invoice_id   BIGINT NOT NULL REFERENCES dues_invoices(invoice_id) ON DELETE CASCADE,
    amount_cents BIGINT NOT NULL,
    paid_on      TIMESTAMP NOT NULL DEFAULT now(),
    method       TEXT NOT NULL DEFAULT 'card'
);

CREATE TABLE events (
    event_id    BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id   BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    title       TEXT NOT NULL,
    description TEXT,
    location    TEXT,
    starts_at   TIMESTAMP NOT NULL,
    ends_at     TIMESTAMP,
    capacity    INTEGER,
    created_at  TIMESTAMP NOT NULL DEFAULT now()
);

CREATE TABLE event_rsvps (
    rsvp_id      BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    event_id     BIGINT NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    member_id    BIGINT NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    response     TEXT NOT NULL DEFAULT 'yes',
    guests       INTEGER NOT NULL DEFAULT 0,
    responded_at TIMESTAMP NOT NULL DEFAULT now(),
    UNIQUE(event_id, member_id)
);

CREATE TABLE event_attendance (
    attendance_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id     BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    event_id      BIGINT NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    member_id     BIGINT NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    checked_in_at TIMESTAMP NOT NULL DEFAULT now(),
    UNIQUE(event_id, member_id)
);

-- ============================================================
-- Seed data
-- ============================================================

-- Clubs / organizations — three independent tenants (3)
INSERT INTO tenants (tenant_id, name, slug, plan, is_active, created_at) OVERRIDING SYSTEM VALUE VALUES
(1, 'Riverside Tennis Club', 'riverside-tennis', 'standard', TRUE, '2023-01-15 09:00:00'),
(2, 'Summit Hiking Association', 'summit-hiking', 'standard', TRUE, '2023-04-02 10:30:00'),
(3, 'Harbor Sailing Club', 'harbor-sailing', 'enterprise', TRUE, '2022-11-20 08:15:00');

-- Roles (global lookup — shared across all clubs) (3)
INSERT INTO roles (role_id, name, description, is_system) OVERRIDING SYSTEM VALUE VALUES
(1, 'owner', 'Full administrative control of the club', TRUE),
(2, 'admin', 'Manage members, plans, events, and dues', TRUE),
(3, 'member', 'Standard member access', TRUE);

-- Role permissions (global lookup) (8)
INSERT INTO role_permissions (role_id, permission) VALUES
(1, 'club:manage'),
(1, 'members:manage'),
(1, 'dues:manage'),
(1, 'events:manage'),
(2, 'members:manage'),
(2, 'dues:manage'),
(2, 'events:manage'),
(3, 'events:rsvp');

-- App users — staff/admins who log in (tenant-scoped) (5)
INSERT INTO app_users (user_id, tenant_id, email, display_name, is_active, created_at) OVERRIDING SYSTEM VALUE VALUES
(1, 1, 'manager@riverside-tennis.example', 'Tina Reed', TRUE, '2023-01-15 09:05:00'),
(2, 1, 'admin@riverside-tennis.example', 'Omar Vance', TRUE, '2023-01-18 11:00:00'),
(3, 2, 'manager@summit-hiking.example', 'Greta Holm', TRUE, '2023-04-02 10:35:00'),
(4, 3, 'manager@harbor-sailing.example', 'Pavel Marsh', TRUE, '2022-11-20 08:20:00'),
(5, 3, 'admin@harbor-sailing.example', 'Lena Cruz', TRUE, '2022-12-01 09:30:00');

-- Organization memberships — staff roles within a club (tenant-scoped) (5)
INSERT INTO organization_memberships (tenant_id, user_id, role_id, status, joined_at) VALUES
(1, 1, 1, 'active', '2023-01-15 09:05:00'),
(1, 2, 2, 'active', '2023-01-18 11:00:00'),
(2, 3, 1, 'active', '2023-04-02 10:35:00'),
(3, 4, 1, 'active', '2022-11-20 08:20:00'),
(3, 5, 2, 'active', '2022-12-01 09:30:00');

-- Households (tenant-scoped) (5)
INSERT INTO households (household_id, tenant_id, name, address_line1, city, region, postal_code, created_at) OVERRIDING SYSTEM VALUE VALUES
(1, 1, 'Carter Household', '12 Oak Lane', 'Riverside', 'CA', '92501', '2023-02-01 10:00:00'),
(2, 1, 'Singh Household', '88 Maple Ave', 'Riverside', 'CA', '92503', '2023-02-10 14:00:00'),
(3, 2, 'Holm Household', '5 Trailhead Rd', 'Summit', 'CO', '80498', '2023-04-10 09:00:00'),
(4, 3, 'Marsh Household', '3 Dockside Way', 'Harbor', 'WA', '98110', '2023-01-05 11:00:00'),
(5, 3, 'Cruz Household', '21 Pier Street', 'Harbor', 'WA', '98110', '2023-01-12 16:00:00');

-- Members (tenant-scoped, soft-delete via deleted_at) (8)
-- Member 4 (Diane Carter) is soft-deleted to exercise the soft-delete rule.
INSERT INTO members (member_id, tenant_id, user_id, household_id, first_name, last_name, email, phone, status, joined_on, deleted_at) OVERRIDING SYSTEM VALUE VALUES
(1, 1, 1, 1, 'Tina', 'Reed', 'tina@riverside-tennis.example', '555-0101', 'active', '2023-01-15 09:05:00', NULL),
(2, 1, NULL, 1, 'Mike', 'Carter', 'mike.carter@example.com', '555-0102', 'active', '2023-02-01 10:05:00', NULL),
(3, 1, NULL, 2, 'Anita', 'Singh', 'anita.singh@example.com', '555-0103', 'active', '2023-02-10 14:05:00', NULL),
(4, 1, NULL, 1, 'Diane', 'Carter', 'diane.carter@example.com', '555-0104', 'resigned', '2023-02-01 10:06:00', '2024-06-30 12:00:00'),
(5, 2, 3, 3, 'Greta', 'Holm', 'greta@summit-hiking.example', '555-0201', 'active', '2023-04-02 10:35:00', NULL),
(6, 2, NULL, 3, 'Ben', 'Holm', 'ben.holm@example.com', '555-0202', 'active', '2023-04-10 09:05:00', NULL),
(7, 3, 4, 4, 'Pavel', 'Marsh', 'pavel@harbor-sailing.example', '555-0301', 'active', '2022-11-20 08:20:00', NULL),
(8, 3, NULL, 5, 'Lena', 'Cruz', 'lena.cruz@example.com', '555-0302', 'active', '2023-01-12 16:05:00', NULL);

-- Household members (tenant-scoped) (8)
INSERT INTO household_members (tenant_id, household_id, member_id, relationship) VALUES
(1, 1, 2, 'head'),
(1, 1, 4, 'spouse'),
(1, 1, 1, 'member'),
(1, 2, 3, 'head'),
(2, 3, 5, 'head'),
(2, 3, 6, 'child'),
(3, 4, 7, 'head'),
(3, 5, 8, 'head');

-- Membership plans (tenant-scoped) (6)
INSERT INTO membership_plans (plan_id, tenant_id, name, description, billing_period, price_cents, is_active) OVERRIDING SYSTEM VALUE VALUES
(1, 1, 'Individual', 'Single-person annual membership', 'annual', 24000, TRUE),
(2, 1, 'Family', 'Household annual membership', 'annual', 40000, TRUE),
(3, 2, 'Standard', 'Annual hiking association membership', 'annual', 8000, TRUE),
(4, 2, 'Student', 'Discounted annual membership for students', 'annual', 4000, TRUE),
(5, 3, 'Crew', 'Annual sailing crew membership', 'annual', 60000, TRUE),
(6, 3, 'Skipper', 'Annual membership with berth privileges', 'annual', 120000, TRUE);

-- Member memberships (tenant-scoped) (7)
INSERT INTO member_memberships (tenant_id, member_id, plan_id, start_date, end_date, status) VALUES
(1, 1, 1, '2024-01-01', '2024-12-31', 'active'),
(1, 2, 2, '2024-01-01', '2024-12-31', 'active'),
(1, 3, 1, '2024-03-01', '2025-02-28', 'active'),
(2, 5, 3, '2024-01-01', '2024-12-31', 'active'),
(2, 6, 4, '2024-01-01', '2024-12-31', 'active'),
(3, 7, 6, '2024-01-01', '2024-12-31', 'active'),
(3, 8, 5, '2024-02-01', '2025-01-31', 'active');

-- Dues invoices (tenant-scoped) (7)
INSERT INTO dues_invoices (tenant_id, member_id, member_membership_id, amount_cents, issued_on, due_on, status) VALUES
(1, 1, 1, 24000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(1, 2, 2, 40000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(1, 3, 3, 24000, '2024-03-01 00:00:00', '2024-03-31', 'open'),
(2, 5, 4, 8000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(2, 6, 5, 4000, '2024-01-01 00:00:00', '2024-01-31', 'open'),
(3, 7, 6, 120000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(3, 8, 7, 60000, '2024-02-01 00:00:00', '2024-02-29', 'open');

-- Dues payments (tenant-scoped) (4)
INSERT INTO dues_payments (tenant_id, invoice_id, amount_cents, paid_on, method) VALUES
(1, 1, 24000, '2024-01-10 09:00:00', 'card'),
(1, 2, 40000, '2024-01-12 14:00:00', 'bank'),
(2, 4, 8000, '2024-01-08 11:00:00', 'card'),
(3, 6, 120000, '2024-01-15 10:00:00', 'bank');

-- Events (tenant-scoped) (4)
INSERT INTO events (event_id, tenant_id, title, description, location, starts_at, ends_at, capacity, created_at) OVERRIDING SYSTEM VALUE VALUES
(1, 1, 'Spring Doubles Tournament', 'Annual members doubles tournament', 'Riverside Courts', '2024-05-18 09:00:00', '2024-05-18 17:00:00', 32, '2024-04-01 10:00:00'),
(2, 1, 'New Member Mixer', 'Welcome social for new members', 'Clubhouse', '2024-06-05 18:00:00', '2024-06-05 20:00:00', 50, '2024-05-01 10:00:00'),
(3, 2, 'Summit Ridge Day Hike', 'Guided group hike on the ridge trail', 'Summit Trailhead', '2024-06-15 07:00:00', '2024-06-15 15:00:00', 20, '2024-05-10 09:00:00'),
(4, 3, 'Harbor Regatta', 'Club regatta and awards dinner', 'Harbor Marina', '2024-07-20 08:00:00', '2024-07-20 21:00:00', 40, '2024-06-01 09:00:00');

-- Event RSVPs (tenant-scoped) (6)
INSERT INTO event_rsvps (tenant_id, event_id, member_id, response, guests, responded_at) VALUES
(1, 1, 1, 'yes', 0, '2024-04-10 12:00:00'),
(1, 1, 2, 'yes', 1, '2024-04-11 09:00:00'),
(1, 2, 3, 'maybe', 0, '2024-05-05 15:00:00'),
(2, 3, 5, 'yes', 0, '2024-05-12 08:00:00'),
(2, 3, 6, 'yes', 0, '2024-05-12 08:05:00'),
(3, 4, 7, 'yes', 2, '2024-06-05 10:00:00');

-- Event attendance (tenant-scoped) (4)
INSERT INTO event_attendance (tenant_id, event_id, member_id, checked_in_at) VALUES
(1, 1, 1, '2024-05-18 08:45:00'),
(1, 1, 2, '2024-05-18 08:50:00'),
(2, 3, 5, '2024-06-15 06:55:00'),
(2, 3, 6, '2024-06-15 06:57:00');
