-- Membership Manager schema (SQLite)
--
-- Tenant-aware database for a club / association membership application.
-- Built on the reusable org-model foundation (tenants, app_users,
-- organization_memberships, roles, role_permissions, audit_log) and extended
-- with the membership domain: members, households, membership plans, member
-- memberships, dues invoices/payments, events, and event RSVPs/attendance.
--
-- Self-contained: this file creates the org-model base tables it depends on
-- so the schema loads standalone (a club using "membership-manager" does not
-- also need to load "org-model").
--
-- Every tenant-owned table carries a tenant_id column suitable for the
-- BifrostQL tenant-filter / auto-filter modules. Global lookup tables
-- (roles, role_permissions) are explicitly un-scoped.

-- ============================================================
-- Org-model foundation (tenant = club / organization)
-- ============================================================

CREATE TABLE tenants (
    tenant_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    slug TEXT NOT NULL UNIQUE,
    plan TEXT NOT NULL DEFAULT 'standard',
    is_active INTEGER NOT NULL DEFAULT 1,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

CREATE TABLE roles (
    role_id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE,
    description TEXT,
    is_system INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE role_permissions (
    role_permission_id INTEGER PRIMARY KEY AUTOINCREMENT,
    role_id INTEGER NOT NULL REFERENCES roles(role_id) ON DELETE CASCADE,
    permission TEXT NOT NULL,
    UNIQUE(role_id, permission)
);

-- password_hash holds an ASP.NET Core PasswordHasher hash for local-auth login;
-- roles is a denormalized, delimited role list LocalUserStore reads directly. The
-- canonical role data lives in organization_memberships.role_id -> roles.name; the
-- denormalized column is the simplest correct source for LocalAuthOptions.RolesColumn.
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

CREATE TABLE organization_memberships (
    membership_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES app_users(user_id) ON DELETE CASCADE,
    role_id INTEGER NOT NULL REFERENCES roles(role_id),
    status TEXT NOT NULL DEFAULT 'active',
    joined_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(tenant_id, user_id)
);

-- Audit log — append-only trail of workflow mutations and admin actions
-- (membership status changes, payment edits, role changes). Tenant-scoped so
-- it is filtered to the caller's clubs by the tenant-filter / auto-filter
-- modules. Workflow endpoints write one row here per high-level operation.
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

-- ============================================================
-- Membership domain (all tenant-scoped on tenant_id = club)
-- ============================================================

-- Households — a billing / family grouping of members within a club.
CREATE TABLE households (
    household_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    address_line1 TEXT,
    city TEXT,
    region TEXT,
    postal_code TEXT,
    created_at TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Members — a person enrolled in a club. Optionally linked to an app_user
-- (members who can also log in) and to a household. soft-delete via deleted_at.
CREATE TABLE members (
    member_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    user_id INTEGER REFERENCES app_users(user_id) ON DELETE SET NULL,
    household_id INTEGER REFERENCES households(household_id) ON DELETE SET NULL,
    first_name TEXT NOT NULL,
    last_name TEXT NOT NULL,
    email TEXT,
    phone TEXT,
    status TEXT NOT NULL DEFAULT 'active',
    joined_on TEXT NOT NULL DEFAULT (datetime('now')),
    deleted_at TEXT
);

-- Household members — links members to a household with a relationship role
-- (head, spouse, child). Redundant with members.household_id but allows a
-- member to be associated with a household before/independent of assignment.
CREATE TABLE household_members (
    household_member_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    household_id INTEGER NOT NULL REFERENCES households(household_id) ON DELETE CASCADE,
    member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    relationship TEXT NOT NULL DEFAULT 'member',
    UNIQUE(household_id, member_id)
);

-- Membership plans — the tiers a club offers (individual, family, student).
CREATE TABLE membership_plans (
    plan_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    description TEXT,
    billing_period TEXT NOT NULL DEFAULT 'annual',
    price_cents INTEGER NOT NULL DEFAULT 0,
    is_active INTEGER NOT NULL DEFAULT 1
);

-- Member memberships — a member's enrollment in a plan for a term.
CREATE TABLE member_memberships (
    member_membership_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    plan_id INTEGER NOT NULL REFERENCES membership_plans(plan_id),
    start_date TEXT NOT NULL,
    end_date TEXT,
    status TEXT NOT NULL DEFAULT 'active'
);

-- Dues invoices — an amount owed by a member for a membership term.
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

-- Dues payments — a payment applied against an invoice.
CREATE TABLE dues_payments (
    payment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    invoice_id INTEGER NOT NULL REFERENCES dues_invoices(invoice_id) ON DELETE CASCADE,
    amount_cents INTEGER NOT NULL,
    paid_on TEXT NOT NULL DEFAULT (datetime('now')),
    method TEXT NOT NULL DEFAULT 'card'
);

-- Events — club events members can RSVP to and attend.
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

-- Event RSVPs — a member's intent to attend an event.
CREATE TABLE event_rsvps (
    rsvp_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    event_id INTEGER NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    response TEXT NOT NULL DEFAULT 'yes',
    guests INTEGER NOT NULL DEFAULT 0,
    responded_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(event_id, member_id)
);

-- Event attendance — recorded check-in for a member at an event.
CREATE TABLE event_attendance (
    attendance_id INTEGER PRIMARY KEY AUTOINCREMENT,
    tenant_id INTEGER NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    event_id INTEGER NOT NULL REFERENCES events(event_id) ON DELETE CASCADE,
    member_id INTEGER NOT NULL REFERENCES members(member_id) ON DELETE CASCADE,
    checked_in_at TEXT NOT NULL DEFAULT (datetime('now')),
    UNIQUE(event_id, member_id)
);
