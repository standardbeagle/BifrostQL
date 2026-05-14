-- Org model sample seed data (PostgreSQL)
--
-- Reusable multi-tenant organization model: tenants (organizations), app users,
-- memberships, roles, role permissions, invitations, and an audit log. Use this
-- as a starting point for tenant-isolated applications built on BifrostQL.
--
-- Self-contained: includes DDL + seed data so it loads cleanly on a fresh
-- PostgreSQL database. The table shape mirrors org-model.sql / the SQLite
-- seed sample; only the type names and identity syntax differ.
--
-- Recommended BifrostQL metadata configuration (apply via the Metadata config
-- section — these are NOT part of the DDL). Claim names match the canonical
-- user-context keys from MetadataKeys.Auth: tenant_id, tenant_ids, user_id,
-- roles, permissions.
--
--   Tenant-scoped tables (carry tenant-filter + auto-filter):
--     app_users                { tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     organization_memberships { tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     invitations              { tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     audit_log                { tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     tenants                  { auto-filter: tenant_id:tenant_ids }   -- row is the tenant itself, filter on its own id
--
--   Global lookup tables (un-scoped — do NOT add tenant-filter/auto-filter):
--     roles
--     role_permissions
--
-- The auto-filter mapping "tenant_id:tenant_ids" reads the plural tenant_ids
-- claim from the user context and constrains the tenant_id column to that set,
-- so a user only sees rows for organizations they belong to.
--
-- The audit_log table is the audit trail for workflow mutations (see the
-- "Workflow Mutations & Audit Trail" guide). It is tenant-scoped exactly like
-- the other application tables, so audit entries are queryable through Bifrost
-- and filtered to the caller's organizations. Recommend also write-denying it
-- to clients so only server-side workflow endpoints can append rows:
--     audit_log { policy-actions: read }   -- read-only through generated CRUD

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

CREATE TABLE invitations (
    invitation_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    tenant_id    BIGINT NOT NULL REFERENCES tenants(tenant_id) ON DELETE CASCADE,
    email        TEXT NOT NULL,
    role_id      BIGINT NOT NULL REFERENCES roles(role_id),
    invited_by_user_id BIGINT REFERENCES app_users(user_id) ON DELETE SET NULL,
    status       TEXT NOT NULL DEFAULT 'pending',
    created_at   TIMESTAMP NOT NULL DEFAULT now(),
    expires_at   TIMESTAMP
);

-- Audit log — append-only trail of workflow mutations and admin actions
-- (status changes, payment edits, role changes). Tenant-scoped so it is
-- queryable through Bifrost like any other table and filtered to the caller's
-- organizations. Workflow endpoints write one row here per high-level
-- operation; raw CRUD never writes it directly.
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

-- Tenants / organizations (2) — identity auto-generates tenant_id 1, 2
INSERT INTO tenants (name, slug, plan, is_active, created_at) VALUES
('Acme Corporation', 'acme', 'enterprise', TRUE, '2024-01-10 09:00:00'),
('Globex Industries', 'globex', 'standard', TRUE, '2024-03-22 14:30:00');

-- Roles (global lookup — shared across all tenants) (3)
INSERT INTO roles (role_id, name, description, is_system) OVERRIDING SYSTEM VALUE VALUES
(1, 'owner', 'Full administrative control of the organization', TRUE),
(2, 'admin', 'Manage members, settings, and content', TRUE),
(3, 'member', 'Standard access to organization resources', TRUE);

-- Role permissions (global lookup) (8)
INSERT INTO role_permissions (role_id, permission) VALUES
(1, 'org:manage'),
(1, 'members:manage'),
(1, 'content:write'),
(1, 'content:read'),
(2, 'members:manage'),
(2, 'content:write'),
(2, 'content:read'),
(3, 'content:read');

-- App users (tenant-scoped) (5)
INSERT INTO app_users (user_id, tenant_id, email, display_name, is_active, created_at) OVERRIDING SYSTEM VALUE VALUES
(1, 1, 'alice@acme.example', 'Alice Anderson', TRUE, '2024-01-10 09:05:00'),
(2, 1, 'bob@acme.example', 'Bob Barker', TRUE, '2024-01-12 11:20:00'),
(3, 1, 'carol@acme.example', 'Carol Chen', TRUE, '2024-02-01 08:45:00'),
(4, 2, 'dave@globex.example', 'Dave Donovan', TRUE, '2024-03-22 14:35:00'),
(5, 2, 'erin@globex.example', 'Erin Edwards', TRUE, '2024-04-05 16:10:00');

-- Organization memberships (tenant-scoped — links users to tenants with a role) (5)
INSERT INTO organization_memberships (tenant_id, user_id, role_id, status, joined_at) VALUES
(1, 1, 1, 'active', '2024-01-10 09:05:00'),
(1, 2, 2, 'active', '2024-01-12 11:20:00'),
(1, 3, 3, 'active', '2024-02-01 08:45:00'),
(2, 4, 1, 'active', '2024-03-22 14:35:00'),
(2, 5, 3, 'active', '2024-04-05 16:10:00');

-- Invitations (tenant-scoped — optional) (2)
INSERT INTO invitations (tenant_id, email, role_id, invited_by_user_id, status, created_at, expires_at) VALUES
(1, 'frank@acme.example', 3, 1, 'pending', '2024-05-01 10:00:00', '2024-05-15 10:00:00'),
(2, 'grace@globex.example', 2, 4, 'pending', '2024-05-03 13:00:00', '2024-05-17 13:00:00');

-- Audit log (tenant-scoped — append-only trail of workflow mutations) (4)
-- Each row is one high-level operation: who (actor_user_id), what (action),
-- on which entity (entity_type/entity_id), and a human-readable summary.
INSERT INTO audit_log (tenant_id, actor_user_id, action, entity_type, entity_id, summary, created_at) VALUES
(1, 2, 'membership.role_changed', 'organization_memberships', '3', 'Carol Chen role changed from member to admin', '2024-04-10 09:30:00'),
(1, 1, 'invitation.sent', 'invitations', '1', 'Invitation sent to frank@acme.example as member', '2024-05-01 10:00:00'),
(2, 4, 'membership.status_changed', 'organization_memberships', '5', 'Erin Edwards membership set to suspended', '2024-04-20 15:45:00'),
(2, 4, 'invitation.sent', 'invitations', '2', 'Invitation sent to grace@globex.example as admin', '2024-05-03 13:00:00');
