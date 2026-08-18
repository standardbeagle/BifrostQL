---
title: "Schema-Aware SQL Editor"
description: "A schema-aware SQL editor in the desktop shell: dialect-aware highlighting, table and column autocomplete, and execution confined to the Photino bridge."
---

The desktop shell's SQL console is a **schema-aware SQL editor** built on
CodeMirror 6: dialect-aware highlighting, plus autocomplete fed from the
introspected schema the shell already loads.

## What it does

- **Autocomplete** offers table names after `FROM` and column names after an
  alias dot, scoped to the tables in the `FROM` clause where derivable, plus SQL
  keywords. The dialect (`@codemirror/lang-sql`) follows the active connection
  profile (SQLite / SQL Server / PostgreSQL / MySQL).
- **Execution** runs the whole buffer or the current selection. Statements are
  split on `;` using the language tree (not a regex), so semicolons inside
  strings and comments are respected. Each statement produces its own result
  block or error, with the offending statement's offset on error.
- **DDL** (`CREATE TABLE`, `DROP`, …) still works — full parity with the old
  console.
- Executed statements can be kept as saved-object `query` entries flagged as
  SQL, plus a lightweight recent-history ring.

## Security boundary (unchanged)

The editor executes **only through the desktop Photino `exec-sql` bridge** — a
local user against their own connection string. It does **not** open a
server-side arbitrary-SQL execution path. The server's raw-SQL resolver keeps
its `RawSqlValidator` gate; the desktop bridge is intentionally unvalidated
local SQL and stays desktop-only. The completion cache is read-only and never
mutates schema.

## Related

- [Export](/BifrostQL/guides/workbench/export/) — export console result sets.
- [ER diagram](/BifrostQL/guides/workbench/erd/) — read the schema the
  autocomplete draws on.
- [Saved queries](/BifrostQL/guides/workbench/saved-queries/) — keep an executed
  statement as a saved object.
- [Desktop app](/BifrostQL/guides/desktop-app/)
- [Data workbench overview](/BifrostQL/guides/workbench/)
