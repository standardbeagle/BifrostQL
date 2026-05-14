-- Membership Manager sample seed data (SQLite)
--
-- Tenant-aware club / association membership model built on the org-model
-- foundation. Seeds three independent clubs so tenant isolation can be
-- exercised: a cross-tenant read with a tenant_ids claim of {1} must never
-- return rows for clubs 2 or 3.
--
-- Recommended BifrostQL metadata configuration (apply via the Metadata config
-- section — these are NOT part of the DDL). Claim names match the canonical
-- user-context keys from MetadataKeys.Auth: tenant_id, tenant_ids, user_id,
-- roles, permissions.
--
--   Tenant-scoped tables (carry tenant-filter + auto-filter, label as shown):
--     tenants               { label: Clubs, auto-filter: tenant_id:tenant_ids, policy-actions: read }   -- row is the club itself
--     app_users             { label: App Users,        tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids }
--     organization_memberships { label: Staff Roles,   tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,update }
--     audit_log             { label: Audit Log,        tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read }
--     households            { label: Households,       tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update,delete, policy-row-scope: household_id = {household_id}, policy-row-scope-roles: member }
--     members               { label: Members,         tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, soft-delete: deleted_at, policy-actions: read,create,update,delete, policy-row-scope: user_id = {user_id}, policy-row-scope-roles: member }
--     household_members     { label: Household Members,tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update,delete }
--     membership_plans      { label: Membership Plans, tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update,delete }
--     member_memberships    { label: Member Memberships,tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update,delete }
--     dues_invoices         { label: Dues Invoices,    tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update }
--     dues_payments         { label: Dues Payments,    tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update }
--     events                { label: Events,          tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update,delete }
--     event_rsvps           { label: Event RSVPs,      tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update,delete }
--     event_attendance      { label: Event Attendance, tenant-filter: tenant_id, auto-filter: tenant_id:tenant_ids, policy-actions: read,create,update,delete }
--
--   Global lookup tables (un-scoped — do NOT add tenant-filter/auto-filter):
--     roles                 { policy-actions: read }
--     role_permissions      { policy-actions: read }
--
-- tenant-filter is the mandatory tenant-isolation mechanism: it constrains
-- every query on a tenant-scoped table to the caller's tenant_id. auto-filter
-- "tenant_id:tenant_ids" additionally reads the plural tenant_ids claim and
-- limits tenant_id to that set, so a user only sees clubs they belong to.
--
-- soft-delete: deleted_at on members means a resigned member is flagged
-- (deleted_at set) rather than physically removed, preserving history for
-- dues and attendance records.
--
-- audit_log carries policy-actions: read so it is read-only through the
-- generated CRUD — only server-side workflow endpoints append rows.
--
-- ============================================================
-- Member self-service row scope (Membership Manager policy, sub-task 2/4)
-- ============================================================
--
-- `policy-row-scope` on members and households constrains the `member` role to
-- its own data. The expression grammar is a single `column = {context-key}`
-- term (RowScopeCompiler): the left side is a table column, the right side a
-- per-request user-context claim. PolicyFilterTransformer compiles it and ANDs
-- the resulting WHERE alongside the tenant filter on the query path;
-- PolicyMutationTransformer applies the same filter to update/delete so a
-- cross-member write matches no row and is rejected server-side.
--
-- `policy-row-scope-roles: member` qualifies the row scope to the `member` role
-- only. officer/event_manager/finance_manager/read_only hold a different role,
-- so the row-scope filter does not narrow them — their access to members and
-- households stays governed by the table-level policy-actions grants and the
-- tenant-filter (officer keeps full lifecycle CRUD within its tenant). admin
-- bypasses the policy engine entirely and is never narrowed.
--
--   members    policy-row-scope: user_id = {user_id}   policy-row-scope-roles: member
--              members.user_id is the FK to app_users — the member row linked to
--              the caller's login account. {user_id} is the canonical user-id
--              claim IdentityContextMapper always writes, so no extra wiring is
--              needed: a `member` sees and edits only their own member row.
--
--   households policy-row-scope: household_id = {household_id}   policy-row-scope-roles: member
--              A `member` may read and edit their own household. households has
--              no user FK, so the scope keys on the household_id the caller
--              belongs to. {household_id} is a provider claim — the
--              authentication layer resolves the caller's member row and adds
--              household_id to AppIdentity.Claims, which IdentityContextMapper
--              copies verbatim into the user context.
--
-- ============================================================
-- Role -> permission matrix (Membership Manager policy, sub-task 1/4)
-- ============================================================
--
-- Six roles drive the membership-manager authorization model. The matrix has
-- two layers:
--
--   1. Table-level allowed-action grants -- the `policy-actions` metadata shown
--      above. PolicyConfigCollector reads `policy-actions` from each table's
--      metadata and produces a TablePolicy; PolicyEvaluator gates generated
--      CRUD against it. This layer is the UNION of what any non-admin role may
--      do on the table, so every table permits `read` (satisfies read_only)
--      and each mutating table permits the widest action any role needs.
--   2. The role catalog + per-role grants -- the `roles` and `role_permissions`
--      seed rows below. These name the six roles and record their intended
--      grants as `<resource>:<action>` permission strings, the vocabulary the
--      later sub-tasks (field restrictions, row-scope) build on.
--
-- Role intent (table-level; field- and row-level nuance is sub-tasks 2 & 3):
--
--   admin            Full control of every table. Bypasses the policy engine
--                    entirely (MetadataKeys.Policy.DefaultAdminRole = "admin"),
--                    so it carries no per-table policy-actions restriction.
--   officer          Manages the member lifecycle: full CRUD on members,
--                    households, household_members, membership_plans,
--                    member_memberships. Read on finance, events, audit, org.
--   event_manager    Runs events end to end: full CRUD on events, event_rsvps,
--                    event_attendance. Read elsewhere.
--   finance_manager  Manages dues: create/update on dues_invoices and
--                    dues_payments (no delete -- dues are corrected, never
--                    destroyed). Read elsewhere.
--   member           Read-all plus self-service create/update on event_rsvps
--                    (RSVP to events). Self-scoping of "own" rows is sub-task 3.
--   read_only        Read on every table; no create/update/delete anywhere.
--
-- Per-table grant matrix (R=read C=create U=update D=delete; admin=full):
--
--   table                  officer  event_manager finance_manager member read_only
--   members                R C U D  R             R               R      R
--   households             R C U D  R             R               R      R
--   household_members      R C U D  R             R               R      R
--   membership_plans       R C U D  R             R               R      R
--   member_memberships     R C U D  R             R               R      R
--   dues_invoices          R        R             R C U           R      R
--   dues_payments          R        R             R C U           R      R
--   events                 R        R C U D       R               R      R
--   event_rsvps            R        R C U D       R               R C U  R
--   event_attendance       R        R C U D       R               R      R
--   audit_log              R        R             R               R      R
--   roles                  R        R             R               R      R
--   role_permissions       R        R             R               R      R
--   organization_memberships R U    R             R               R      R
--   tenants                R        R             R               R      R

