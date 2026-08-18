---
title: "Database to GraphQL API in Five Minutes"
description: "Install BifrostQL, point it at a database, and run your first GraphQL query in five minutes. One connection string turns any SQL database into a GraphQL API."
---

BifrostQL turns a database into a GraphQL API in about five minutes. You install
two packages, put a connection string in `appsettings.json`, and wire four lines
of `Program.cs`. There is no schema file to write and no code to generate — the
database is the contract.

This page uses SQL Server. For the other three engines, see
[connect a database](/BifrostQL/getting-started/connect-a-database/).

## Install

Create a new ASP.NET Core project and add the BifrostQL packages:

```bash
dotnet new web -n MyBifrostApi
cd MyBifrostApi
dotnet add package BifrostQL.Server
dotnet add package BifrostQL.SqlServer  # or BifrostQL.Ngsql, BifrostQL.MySql
```

## Configure

Add your connection string and BifrostQL settings to `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "bifrost": "Server=localhost;Database=mydb;User Id=sa;Password=xxx"
  },
  "BifrostQL": {
    "Path": "/graphql",
    "Playground": "/graphiql",
    "DisableAuth": true,
    "Provider": "sqlserver"
  }
}
```

Replace `sqlserver` with `postgres`, `mysql`, or `sqlite` if you're using a different database. `Provider` is optional; BifrostQL infers the engine from the connection string when it is absent, and fails fast when it cannot.

## Wire up

Replace the contents of `Program.cs`:

```csharp
using BifrostQL.Core.Model;
using BifrostQL.Server;
using BifrostQL.SqlServer;

// Dialect packages do not self-register. Register the provider you installed.
DbConnFactoryResolver.Register(BifrostDbProvider.SqlServer, cs => new SqlServerDbConnFactory(cs));

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
app.UseBifrostQL();
await app.RunAsync();
```

That's the entire application. BifrostQL reads your database schema at startup and generates the complete GraphQL API.

The `DbConnFactoryResolver.Register` call is required. Without it, startup fails
with `No factory registered for provider …`. Each engine has its own factory
type — see [connect a database](/BifrostQL/getting-started/connect-a-database/).

## Run

```bash
dotnet run
```

Open `http://localhost:5000/graphiql` for the GraphQL playground. The API endpoint is at `http://localhost:5000/graphql`.

## Your first query

If your database has a `users` table, you can query it immediately:

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

Every query uses the paged format: the table name takes `limit`, `offset`, `sort`, and `filter` arguments, and results are nested inside a `data` field.

## CLI tool

BifrostQL also ships as a standalone CLI tool for quick schema inspection and local serving:

```bash
dotnet tool install -g BifrostQL.Tool
bifrost serve --connection "Server=localhost;Database=mydb;..."
bifrost schema --connection "..."
bifrost config-generate --connection "..."
```

`bifrost serve` starts a local GraphQL server without writing any project files. Useful for exploring a database schema before committing to a project structure.

## Next steps

- [Connect a Database](/BifrostQL/getting-started/connect-a-database/) -- SQL Server, PostgreSQL, MySQL, and SQLite wiring
- [Ready-Made Schemas](/BifrostQL/getting-started/app-schemas/) -- eight sample databases to explore
- [Example Projects](/BifrostQL/getting-started/examples/) -- runnable samples in the repository
- [Case Studies](/BifrostQL/case-studies/) -- end-to-end walkthroughs of complete deployments
- [Schema Generation](/BifrostQL/concepts/schema-generation/) -- how BifrostQL maps database types to GraphQL
- [Queries](/BifrostQL/guides/queries/) -- filtering, sorting, and pagination
- [Joins](/BifrostQL/guides/joins/) -- automatic and explicit table joins
- [Mutations](/BifrostQL/guides/mutations/) -- insert, update, upsert, delete
- [Module System](/BifrostQL/guides/modules/) -- tenant isolation, auto filters, soft delete, audit columns
- [Extending BifrostQL](/BifrostQL/guides/extensibility/) -- custom filters, mutation transformers, before-commit hooks, async validation
- [React Hooks & Components](/BifrostQL/guides/react-hooks/) -- query and mutate from React
- [Embeddable Data Editor](/BifrostQL/guides/embedded-editor/) -- a full admin UI in one component
- [Desktop App](/BifrostQL/guides/desktop-app/) -- explore any database with `bifrostui`
