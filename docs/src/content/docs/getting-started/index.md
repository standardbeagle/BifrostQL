---
title: Getting Started
description: Install BifrostQL and serve your first GraphQL API in under five minutes.
---

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

Replace `sqlserver` with `postgres` or `mysql` if you're using a different database.

## Wire up

Replace the contents of `Program.cs`:

```csharp
using BifrostQL.Server;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddBifrostQL(o => o.BindStandardConfig(builder.Configuration));
builder.Services.AddCors();

var app = builder.Build();
app.UseCors(x => x.AllowAnyMethod().AllowAnyHeader().AllowAnyOrigin());
app.UseBifrostQL();
await app.RunAsync();
```

That's the entire application. BifrostQL reads your database schema at startup and generates the complete GraphQL API.

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
bifrost config generate --connection "..."
```

`bifrost serve` starts a local GraphQL server without writing any project files. Useful for exploring a database schema before committing to a project structure.

## Next steps

- [Schema Generation](/BifrostQL/concepts/schema-generation/) -- how BifrostQL maps database types to GraphQL
- [Queries](/BifrostQL/guides/queries/) -- filtering, sorting, and pagination
- [Joins](/BifrostQL/guides/joins/) -- automatic and explicit table joins
- [Mutations](/BifrostQL/guides/mutations/) -- insert, update, upsert, delete
