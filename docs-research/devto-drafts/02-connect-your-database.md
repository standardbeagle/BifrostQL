---
title: "Connecting your database: SQL Server, Postgres, MySQL, SQLite"
published: false
description: "The package, provider registration, and connection string for each of BifrostQL's four SQL engines — and the one line that decides whether your host starts at all."
tags: dotnet, database, graphql, postgres
canonical_url: https://dev.standardbeagle.com/BifrostQL/getting-started/connect-a-database/
series: BifrostQL quickstarts
---

Switching a BifrostQL host from one SQL engine to another touches three things:
one NuGet package, one registration line, and one connection string. Nothing
else in the application changes, and the GraphQL surface your clients query stays
identical.

Here is the whole switch, driven entirely from the environment against a running
host:

```bash
ConnectionStrings__bifrost="Data Source=blog.db" \
BifrostQL__Provider=sqlite \
ASPNETCORE_URLS=http://127.0.0.1:5301 \
dotnet run --project src/BifrostQL.Host --framework net10.0 --no-launch-profile
```

```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://127.0.0.1:5301
```

```bash
curl -s -X POST http://127.0.0.1:5301/graphql \
  -H 'Content-Type: application/json' \
  -d '{"query":"{ authors(limit: 3, sort: [name_asc]) { data { author_id name } total } }"}'
```

```json
{"data":{"authors":{"data":[
  {"author_id":7,"name":"Amara Okafor"},
  {"author_id":10,"name":"Carlos Mendez"},
  {"author_id":6,"name":"David Kim"}],"total":10}}}
```

That run is SQLite. The three server engines take the same three steps with
different values, listed below.

## 1. The package per engine

Every project needs `BifrostQL.Server` plus exactly one dialect package —
or several, if one host serves several databases.

| Database | Package | Driver it pulls in |
|---|---|---|
| SQL Server | `BifrostQL.SqlServer` | Microsoft.Data.SqlClient |
| PostgreSQL | `BifrostQL.Ngsql` | Npgsql |
| MySQL / MariaDB | `BifrostQL.MySql` | MySqlConnector |
| SQLite | `BifrostQL.Sqlite` | Microsoft.Data.Sqlite |

```bash
dotnet add package BifrostQL.Server
dotnet add package BifrostQL.Ngsql
```

The PostgreSQL package name is `Ngsql`, after Npgsql. It is the one people
mistype.

## 2. Register the provider

This is the step that decides whether the host starts. Dialect packages do not
self-register — there is no module initializer that runs when the assembly
loads, so a `PackageReference` alone has no runtime effect. Call
`DbConnFactoryResolver.Register` before the host is built:

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.Ngsql;

DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));