-- ============================================================
-- Org-model foundation
-- ============================================================

-- Clubs / organizations — three independent tenants (3)
INSERT INTO tenants (tenant_id, name, slug, plan, is_active, created_at) VALUES
(1, 'Riverside Tennis Club', 'riverside-tennis', 'standard', 1, '2023-01-15 09:00:00'),
(2, 'Summit Hiking Association', 'summit-hiking', 'standard', 1, '2023-04-02 10:30:00'),
(3, 'Harbor Sailing Club', 'harbor-sailing', 'enterprise', 1, '2022-11-20 08:15:00');

-- Roles (global lookup — shared across all clubs) (6)
-- The six-role Membership Manager catalog. See the role -> permission matrix
-- in the header for table-level intent. admin bypasses the policy engine
-- (MetadataKeys.Policy.DefaultAdminRole = "admin").
INSERT INTO roles (role_id, name, description, is_system) VALUES
(1, 'admin', 'Full administrative control of the club; bypasses the policy engine', 1),
(2, 'officer', 'Manages the member lifecycle: members, households, plans, memberships', 1),
(3, 'event_manager', 'Runs events end to end: events, RSVPs, attendance', 1),
(4, 'finance_manager', 'Manages dues invoices and payments (create/update, no delete)', 1),
(5, 'member', 'Standard member: read-all plus self-service event RSVPs', 1),
(6, 'read_only', 'Read-only access to every table; no mutations', 1);

