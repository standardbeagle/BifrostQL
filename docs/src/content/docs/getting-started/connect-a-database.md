---
title: "Connect SQL Server, Postgres, MySQL, SQLite"
description: "Connection strings and host wiring for all four BifrostQL databases, with the provider registration each one needs and a first GraphQL query to prove it works."
---

BifrostQL reads connection strings for SQL Server, PostgreSQL, MySQL, and SQLite,
and serves the same GraphQL API over each. This page gives the package, the
provider registration, the connection string shape, and a query to confirm the
connection works. Every engine follows the same three steps, so switching from
SQLite in development to Postgres in production changes two lines.

If you have not installed BifrostQL yet, start with
[getting started](/BifrostQL/getting-started/).

## 1. Install the packages

Every project needs `BifrostQL.Server` plus exactly one dialect package:

```bash
dotnet add package BifrostQL.Server
dotnet add package BifrostQL.SqlServer   # or BifrostQL.Ngsql / BifrostQL.MySql / BifrostQL.Sqlite
```

`BifrostQL.Core` arrives transitively. Add it explicitly only if you call Core
APIs from your own code.

| Database | Package | Underlying driver |
|---|---|---|
| SQL Server | `BifrostQL.SqlServer` | Microsoft.Data.SqlClient |
| PostgreSQL | `BifrostQL.Ngsql` | Npgsql |
| MySQL / MariaDB | `BifrostQL.MySql` | MySqlConnector |
| SQLite | `BifrostQL.Sqlite` | Microsoft.Data.Sqlite |

## 2. Register the provider

Dialect packages do not register themselves. Call
`DbConnFactoryResolver.Register` once, before the host starts, for the engine you
installed. Skip it and startup throws `No factory registered for provider …`.

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.SqlServer;

DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));

var app = builder.Build();
app.UseBifrostQL();
await app.RunAsync();
```

That is the whole host. `UseBifrostQL` mounts the GraphQL endpoint, the GraphiQL
playground, and the authentication middleware.

Swap the last `using` and the registration line per engine:

| Database | Namespace | Registration |
|---|---|---|
| SQL Server | `BifrostQL.SqlServer` | `DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));` |
| PostgreSQL | `BifrostQL.Ngsql` | `DbConnFactoryResolver.Register(BifrostDbProvider.PostgreSql, cs => new PostgresDbConnFactory(cs));` |
| MySQL | `BifrostQL.MySql` | `DbConnFactoryResolver.Register(BifrostDbProvider.MySql, cs => new MySqlDbConnFactory(cs));` |
| SQLite | `BifrostQL.Sqlite` | `DbConnFactoryResolver.Register(BifrostDbProvider.Sqlite, cs => new SqliteDbConnFactory(cs));` |

Register more than one when a single host serves several profiles.

## 3. Supply the connection string

`BindStandardConfig` reads the connection string from `ConnectionStrings:bifrost`
and the rest of the settings from the `BifrostQL` section:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "DisableAuth": true,
    "Provider": "sqlserver"
  }
}
```

Connection string shapes per engine:

| Database | Connection string |
|---|---|
| SQL Server | `Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True` |
| PostgreSQL | `Host=localhost;Port=5432;Database=mydb;Username=postgres;Password=xxx` |
| MySQL | `Server=localhost;Port=3306;Database=mydb;User=root;Password=xxx` |
| SQLite | `Data Source=app.db` |

`TrustServerCertificate=True` accepts any certificate the server presents. It
suits local development. For anything else, read
[what accepting it costs](/BifrostQL/reference/configuration/#trustservercertificate).

### Naming the provider

`BifrostQL:Provider` is optional. When it is absent, BifrostQL infers the engine
from the connection string, and throws rather than guessing when the string
matches nothing. Set it explicitly when a string is ambiguous or when you want
the failure at configuration time. Accepted names:

| Engine | Accepted values |
|---|---|
| SQL Server | `sqlserver`, `mssql` |
| PostgreSQL | `postgresql`, `postgres`, `npgsql`, `pgsql` |
| MySQL | `mysql`, `mariadb` |
| SQLite | `sqlite` |

Authentication is on by default. `"DisableAuth": true` suits a first run only —
see [authentication and OIDC](/BifrostQL/guides/authentication/) before exposing
the endpoint. Without it, `BindStandardConfig` requires a `JwtSettings` section
and throws when it is missing.

## 4. Confirm the connection

```bash
dotnet run
```

Open the playground at `http://localhost:5000/graphiql`. The API endpoint sits at
`http://localhost:5000/graphql`.

Every table in the database is now a query field. Pick one and read ten rows:

```graphql
{
  users(limit: 10, sort: [name_asc]) {
    data {
      userId
      name
      email
    }
  }
}
```

Rows come back nested inside `data`, and the field takes `limit`, `offset`,
`sort`, and `filter` arguments. Seeing rows means the connection string, the
provider registration, and the schema read all worked.

If the field names surprise you, read
[schema generation](/BifrostQL/concepts/schema-generation/) — BifrostQL derives
GraphQL names from your table and column names.

## Where the engines differ

The GraphQL surface is identical across all four, but the SQL underneath is not.
Identifier quoting, paging syntax, string concatenation, full-text search, and
upsert strategy each have a per-engine spelling. The
[SQL dialect reference](/BifrostQL/reference/dialects/) has the capability matrix.

## Next steps

- [Ready-made database schemas](/BifrostQL/getting-started/app-schemas/) — eight sample databases to try this against
- [Filtering, sorting, and paging](/BifrostQL/guides/queries/) — the full query surface
- [Automatic table joins](/BifrostQL/guides/joins/) — traversing foreign keys as nested fields
- [Insert, update, upsert, delete](/BifrostQL/guides/mutations/) — the write surface
- [Configuration reference](/BifrostQL/reference/configuration/) — every setting in one table
