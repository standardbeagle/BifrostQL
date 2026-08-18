---
title: "GraphQL API from any SQL database in 5 minutes"
published: false
description: "Point BifrostQL at a SQLite blog database, start the host, and get a working GraphQL API with filters, joins, and mutations — no schema file, no codegen."
tags: graphql, dotnet, database, tutorial
canonical_url: https://dev.standardbeagle.com/BifrostQL/getting-started/
series: BifrostQL quickstarts
---

Here is the whole payoff first. A SQLite file with a blog schema in it, a 20-line
`Program.cs`, and this request:

```bash
curl -s -X POST http://127.0.0.1:5301/graphql \
  -H 'Content-Type: application/json' \
  -d '{"query":"{ authors(limit: 3, sort: [name_asc]) { data { author_id name email } total } }"}'
```

```json
{
  "data": {
    "authors": {
      "data": [
        { "author_id": 7,  "name": "Amara Okafor",  "email": "amara.o@example.com" },
        { "author_id": 10, "name": "Carlos Mendez", "email": "carlos.m@example.com" },
        { "author_id": 6,  "name": "David Kim",     "email": "david.kim@example.com" }
      ],
      "total": 10
    }
  }
}
```

No `.graphql` file was written and no code was generated. BifrostQL read the
database schema at startup and built the API from it. Every table became a query
field, every foreign key became a nested field, and every table got a mutation.

Below is the complete path from an empty directory to that response.

## 1. Get a database

BifrostQL ships sample schemas. The blog one has six tables and enough rows to
make joins interesting:

```bash
sqlite3 blog.db < src/BifrostQL.UI/Schemas/blog.sql
sqlite3 blog.db < src/BifrostQL.UI/Schemas/blog-seed-sample.sql
sqlite3 blog.db "select count(*) from posts;"
```

That gives 10 authors, 8 categories, 50 posts, 15 tags, 67 post-tag rows, and 50
comments. Any SQLite file you already have works the same way — the schema is
the input, so there is nothing to adapt.

## 2. Create the project

```bash
dotnet new web -n MyBifrostApi
cd MyBifrostApi
dotnet add package BifrostQL.Server
dotnet add package BifrostQL.Sqlite
```

`BifrostQL.Server` carries the middleware and the configuration binder.
`BifrostQL.Sqlite` carries the SQLite dialect and pulls in
`Microsoft.Data.Sqlite`. `BifrostQL.Core` arrives transitively; add it
explicitly only when your own code calls Core APIs.

## 3. Write Program.cs

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Sqlite;

DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));

var app = builder.Build();
app.UseBifrostQL();
await app.RunAsync();
```

The `DbConnFactoryResolver.Register` line is the one people skip. Dialect
packages do not register themselves — there is no module initializer that runs
on assembly load, so referencing `BifrostQL.Sqlite` alone changes nothing at
runtime. Startup then fails with `No factory registered for provider …`.

The explicit call buys something in return: one host can register several
dialects and route by configured provider. The reference host in the BifrostQL
repository registers all four for exactly that reason.

`UseBifrostQL` mounts the GraphQL endpoint, the playground, and the
authentication middleware in one call.

## 4. Configure

```json
{
  "ConnectionStrings": {
    "bifrost": "Data Source=blog.db"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "DisableAuth": true,
    "Provider": "sqlite"
  }
}
```

`BindStandardConfig` reads the connection string from `ConnectionStrings:bifrost`
and everything else from the `BifrostQL` section. Two of these settings deserve
a note.

`DisableAuth: true` suits a first run on a loopback port and nothing else.
Authentication is on by default, and without this flag `BindStandardConfig`
requires a `JwtSettings` section and throws when it is missing. Turn it back on
before the endpoint is reachable by anyone else.

`Provider` is optional. BifrostQL infers the engine from the connection string
when the key is absent, and throws rather than guessing when the string matches
nothing. Setting it explicitly moves that failure to configuration time, which
is where you want it.

## 5. Run

```bash
ASPNETCORE_URLS=http://127.0.0.1:5301 dotnet run
```

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://127.0.0.1:5301
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

The playground is at `http://127.0.0.1:5301/graphiql`, matching the `Playground`
path you configured. The API is at `/graphql`. Binding to `127.0.0.1` rather
than `0.0.0.0` keeps an unauthenticated first run off the network.