-- Role permissions (global lookup) — the per-role, per-table grant matrix as
-- `<table>:<action>` permission strings. Covers every MM table for every role.
-- This is the role-keyed layer; the table-level union is the `policy-actions`
-- metadata documented in the header. admin is granted explicit full CRUD on
-- every table for completeness even though it also bypasses the policy engine.
INSERT INTO role_permissions (role_permission_id, role_id, permission) VALUES
-- admin (role 1) — full CRUD on every MM table (15 tables x 4 = 60)
(1,  1, 'members:read'), (2,  1, 'members:create'), (3,  1, 'members:update'), (4,  1, 'members:delete'),
(5,  1, 'households:read'), (6,  1, 'households:create'), (7,  1, 'households:update'), (8,  1, 'households:delete'),
(9,  1, 'household_members:read'), (10, 1, 'household_members:create'), (11, 1, 'household_members:update'), (12, 1, 'household_members:delete'),
(13, 1, 'membership_plans:read'), (14, 1, 'membership_plans:create'), (15, 1, 'membership_plans:update'), (16, 1, 'membership_plans:delete'),
(17, 1, 'member_memberships:read'), (18, 1, 'member_memberships:create'), (19, 1, 'member_memberships:update'), (20, 1, 'member_memberships:delete'),
(21, 1, 'dues_invoices:read'), (22, 1, 'dues_invoices:create'), (23, 1, 'dues_invoices:update'), (24, 1, 'dues_invoices:delete'),
(25, 1, 'dues_payments:read'), (26, 1, 'dues_payments:create'), (27, 1, 'dues_payments:update'), (28, 1, 'dues_payments:delete'),
(29, 1, 'events:read'), (30, 1, 'events:create'), (31, 1, 'events:update'), (32, 1, 'events:delete'),
(33, 1, 'event_rsvps:read'), (34, 1, 'event_rsvps:create'), (35, 1, 'event_rsvps:update'), (36, 1, 'event_rsvps:delete'),
(37, 1, 'event_attendance:read'), (38, 1, 'event_attendance:create'), (39, 1, 'event_attendance:update'), (40, 1, 'event_attendance:delete'),
(41, 1, 'audit_log:read'), (42, 1, 'audit_log:create'), (43, 1, 'audit_log:update'), (44, 1, 'audit_log:delete'),
(45, 1, 'roles:read'), (46, 1, 'roles:create'), (47, 1, 'roles:update'), (48, 1, 'roles:delete'),
(49, 1, 'role_permissions:read'), (50, 1, 'role_permissions:create'), (51, 1, 'role_permissions:update'), (52, 1, 'role_permissions:delete'),
(53, 1, 'organization_memberships:read'), (54, 1, 'organization_memberships:create'), (55, 1, 'organization_memberships:update'), (56, 1, 'organization_memberships:delete'),
(57, 1, 'tenants:read'), (58, 1, 'tenants:create'), (59, 1, 'tenants:update'), (60, 1, 'tenants:delete'),
-- officer (role 2) — full CRUD on the member lifecycle; read elsewhere
(61, 2, 'members:read'), (62, 2, 'members:create'), (63, 2, 'members:update'), (64, 2, 'members:delete'),
(65, 2, 'households:read'), (66, 2, 'households:create'), (67, 2, 'households:update'), (68, 2, 'households:delete'),
(69, 2, 'household_members:read'), (70, 2, 'household_members:create'), (71, 2, 'household_members:update'), (72, 2, 'household_members:delete'),
(73, 2, 'membership_plans:read'), (74, 2, 'membership_plans:create'), (75, 2, 'membership_plans:update'), (76, 2, 'membership_plans:delete'),
(77, 2, 'member_memberships:read'), (78, 2, 'member_memberships:create'), (79, 2, 'member_memberships:update'), (80, 2, 'member_memberships:delete'),
(81, 2, 'dues_invoices:read'), (82, 2, 'dues_payments:read'),
(83, 2, 'events:read'), (84, 2, 'event_rsvps:read'), (85, 2, 'event_attendance:read'),
(86, 2, 'audit_log:read'), (87, 2, 'roles:read'), (88, 2, 'role_permissions:read'),
(89, 2, 'organization_memberships:read'), (90, 2, 'organization_memberships:update'), (91, 2, 'tenants:read'),
-- event_manager (role 3) — full CRUD on events; read elsewhere
(92, 3, 'events:read'), (93, 3, 'events:create'), (94, 3, 'events:update'), (95, 3, 'events:delete'),
(96, 3, 'event_rsvps:read'), (97, 3, 'event_rsvps:create'), (98, 3, 'event_rsvps:update'), (99, 3, 'event_rsvps:delete'),
(100, 3, 'event_attendance:read'), (101, 3, 'event_attendance:create'), (102, 3, 'event_attendance:update'), (103, 3, 'event_attendance:delete'),
(104, 3, 'members:read'), (105, 3, 'households:read'), (106, 3, 'household_members:read'),
(107, 3, 'membership_plans:read'), (108, 3, 'member_memberships:read'),
(109, 3, 'dues_invoices:read'), (110, 3, 'dues_payments:read'),
(111, 3, 'audit_log:read'), (112, 3, 'roles:read'), (113, 3, 'role_permissions:read'),
(114, 3, 'organization_memberships:read'), (115, 3, 'tenants:read'),
-- finance_manager (role 4) — create/update dues; read elsewhere
(116, 4, 'dues_invoices:read'), (117, 4, 'dues_invoices:create'), (118, 4, 'dues_invoices:update'),
(119, 4, 'dues_payments:read'), (120, 4, 'dues_payments:create'), (121, 4, 'dues_payments:update'),
(122, 4, 'members:read'), (123, 4, 'households:read'), (124, 4, 'household_members:read'),
(125, 4, 'membership_plans:read'), (126, 4, 'member_memberships:read'),
(127, 4, 'events:read'), (128, 4, 'event_rsvps:read'), (129, 4, 'event_attendance:read'),
(130, 4, 'audit_log:read'), (131, 4, 'roles:read'), (132, 4, 'role_permissions:read'),
(133, 4, 'organization_memberships:read'), (134, 4, 'tenants:read'),
-- member (role 5) — read-all plus self-service event RSVPs
(135, 5, 'members:read'), (136, 5, 'households:read'), (137, 5, 'household_members:read'),
(138, 5, 'membership_plans:read'), (139, 5, 'member_memberships:read'),
(140, 5, 'dues_invoices:read'), (141, 5, 'dues_payments:read'),
(142, 5, 'events:read'),
(143, 5, 'event_rsvps:read'), (144, 5, 'event_rsvps:create'), (145, 5, 'event_rsvps:update'),
(146, 5, 'event_attendance:read'),
(147, 5, 'audit_log:read'), (148, 5, 'roles:read'), (149, 5, 'role_permissions:read'),
(150, 5, 'organization_memberships:read'), (151, 5, 'tenants:read'),
-- read_only (role 6) — read on every MM table; no mutations (15)
(152, 6, 'members:read'), (153, 6, 'households:read'), (154, 6, 'household_members:read'),
(155, 6, 'membership_plans:read'), (156, 6, 'member_memberships:read'),
(157, 6, 'dues_invoices:read'), (158, 6, 'dues_payments:read'),
(159, 6, 'events:read'), (160, 6, 'event_rsvps:read'), (161, 6, 'event_attendance:read'),
(162, 6, 'audit_log:read'), (163, 6, 'roles:read'), (164, 6, 'role_permissions:read'),
(165, 6, 'organization_memberships:read'), (166, 6, 'tenants:read');

