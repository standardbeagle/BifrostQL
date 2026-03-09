# Multi-Provider UI + SQLite Quick-Start

## Summary

Redesign BifrostQL.UI to support all four database backends (SQL Server, PostgreSQL, MySQL, SQLite) and add a zero-config SQLite quick-start with five example schemas. The quick-start serves as the primary onboarding path, especially for educational use (e.g., frontend development classes needing a ready-made database + API).

## Welcome Screen

Three primary cards replace the current two-card layout:

1. **Try It Now** (hero position) - SQLite quick-start. Zero config, pick a schema, toggle sample data, launch. Subtitle: "Explore a ready-made database with GraphQL in seconds"
2. **Connect to Database** - Card-based provider selection, then provider-specific connection form
3. **Recent Connections** - Same localStorage persistence, now with provider icons/badges

The "Create Test Database" card is removed; its functionality is subsumed by the quick-start.

## Provider Connection Flow

```
Welcome -> Connect to Database -> Provider Cards -> Connection Form -> Test -> Connect -> Editor
```

### Provider Cards

Four cards with provider logos: SQL Server, PostgreSQL, MySQL, SQLite. Clicking one slides into the provider-specific form.

### Provider-Specific Forms

| Provider    | Fields                                                                  |
|-------------|-------------------------------------------------------------------------|
| SQL Server  | Server, database, auth method (SQL/Windows), username, password, TrustServerCertificate |
| PostgreSQL  | Host, port (default 5432), database, username, password, SSL mode       |
| MySQL       | Host, port (default 3306), database, username, password, SSL mode       |
| SQLite      | File path with browse button, option to create new empty database       |

Each form includes a "Use connection string" toggle for advanced users. Provider auto-detection via `DbConnFactoryResolver.DetectProvider` identifies provider from raw strings.

## SQLite Quick-Start Flow

```
Welcome -> Try It Now -> Schema Picker -> Select + Data Toggle -> Launch -> Editor
```

### Schema Picker

Grid of five schema cards, each with icon, description, and feature highlights:

1. **Blog** - "Posts, authors, comments & tags"
   - Tables: `authors`, `posts`, `comments`, `tags`, `post_tags`, `categories` (6 tables)
   - Features: many-to-many joins (post_tags), nested comments via parent_id, date/status filtering

2. **E-commerce** - "Products, orders, customers & reviews"
   - Tables: `customers`, `products`, `categories`, `orders`, `order_items`, `reviews`, `addresses` (7 tables)
   - Features: composite relationships (order->items->product), nullable FKs (guest checkout), numeric filtering

3. **CRM** - "Contacts, companies, deals & activities"
   - Tables: `companies`, `contacts`, `deals`, `activities`, `notes`, `deal_stages` (6 tables)
   - Features: self-referencing (company.parent_company_id), polymorphic-style notes (entity_type + entity_id), stage pipeline

4. **Classroom** - "Courses, students, assignments & grades"
   - Tables: `instructors`, `courses`, `students`, `enrollments`, `assignments`, `submissions` (6 tables)
   - Features: junction table with extra data (enrollment.grade), date-range queries, status enums

5. **Project Tracker** - "Asana-style tasks, projects & workflows"
   - Tables: `workspaces`, `projects`, `sections`, `tasks`, `labels`, `task_labels`, `task_assignments`, `members` (8 tables)
   - `tasks` columns: title, description, status, priority, due_date, created_at, completed_at, sort_order, parent_task_id
   - Features: hierarchical tasks (subtasks via self-ref), multi-assignee, kanban-style status workflow, sort ordering, label filtering

### Data Toggle

Two radio buttons below the schema selection:
- **Sample data** (~50 rows per table) - default, fast to create, good for demos
- **Full dataset** (~500-1000 rows per table) - makes filtering/pagination meaningful for classroom use

### Launch

On click: creates SQLite file in OS temp directory, streams DDL + seed execution progress via SSE, auto-connects on completion. SQLite file path shown in header bar for reuse.

## Backend Changes

### New Endpoints

- `GET /api/providers` - Returns list of available providers (checks which dialect assemblies are loaded)
- `POST /api/database/create-quickstart` - Accepts `{ schema: string, dataSize: "sample" | "full" }`, creates SQLite file, streams progress via SSE
- `POST /api/connection/set` - Updated to accept optional `provider` field alongside connection string

### Schema SQL Files

Embedded resources in `BifrostQL.UI.csproj`:

```
src/BifrostQL.UI/Schemas/
  blog.sql
  blog-seed-sample.sql
  blog-seed-full.sql
  ecommerce.sql
  ecommerce-seed-sample.sql
  ecommerce-seed-full.sql
  crm.sql
  crm-seed-sample.sql
  crm-seed-full.sql
  classroom.sql
  classroom-seed-sample.sql
  classroom-seed-full.sql
  project-tracker.sql
  project-tracker-seed-sample.sql
  project-tracker-seed-full.sql
```

### Package References

`BifrostQL.UI.csproj` gains references to all dialect packages:
- BifrostQL.SqlServer
- BifrostQL.Ngsql
- BifrostQL.MySql
- BifrostQL.Sqlite

All dialect factories registered on startup.

## Frontend Changes

### New Components

- `ProviderSelect.tsx` - Four provider cards with back navigation
- `QuickStart.tsx` - Schema picker grid + data toggle + launch button + SSE progress

### Modified Components

- `WelcomePanel.tsx` - Redesigned three-card layout
- `ConnectionForm.tsx` - Refactored for per-provider field sets
- `main.tsx` - Updated view state machine
- `types.ts` - Provider enum, connection metadata types

### State Machine

```
type View =
  | 'welcome'
  | 'quickstart'
  | 'provider-select'
  | 'connect'
  | 'editor'

type Provider = 'sqlserver' | 'postgres' | 'mysql' | 'sqlite'
```

### Recent Connections

Updated to store provider type for icon display. Provider stored alongside connection string and display name in localStorage.

## Implementation Order

1. Backend: API endpoints + dialect registration
2. Schema SQL files (can be parallelized across all 5 schemas)
3. Frontend: State machine + types
4. Frontend: WelcomePanel redesign
5. Frontend: ProviderSelect + ConnectionForm refactor
6. Frontend: QuickStart component

## Dart Tasks

Parent: [Multi-provider UI + SQLite quick-start](https://app.dartai.com/t/97YHOuTXKeOw)

Subtasks:
- [Backend: Add quickstart + provider API endpoints](https://app.dartai.com/t/OWAuGTGSkvLj)
- [Schema SQL: Blog](https://app.dartai.com/t/1NAwS9uBtG7K)
- [Schema SQL: E-commerce](https://app.dartai.com/t/NeZFvpVAV0EB)
- [Schema SQL: CRM](https://app.dartai.com/t/NIXr3C1HEyJW)
- [Schema SQL: Classroom/LMS](https://app.dartai.com/t/YYQrjJFJcF7w)
- [Schema SQL: Project Tracker (mini-Asana)](https://app.dartai.com/t/L3WCe5zN8T0E)
- [Frontend: Redesign WelcomePanel](https://app.dartai.com/t/5XQwMeVy2WpP)
- [Frontend: Provider selection + ConnectionForm](https://app.dartai.com/t/2JuaRNOsXEYo)
- [Frontend: QuickStart schema picker](https://app.dartai.com/t/kwYNOHE3VlZZ)
- [Update main.tsx state machine](https://app.dartai.com/t/Ql2sPnLdIgZv)