That is the point where the query at the top of this article works.

## 6. Follow the foreign keys

Filters, sorting, paging, and joins are all in the generated schema. This one
query reads published posts newest first, resolves each post's author and
category, and pulls two comments per post:

```graphql
{
  posts(limit: 2, offset: 0, sort: [published_at_desc],
        filter: { status: { _eq: "published" } }) {
    total
    data {
      post_id
      title
      published_at
      authors { name }
      categories { name }
      comments(limit: 2) { data { author_name status } }
    }
  }
}
```

```json
{
  "data": {
    "posts": {
      "total": 45,
      "data": [
        {
          "post_id": 45,
          "title": "Introduction to WebRTC",
          "published_at": "2024-10-05 11:00:00",
          "authors": { "name": "Carlos Mendez" },
          "categories": { "name": "Technology" },
          "comments": { "data": [] }
        },
        {
          "post_id": 44,
          "title": "Optimizing React Rendering Performance",
          "published_at": "2024-10-01 09:00:00",
          "authors": { "name": "James Park" },
          "categories": { "name": "Web Development" },
          "comments": {
            "data": [
              { "author_name": "Jordan Lee", "status": "approved" },
              { "author_name": "Sam Brooks", "status": "approved" }
            ]
          }
        }
      ]
    }
  }
}
```

The nested field names come from the referenced tables. `posts.author_id`
references `authors`, so the field is `authors`; the reverse direction gives
`posts` a paged `comments` field. `total` counts rows matching the filter, not
rows returned, so paging controls stay honest.

Introspecting the `posts` type shows what else was generated: the columns, the
join fields, plus `_agg`, `_single`, and `_join`.

The root query type is worth one introspection of its own. Six tables produced
nineteen root fields: `posts`, `postsAggregate`, and `postsPivot` for each table,
plus a `_dbSchema` field describing the model itself. The aggregate type carries
every column for grouping alongside `_count`, `_sum`, `_avg`, `_min`, and `_max`,
so counts and totals stay on the server instead of arriving as 50 rows your
client reduces. The mutation root mirrors the same list with a `_batch` variant
per table.

## 7. Write to it

Each table gets a mutation field with `insert`, `update`, `upsert`, `delete`,
and `sync` arguments:

```graphql
mutation {
  authors(insert: {
    name: "Ada Lovelace",
    email: "ada@example.com",
    bio: "Wrote the first algorithm.",
    created_at: "2026-08-16 12:00:00"
  })
}
```

```json
{ "data": { "authors": 11 } }
```

The return value is the new primary key. The `created_at` argument is there
because of a rule worth knowing before you hit it: a `NOT NULL` column is
required in the insert input type even when the database has a `DEFAULT` for it.
Omitting it fails validation before any SQL runs:

```
Argument 'insert' has invalid value. Missing required field 'created_at' of type 'String'.
```

Metadata handles this permanently. A `populate: created-on` rule on the column
marks it auto-populated, and the schema generator then emits its insert field as
nullable regardless of the `NOT NULL` constraint, because the value is stamped
server-side.

## Takeaways

- Register the dialect factory in `Program.cs`. Adding the NuGet package is not
  enough, and the failure only shows at startup.
- Set `BifrostQL:Provider` explicitly so a bad connection string fails at
  configuration time instead of on first query.
- Bind to `127.0.0.1` while `DisableAuth` is on, and wire `JwtSettings` before
  the port is reachable from anywhere else.
- Introspect the generated types (`__type(name: "posts")`) before writing
  queries — the join field names come from your table names, and guessing wastes
  more time than one introspection query costs.
- Use `total` for pager UI and `limit`/`offset` for the page itself; they answer
  different questions.
- Reach for column metadata (`populate`, `soft-delete`, `audit-table`) as soon as
  insert payloads start carrying bookkeeping columns.

Next: the same host against SQL Server, PostgreSQL, and MySQL, and what changes
between them.