-- App users — staff/admins who log in (tenant-scoped) (5)
INSERT INTO app_users (user_id, tenant_id, email, display_name, is_active, created_at) VALUES
(1, 1, 'manager@riverside-tennis.example', 'Tina Reed', 1, '2023-01-15 09:05:00'),
(2, 1, 'admin@riverside-tennis.example', 'Omar Vance', 1, '2023-01-18 11:00:00'),
(3, 2, 'manager@summit-hiking.example', 'Greta Holm', 1, '2023-04-02 10:35:00'),
(4, 3, 'manager@harbor-sailing.example', 'Pavel Marsh', 1, '2022-11-20 08:20:00'),
(5, 3, 'admin@harbor-sailing.example', 'Lena Cruz', 1, '2022-12-01 09:30:00');

-- Organization memberships — staff roles within a club (tenant-scoped) (5)
-- role_id references the six-role catalog: 1=admin, 2=officer.
INSERT INTO organization_memberships (membership_id, tenant_id, user_id, role_id, status, joined_at) VALUES
(1, 1, 1, 1, 'active', '2023-01-15 09:05:00'),
(2, 1, 2, 2, 'active', '2023-01-18 11:00:00'),
(3, 2, 3, 1, 'active', '2023-04-02 10:35:00'),
(4, 3, 4, 1, 'active', '2022-11-20 08:20:00'),
(5, 3, 5, 2, 'active', '2022-12-01 09:30:00');

