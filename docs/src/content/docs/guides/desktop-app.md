---
title: Desktop App (BifrostQL.UI)
description: A native desktop database explorer with built-in GraphQL playground.
---

BifrostQL includes a desktop application built on [Photino.NET](https://www.tryphotino.io/) — a lightweight native window that wraps a web view. Point it at any SQL Server and you get a GraphQL playground with zero setup.

## Install

The desktop app ships as a standalone binary:

```bash
dotnet tool install -g BifrostQL.UI
```

Or build from source:

```bash
dotnet build src/BifrostQL.UI/BifrostQL.UI.csproj
```

This produces a `bifrostui` binary.

## Usage

Pass a connection string directly:

```bash
bifrostui "Server=localhost;Database=mydb;User Id=sa;Password=xxx;TrustServerCertificate=True"
```

Or launch without one and connect through the UI:

```bash
bifrostui
```

### Options

| Flag | Short | Description |
|------|-------|-------------|
| `--port` | `-p` | Port for the embedded server (default: 5000) |
| `--expose` | `-e` | Bind to `0.0.0.0` instead of localhost |
| `--headless` | `-H` | Server only, no desktop window |

### Headless mode

Run BifrostQL as a standalone server without the desktop window:

```bash
bifrostui "Server=localhost;Database=mydb;..." --headless --port 8080
```

This gives you the same GraphQL endpoint and playground at `http://localhost:8080/graphql` and `http://localhost:8080/graphiql`, plus the connection management API — useful for remote servers or Docker containers.

## What it does

The desktop app bundles a full BifrostQL server inside a native window:

- **Connection management** — Test and switch database connections without restarting. The UI provides endpoints to test (`/api/connection/test`) and set (`/api/connection/set`) connections at runtime.
- **GraphQL playground** — Built-in GraphiQL interface at `/graphiql` for writing and testing queries against your database.
- **Sample databases** — Create pre-built test databases (Northwind, AdventureWorks Lite, Simple Blog) with schema and sample data via `/api/database/create`. Useful for demos and learning.
- **Health check** — `/api/health` reports server status and connection state.

## Architecture

The app runs an embedded ASP.NET Core server on localhost and opens a Photino native window pointed at it. The server hosts both the BifrostQL GraphQL endpoint and a React-based frontend.

```
bifrostui
├── ASP.NET Core server (localhost:5000)
│   ├── /graphql          — BifrostQL GraphQL endpoint
│   ├── /graphiql         — GraphQL playground
│   ├── /api/connection/* — Connection management
│   ├── /api/database/*   — Test database creation
│   └── /api/health       — Health check
└── Photino native window
    └── Loads http://localhost:5000
```

Kestrel is configured with larger request header limits (128KB) to accommodate auth tokens.

## Sample database templates

The `/api/database/create` endpoint accepts a `template` parameter:

| Template | Tables | Description |
|----------|--------|-------------|
| `northwind` | Categories, Products, Customers, Orders, OrderDetails | Classic Northwind with foreign key relationships |
| `adventureworks-lite` | Departments, Employees, Shifts, EmployeeDepartmentHistory | HR-style schema with history tracking |
| `simple-blog` | Users, Posts, Comments, Tags, PostTags | Blog with many-to-many tag relationships |

Database creation streams progress via Server-Sent Events, reporting each stage (connection, schema creation, data insertion) with percentage updates.
