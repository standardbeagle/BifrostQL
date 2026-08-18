---
title: "8 Ready-Made App Backends You Can Query in 60 Seconds"
published: false
description: "BifrostQL ships eight seeded SQLite schemas — blog, CRM, ecommerce, project tracker, and four more. Point the server at one and you get a full GraphQL API with joins, filters, and paging, no resolvers written."
tags: graphql, database, dotnet, webdev
canonical_url: https://dev.standardbeagle.com/BifrostQL/getting-started/app-schemas/
series: BifrostQL quickstarts
---

Here is a blog backend, from nothing, in two commands.

```bash
sqlite3 blog.db < src/BifrostQL.UI/Schemas/blog.sql
sqlite3 blog.db < src/BifrostQL.UI/Schemas/blog-seed-sample.sql
```

Start BifrostQL against that file and ask it for published posts, with each
post's author, its category, and its first two comments:

```graphql
{
  posts(limit: 2, sort: [post_id_asc], filter: { status: { _eq: "published" } }) {
    total
    data {
      post_id title status
      authors { name }
      categories { name }
      comments(limit: 2) { data { author_name } }
    }
  }
}
```

```json
{
  "posts": {
    "total": 45,
    "data": [
      {
        "post_id": 1,
        "title": "Getting Started with GraphQL in .NET",
        "status": "published",
        "authors": { "name": "Sarah Chen" },
        "categories": { "name": "Web Development" },
        "comments": { "data": [
          { "author_name": "Alex Turner" },
          { "author_name": "Mia Foster" }
        ] }
      },
      {
        "post_id": 2,
        "title": "Why I Switched from REST to GraphQL",
        "status": "published",
        "authors": { "name": "Sarah Chen" },
        "categories": { "name": "Web Development" },
        "comments": { "data": [ { "author_name": "Jordan Lee" } ] }
      }
    ]
  }
}
```

No schema file, no resolvers, no type definitions. BifrostQL read the SQLite
catalog, found the foreign keys, and generated the types, the join fields, the
filter arguments, and the sort enum. `authors` and `categories` are singular
because those foreign keys point one way; `comments` came back paged because
that one points the other way.

Eight of these schemas ship in the box.

## Running one yourself

The schemas live in `src/BifrostQL.UI/Schemas/` as plain SQLite DDL with foreign
keys enforced. Each has a `<name>.sql` for structure and a
`<name>-seed-sample.sql` for data. Apply both with the `sqlite3` CLI, then point
the reference server at the file:

```bash
ASPNETCORE_URLS=http://127.0.0.1:5303 \
  dotnet run --project src/BifrostQL.Host --framework net10.0 -- \
  --BifrostQL:Provider=sqlite \
  --ConnectionStrings:bifrost="Data Source=$PWD/blog.db"
```

Then POST to `http://127.0.0.1:5303/graphql`. Everything below came out of that
loop — four schemas booted in turn on the same port, each query run with `curl`.

Inside the desktop app the same thing happens without the CLI: open `bifrostui`
with no connection string and the Quick Start screen offers cards for `blog`,
`ecommerce`, `crm`, `classroom`, and `project-tracker`. Pick a schema, pick
`sample` or `full`, and the app writes a fresh database to your temp directory
and connects.

## The eight

| Schema | Tables | What it models |
|---|---|---|
| `blog` | 6 | Authors, posts, categories, comments, many-to-many tags |
| `classroom` | 6 | Instructors, courses, students, enrollments, assignments, submissions |
| `crm` | 6 | Companies, contacts, deals, stages, activities, polymorphic notes |
| `ecommerce` | 7 | Catalog, customers, addresses, orders, order items, reviews |
| `project-tracker` | 8 | Workspaces, projects, sections, tasks, labels, multi-assignee assignments |
| `org-model` | 7 | Tenants, roles, permissions, users, memberships, invitations, audit log |
| `membership-manager` | 16 | Clubs on the org model: households, plans, dues, events, attendance |
| `sqlite-advanced` | 8 | IoT sensor telemetry, plus a generated column and a view |

Four of them are worth walking through, because each one exercises a different
part of the schema reader.

## `crm` — soft deletes and a pipeline

The CRM models a sales pipeline. `deals` carries `deleted_at`, so the sample
data includes rows that should never reach a client:

```graphql
{
  deals(limit: 3, sort: [value_desc], filter: { deleted_at: { _null: true } }) {
    total
    data {
      title value probability
      deal_stages { name }
      companies { name industry }
      contacts { first_name last_name }
    }
  }
}
```

```json
{
  "deals": {
    "total": 30,
    "data": [
      { "title": "RedStone SCADA System", "value": 600000, "probability": 0.25,
        "deal_stages": { "name": "Qualified" },
        "companies": { "name": "RedStone Energy", "industry": "Energy" },
        "contacts": { "first_name": "Richard", "last_name": "Hayes" } },
      { "title": "Orbit Financial Trading Platform", "value": 500000, "probability": 0.5,
        "deal_stages": { "name": "Proposal" },
        "companies": { "name": "Orbit Financial", "industry": "Finance" },
        "contacts": { "first_name": "Catherine", "last_name": "Park" } }
    ]
  }
}
```

