---
title: "Ready-Made Database Schemas to Start From"
description: "Eight ready-made database schemas ship with the BifrostQL desktop app: blog, CRM, ecommerce, classroom, project tracker, membership, org model, and IoT sensors."
---

Eight ready-made database schemas ship inside the BifrostQL desktop app. Each one
creates a populated SQLite database in a few seconds, so you can see generated
queries, joins, and mutations against a realistic shape before pointing BifrostQL
at a database of your own. They also serve as reference designs — the CRM schema
shows polymorphic notes, the org model shows tenancy.

Every schema is SQLite DDL with foreign keys enforced.

## The schemas

| Schema | Tables | What it models |
|---|---|---|
| `blog` | 6 | Authors, posts, categories, comments, and many-to-many tags |
| `classroom` | 6 | Instructors, courses, students, enrollments, assignments, submissions |
| `crm` | 6 | Sales pipeline: companies, contacts, deals, stages, activities, polymorphic notes |
| `ecommerce` | 7 | Catalog, customers, addresses, orders, order items, reviews |
| `project-tracker` | 8 | Workspaces, projects, sections, tasks, labels, multi-assignee assignments |
| `org-model` | 7 | Multi-tenant foundation: tenants, roles, permissions, users, memberships, invitations, audit log |
| `membership-manager` | 16 | Club and association management on the org model: households, members, plans, dues invoices and payments, events, RSVPs, attendance |
| `sqlite-advanced` | 8 | IoT sensor telemetry, and a workout for SQLite features — a generated column and a view |

`org-model` is the foundation `membership-manager` builds on. Read it first if
you are designing a multi-tenant application; the same shape is documented in the
[multi-tenant organization data model](/BifrostQL/guides/org-model/) guide.

`sqlite-advanced` exists to exercise the schema reader. It carries a
`GENERATED ALWAYS` column and a `CREATE VIEW`, so it is the fastest way to see how
BifrostQL surfaces both.

## Seed data

Every schema installs its DDL. Seed data is separate, and how much you get
depends on the schema:

| Schema | `sample` seed | `full` seed |
|---|---|---|
| `blog` | yes | yes |
| `classroom` | yes | yes |
| `crm` | yes | yes |
| `ecommerce` | yes | yes |
| `project-tracker` | yes | yes |
| `membership-manager` | yes | — |
| `org-model` | yes | — |
| `sqlite-advanced` | yes | — |

`sample` seeds are a few hundred rows — enough to read a grid and follow a join.
`full` seeds run to thousands of rows across every table, which is what you want
when testing paging, grouping, aggregates, or chart cardinality.

A schema with no seed for the size you asked for installs the DDL and says so.
It does not fail.

### PostgreSQL variants

`org-model` and `membership-manager` also ship PostgreSQL scripts:
`org-model-postgres-seed-sample.sql` and
`membership-manager-postgres-seed-sample.sql`. These are self-contained — DDL and
data in one file — so you run them against a Postgres database yourself with
`psql`, then point BifrostQL at it as an ordinary connection. They are not
reachable from the desktop app's Quick Start.

## Creating one

The schemas live inside the desktop app as embedded resources. Build it to get a
`bifrostui` binary — see the [desktop database explorer](/BifrostQL/guides/desktop-app/)
guide — or run `./dev-ui.sh` from a repository checkout.

Open the app without a connection string and the Quick Start screen offers
`blog`, `ecommerce`, `crm`, `classroom`, and `project-tracker` as cards. Pick one,
pick `sample` or `full`, and the app writes a fresh SQLite file to your temp
directory, applies the DDL, applies the seed, and connects.

The remaining three — `org-model`, `membership-manager`, and `sqlite-advanced` —
have no card yet. They are created through the same endpoint the cards call:

```bash
curl -N -X POST http://localhost:5000/api/database/create-quickstart \
  -H 'Content-Type: application/json' \
  -d '{"schema":"membership-manager","dataSize":"sample"}'
```

The response streams progress as server-sent events. Use the port the desktop app
is serving on.

## Profiles

A schema may ship a profile document that configures BifrostQL beyond the DDL.
`crm` does: `crm.bifrost.json` declares a `showcase` profile carrying schema
metadata rules — the polymorphic mapping for notes, `soft-delete: deleted_at` on
deals, and hidden timestamp columns — plus the `polymorphic` and `soft-delete`
modules. The profile is applied when the database is created, so the CRM sample
demonstrates those features working rather than merely having the columns for them.

This is the ordinary metadata surface, not a schema-only feature. See
[cross-cutting modules and transformers](/BifrostQL/guides/modules/) for what else
you can declare the same way.

## Next steps

- [Connect a database](/BifrostQL/getting-started/connect-a-database/) — point BifrostQL at your own
- [Automatic table joins](/BifrostQL/guides/joins/) — the relationships these schemas exercise
- [Headless CRUD admin](/BifrostQL/guides/workbench/) — browse any of these in the data workbench
- [Multi-tenant organization data model](/BifrostQL/guides/org-model/) — the pattern behind `org-model`