var app = builder.Build();
app.UseBifrostQL();
await app.RunAsync();
```

Skip it and startup throws `No factory registered for provider '…'`.

The four registrations:

| Database | Namespace | Registration |
|---|---|---|
| SQL Server | `BifrostQL.SqlServer` | `DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));` |
| PostgreSQL | `BifrostQL.Ngsql` | `DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));` |
| MySQL | `BifrostQL.MySql` | `DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));` |
| SQLite | `BifrostQL.Sqlite` | `DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));` |

An explicit call reads like boilerplate until you need two engines at once.
Registration is a map from provider to factory, so a host can register all four
and let configuration pick. BifrostQL's own reference host does exactly that:

```csharp
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));
DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));
```

That host is the reason the SQLite run at the top of this article needed no code
edit at all — only environment variables.

## 3. The connection string

`BindStandardConfig` reads the connection string from `ConnectionStrings:bifrost`
and the rest from the `BifrostQL` section:

```json
{
  "ConnectionStrings": {
    "bifrost": "Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=xxx"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "DisableAuth": true,
    "Provider": "postgres"
  }
}
```

| Database | Connection string |
|---|---|
| SQL Server | `Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True` |
| PostgreSQL | `Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=xxx` |
| MySQL | `Server=localhost;Port=3306;Database=mydb;User=root;Password=xxx` |
| SQLite | `Data Source=app.db` |

Both keys accept the standard .NET environment-variable override, which is how
the run at the top of this article was configured:
`ConnectionStrings__bifrost` and `BifrostQL__Provider`. Double underscore is the
section separator. This keeps credentials out of `appsettings.json` and lets one
built image target a different database per deployment.

`TrustServerCertificate=True` on the SQL Server string accepts whatever
certificate the server presents. It is a local-development setting; read what it
costs before carrying it into anything shared.

### Naming the provider

`BifrostQL:Provider` is optional. When it is absent, BifrostQL infers the engine
from the connection string and throws rather than guessing when the string
matches nothing. Set it when a string is ambiguous, or when you want the failure
at configuration time instead of first query. The parser accepts:

| Engine | Accepted values |
|---|---|
| SQL Server | `sqlserver`, `mssql` |
| PostgreSQL | `postgresql`, `postgres`, `npgsql`, `pgsql` |
| MySQL | `mysql`, `mariadb` |
| SQLite | `sqlite` |

Anything else throws with the supported values in the message.

## 4. Confirm the connection

Start the host and read from any table. The field name follows your table name,
and rows come back inside `data`:

```graphql
{
  authors(limit: 3, sort: [name_asc]) {
    data { author_id name email }
    total
  }
}
```

Rows coming back proves three separate things at once: the connection string
parsed and connected, the provider registration resolved to a factory, and the
schema read produced a GraphQL type. A failure at any of those three stops
startup or the first query with a distinct message, so you rarely have to guess
which one broke.

The three failures read as follows. A missing registration throws
`No factory registered for provider '…'` — the package is referenced but nothing
called `Register`. An unrecognized `Provider` value throws with the supported
names listed in the message, which means a typo like `postgress` never reaches
the driver. A connection that cannot be opened fails inside the driver itself,
so a bad password or an unreachable host is diagnosed the same way it would be
in any other .NET application on that driver. Only the first two are specific to
BifrostQL, and the first is the one a new project hits.

The playground sits at whatever `BifrostQL:Playground` names — with
`"Playground": "/graphiql"` the host answers 200 at `/graphiql`, and 404 at any
other path, including the default from a different config file.

## What was and was not run here

The SQLite path above was executed end to end: a `blog.db` built from the
repository's blog schema and sample seed, the reference host started against it
on `127.0.0.1:5301`, and the query answered with 10 authors.

The SQL Server, PostgreSQL, and MySQL rows in the tables above come from the
shipped source — the four `DbConnFactoryResolver.Register` calls in the
reference host's `Program.cs`, the driver `PackageReference` in each dialect
project, and the provider-name parser in `DbConnFactory.cs`. No server instance
of those three was running on the machine used for this article, so treat their
connection-string shapes as the documented forms rather than as observed output.

## Where the engines actually differ

The GraphQL surface is the same across all four. The SQL underneath is not:
identifier quoting (`[name]`, `"name"`, `` `name` ``), paging syntax, string
concatenation (`+`, `||`, `CONCAT()`), full-text search, and upsert strategy each
have a per-engine spelling. That is the dialect layer's job, and it is the reason
moving from SQLite in development to Postgres in production is a configuration
change rather than a query rewrite.

## Takeaways

- Add the dialect package and the `DbConnFactoryResolver.Register` call together.
  Treating them as one step removes the most common startup failure.
- Register every engine your host might target; configuration picks at runtime,
  and the unused registrations cost a delegate each.
- Set `BifrostQL:Provider` explicitly rather than relying on inference, so a
  malformed connection string fails during configuration.
- Feed connection strings through `ConnectionStrings__bifrost` in deployment and
  keep credentials out of the repository.
- When the first query fails, read which of the three stages reported it —
  connection, factory resolution, or schema read — before changing anything.
- Develop against SQLite for speed, but run your test suite against the
  production engine before release; the dialect layer is where the behavior
  differences live.