`total` is 30 against a table holding 33 rows — three deals carry a
`deleted_at`, and the filter removed them from the count as well as the page.

That filter is written by hand here. The CRM also ships `crm.bifrost.json`, a
profile that declares `*.deals { soft-delete: deleted_at }` plus the polymorphic
mapping for `notes` (`entity_type` / `entity_id` resolving to companies,
contacts, or deals) and hidden timestamp columns. Apply the profile and the
soft-delete filter becomes server-side and unskippable rather than something
every caller remembers. The desktop app applies it when it creates the database.

## `ecommerce` — two foreign keys to one table

`orders` points at `addresses` twice, for shipping and for billing. A naive
generator collapses those into one field or gives up. BifrostQL names them after
the columns:

```graphql
{
  orders(limit: 1, sort: [total_desc]) {
    data {
      order_id status total
      customers { email }
      shipping_address { city state }
      billing_address { city state }
      order_items(limit: 2) { data { quantity unit_price products { name } } }
    }
  }
}
```

```json
{
  "order_id": 10,
  "status": "processing",
  "total": 1673.98,
  "customers": { "email": "grace.hernandez@example.com" },
  "shipping_address": { "city": "Miami", "state": "FL" },
  "billing_address": null,
  "order_items": { "data": [
    { "quantity": 1, "unit_price": 1499.99, "products": { "name": "GameStation X Laptop" } },
    { "quantity": 1, "unit_price": 49.99, "products": { "name": "Slim Fit Chinos" } }
  ] }
}
```

The `null` billing address is honest seed data: 21 of the 30 sample orders leave
`billing_address_id` unset, the way real checkout flows do when the customer
ticks "same as shipping". The join follows the nullable FK and returns null
rather than dropping the order from the result.

## `project-tracker` — self-joins and many-to-many

`tasks` has a `parent_task_id` pointing back at itself, and reaches `labels`
through a `task_labels` link table. Both come out as fields:

```graphql
{
  tasks(limit: 1, sort: [task_id_asc], filter: { parent_task_id: { _null: true } }) {
    total
    data {
      task_id title status
      projects { name }
      tasks_children(limit: 3) { total data { title status } }
      labels { data { name } }
    }
  }
}
```

```json
{
  "total": 34,
  "data": [{
    "task_id": 1,
    "title": "Design new homepage layout",
    "status": "in_progress",
    "projects": { "name": "Website Redesign" },
    "tasks_children": { "total": 3, "data": [
      { "title": "Create wireframe sketches", "status": "done" },
      { "title": "Design hero section mockup", "status": "in_progress" },
      { "title": "Design content sections", "status": "todo" }
    ] },
    "labels": { "data": [ { "name": "Design" }, { "name": "Frontend" } ] }
  }]
}
```

Two things to notice. The self-referencing key produces a pair of fields —
`tasks` walks up to the parent, `tasks_children` walks down to the subtasks — so
one column gives you both directions of a tree. And `labels` is flattened
through `task_labels`: the link table still exists as its own type if you want
it, but the useful traversal is generated for you.

## The other four

- **`classroom`** (6 tables) — instructors, courses, students, enrollments,
  assignments, submissions. Two link tables and a grading chain; the closest
  thing here to a normalized textbook schema.
- **`org-model`** (7 tables) — the multi-tenant foundation. Every table carries
  `tenant_id` with `ON DELETE CASCADE`, alongside roles, role permissions,
  invitations, and an audit log. Read this one first if you are designing for
  tenancy.
- **`membership-manager`** (16 tables) — the largest, built on `org-model`. Adds
  households, members, plans, dues invoices and payments, events, RSVPs, and
  attendance across 30 foreign keys.
- **`sqlite-advanced`** (8 tables) — sensor telemetry that deliberately stresses
  the reader: a `GENERATED ALWAYS ... STORED` column deriving a timestamp from an
  epoch integer, and a `sensor_reading_stats` view. The fastest way to see how
  both surface in GraphQL.

## Seed sizes

Five schemas ship both a `sample` and a `full` seed: `blog`, `classroom`, `crm`,
`ecommerce`, and `project-tracker`. Sample seeds are a few hundred rows total —
the blog sample is 10 authors, 50 posts, 50 comments, 67 post-tag links. Full
seeds run to thousands of rows, which is what you want for testing paging,
grouping, and aggregates. `org-model`, `membership-manager`, and
`sqlite-advanced` ship sample only; asking for a size that does not exist
installs the DDL and tells you so.

`org-model` and `membership-manager` also ship self-contained PostgreSQL
scripts, DDL and data in one file. Run those with `psql` and connect to the
result as an ordinary Postgres database.

## Where to take it

Each of these is a working backend the moment the file exists. Point the desktop
explorer at one to browse it, run the same queries through the workbench, or
copy a schema into your own project as a starting shape. The CRM profile is the
one to read next — it is the shortest example of turning a column convention
into enforced server behavior.