-- ============================================================
-- Membership domain
-- ============================================================

-- Households (tenant-scoped) (5)
INSERT INTO households (household_id, tenant_id, name, address_line1, city, region, postal_code, created_at) VALUES
(1, 1, 'Carter Household', '12 Oak Lane', 'Riverside', 'CA', '92501', '2023-02-01 10:00:00'),
(2, 1, 'Singh Household', '88 Maple Ave', 'Riverside', 'CA', '92503', '2023-02-10 14:00:00'),
(3, 2, 'Holm Household', '5 Trailhead Rd', 'Summit', 'CO', '80498', '2023-04-10 09:00:00'),
(4, 3, 'Marsh Household', '3 Dockside Way', 'Harbor', 'WA', '98110', '2023-01-05 11:00:00'),
(5, 3, 'Cruz Household', '21 Pier Street', 'Harbor', 'WA', '98110', '2023-01-12 16:00:00');

-- Members (tenant-scoped, soft-delete via deleted_at) (8)
-- Member 4 (Diane Carter) is soft-deleted to exercise the soft-delete rule.
INSERT INTO members (member_id, tenant_id, user_id, household_id, first_name, last_name, email, phone, status, joined_on, deleted_at) VALUES
(1, 1, 1, 1, 'Tina', 'Reed', 'tina@riverside-tennis.example', '555-0101', 'active', '2023-01-15 09:05:00', NULL),
(2, 1, NULL, 1, 'Mike', 'Carter', 'mike.carter@example.com', '555-0102', 'active', '2023-02-01 10:05:00', NULL),
(3, 1, NULL, 2, 'Anita', 'Singh', 'anita.singh@example.com', '555-0103', 'active', '2023-02-10 14:05:00', NULL),
(4, 1, NULL, 1, 'Diane', 'Carter', 'diane.carter@example.com', '555-0104', 'resigned', '2023-02-01 10:06:00', '2024-06-30 12:00:00'),
(5, 2, 3, 3, 'Greta', 'Holm', 'greta@summit-hiking.example', '555-0201', 'active', '2023-04-02 10:35:00', NULL),
(6, 2, NULL, 3, 'Ben', 'Holm', 'ben.holm@example.com', '555-0202', 'active', '2023-04-10 09:05:00', NULL),
(7, 3, 4, 4, 'Pavel', 'Marsh', 'pavel@harbor-sailing.example', '555-0301', 'active', '2022-11-20 08:20:00', NULL),
(8, 3, NULL, 5, 'Lena', 'Cruz', 'lena.cruz@example.com', '555-0302', 'active', '2023-01-12 16:05:00', NULL);

-- Household members (tenant-scoped) (8)
INSERT INTO household_members (household_member_id, tenant_id, household_id, member_id, relationship) VALUES
(1, 1, 1, 2, 'head'),
(2, 1, 1, 4, 'spouse'),
(3, 1, 1, 1, 'member'),
(4, 1, 2, 3, 'head'),
(5, 2, 3, 5, 'head'),
(6, 2, 3, 6, 'child'),
(7, 3, 4, 7, 'head'),
(8, 3, 5, 8, 'head');

-- Membership plans (tenant-scoped) (6)
INSERT INTO membership_plans (plan_id, tenant_id, name, description, billing_period, price_cents, is_active) VALUES
(1, 1, 'Individual', 'Single-person annual membership', 'annual', 24000, 1),
(2, 1, 'Family', 'Household annual membership', 'annual', 40000, 1),
(3, 2, 'Standard', 'Annual hiking association membership', 'annual', 8000, 1),
(4, 2, 'Student', 'Discounted annual membership for students', 'annual', 4000, 1),
(5, 3, 'Crew', 'Annual sailing crew membership', 'annual', 60000, 1),
(6, 3, 'Skipper', 'Annual membership with berth privileges', 'annual', 120000, 1);

-- Member memberships (tenant-scoped) (7)
INSERT INTO member_memberships (member_membership_id, tenant_id, member_id, plan_id, start_date, end_date, status) VALUES
(1, 1, 1, 1, '2024-01-01', '2024-12-31', 'active'),
(2, 1, 2, 2, '2024-01-01', '2024-12-31', 'active'),
(3, 1, 3, 1, '2024-03-01', '2025-02-28', 'active'),
(4, 2, 5, 3, '2024-01-01', '2024-12-31', 'active'),
(5, 2, 6, 4, '2024-01-01', '2024-12-31', 'active'),
(6, 3, 7, 6, '2024-01-01', '2024-12-31', 'active'),
(7, 3, 8, 5, '2024-02-01', '2025-01-31', 'active');

-- Dues invoices (tenant-scoped) (7)
INSERT INTO dues_invoices (invoice_id, tenant_id, member_id, member_membership_id, amount_cents, issued_on, due_on, status) VALUES
(1, 1, 1, 1, 24000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(2, 1, 2, 2, 40000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(3, 1, 3, 3, 24000, '2024-03-01 00:00:00', '2024-03-31', 'open'),
(4, 2, 5, 4, 8000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(5, 2, 6, 5, 4000, '2024-01-01 00:00:00', '2024-01-31', 'open'),
(6, 3, 7, 6, 120000, '2024-01-01 00:00:00', '2024-01-31', 'paid'),
(7, 3, 8, 7, 60000, '2024-02-01 00:00:00', '2024-02-29', 'open');

-- Dues payments (tenant-scoped) (4)
INSERT INTO dues_payments (payment_id, tenant_id, invoice_id, amount_cents, paid_on, method) VALUES
(1, 1, 1, 24000, '2024-01-10 09:00:00', 'card'),
(2, 1, 2, 40000, '2024-01-12 14:00:00', 'bank'),
(3, 2, 4, 8000, '2024-01-08 11:00:00', 'card'),
(4, 3, 6, 120000, '2024-01-15 10:00:00', 'bank');

-- Events (tenant-scoped) (4)
INSERT INTO events (event_id, tenant_id, title, description, location, starts_at, ends_at, capacity, created_at) VALUES
(1, 1, 'Spring Doubles Tournament', 'Annual members doubles tournament', 'Riverside Courts', '2024-05-18 09:00:00', '2024-05-18 17:00:00', 32, '2024-04-01 10:00:00'),
(2, 1, 'New Member Mixer', 'Welcome social for new members', 'Clubhouse', '2024-06-05 18:00:00', '2024-06-05 20:00:00', 50, '2024-05-01 10:00:00'),
(3, 2, 'Summit Ridge Day Hike', 'Guided group hike on the ridge trail', 'Summit Trailhead', '2024-06-15 07:00:00', '2024-06-15 15:00:00', 20, '2024-05-10 09:00:00'),
(4, 3, 'Harbor Regatta', 'Club regatta and awards dinner', 'Harbor Marina', '2024-07-20 08:00:00', '2024-07-20 21:00:00', 40, '2024-06-01 09:00:00');

-- Event RSVPs (tenant-scoped) (6)
INSERT INTO event_rsvps (rsvp_id, tenant_id, event_id, member_id, response, guests, responded_at) VALUES
(1, 1, 1, 1, 'yes', 0, '2024-04-10 12:00:00'),
(2, 1, 1, 2, 'yes', 1, '2024-04-11 09:00:00'),
(3, 1, 2, 3, 'maybe', 0, '2024-05-05 15:00:00'),
(4, 2, 3, 5, 'yes', 0, '2024-05-12 08:00:00'),
(5, 2, 3, 6, 'yes', 0, '2024-05-12 08:05:00'),
(6, 3, 4, 7, 'yes', 2, '2024-06-05 10:00:00');

-- Event attendance (tenant-scoped) (4)
INSERT INTO event_attendance (attendance_id, tenant_id, event_id, member_id, checked_in_at) VALUES
(1, 1, 1, 1, '2024-05-18 08:45:00'),
(2, 1, 1, 2, '2024-05-18 08:50:00'),
(3, 2, 3, 5, '2024-06-15 06:55:00'),
(4, 2, 3, 6, '2024-06-15 06:57:00');
