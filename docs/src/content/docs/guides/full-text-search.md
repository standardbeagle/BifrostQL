---
title: "Full-Text Search (_search)"
description: "Expose cross-dialect full-text search with the table-scoped _search filter operator: declare searchable columns with the search metadata, understand the pinned multi-term/phrase semantic, wire the required per-database full-text index (SQL Server catalog, Postgres GIN, MySQL FULLTEXT, SQLite FTS5), and rely on _search composing safely (ANDed) with tenant, policy, and soft-delete scoping."
---

BifrostQL exposes cross-dialect full-text search through a single table-scoped filter
operator, `_search`. You declare which columns are searchable with one metadata key; the
operator then appears on that table's GraphQL filter input and lowers to the native
full-text predicate of whichever database backs the profile (SQL Server, PostgreSQL,
MySQL, or SQLite). The query terms are always bound as SQL parameters, and the search
predicate composes as an AND with tenant, policy, and soft-delete scoping — a search can
never return a row the caller could not otherwise see.

## Declaring searchable columns

Mark a table searchable with the table-level `search` metadata key — a comma-separated
list of the string columns the operator matches against:

```
dbo.articles { search: title,body }
dbo.articles { search-language: english }   # optional dialect hint
```

Rules, enforced at model load (a misconfiguration fails fast, never silently):

- Every named column must **exist** on the table and be a **string type**
  (`varchar`/`nvarchar`/`text`/`char`/`clob`/…). Searching a numeric or date column is
  meaningless and is rejected.
- A miscased or unknown key (`Search`, `serach`) is a hard `ModelConfigValidator` error,
  not a silently non-searchable table.
- `search-language` is an optional hint passed to engines that take a text-search
  configuration (currently PostgreSQL's `regconfig`). It is advisory; omit it to use the
  server default.

Once declared, the table's filter input gains a `_search: String` argument:

```graphql
query {
  articles(filter: { _search: "quick brown \"lazy dog\"" }) {
    data { id title }
  }
}
```

`_search` is **table-scoped** (it spans several columns), unlike the per-column operators
(`_eq`, `_contains`, …). It appears only on searchable tables and never on a per-column
`FilterType…Input`.

## Query semantics (the same on every engine)

The four databases' native full-text query languages disagree about how a multi-word
query is interpreted. BifrostQL pins **one** semantic and lowers to it explicitly on every
engine, so identical input produces the same logical result set regardless of backend:

- **Terms** are separated by whitespace.
- **Multiple terms are ANDed** — a row matches only when *every* term matches at least one
  searchable column. (`quick brown` ⇒ must contain "quick" **and** "brown".) Conjunctive
  "narrow as I type" is the intuitive default, and widening it later would be
  non-breaking whereas narrowing it would not.
- **A double-quoted run is a contiguous phrase** — `"lazy dog"` matches those words
  adjacent, in order. Whitespace inside the quotes is literal.
- **Matching is case-insensitive.**

Example: `_search: "quick brown \"lazy dog\""` matches rows where some searchable column
contains "quick", **and** some contains "brown", **and** some contains the exact phrase
"lazy dog".

### Relevance ranking is out of scope

`_search` is a **boolean predicate** — it filters rows, it does not rank them. There is no
relevance score exposed and no `ORDER BY relevance`. `_search` composes with the normal
`sort:` and paging arguments (you order by your own columns, e.g. `sort: [created_at_desc]`,
and page with `limit`/`offset`). Relevance-ranked ordering may be added in a later slice;
until then, order explicitly by a column.

## Security: `_search` is always scoped

The search predicate is **ANDed inside the filter-transformer composition**, never ORed and
never a replacement. When a query runs, the tenant filter (`tenant-filter`, priority band
0–99), the authorization policy (`policy-*`), and the soft-delete filter (`soft-delete`,
band 100–199) each contribute a predicate that is ANDed with the query's own filter — and
`_search` is part of that filter. The multi-column search predicate is parenthesized so it
can never bind more loosely than the scoping predicates it is ANDed with:

```sql
-- correct (what BifrostQL emits): the search predicate is wrapped
WHERE tenant_id = @t AND (deleted_at IS NULL) AND ( <full-text predicate> )
```

A search therefore **cannot** return another tenant's rows, a policy-denied row, or a
soft-deleted row, even when those rows match the search text. The search columns come only
from the validated `search` metadata (schema-derived); no client-supplied identifier ever
reaches the predicate, and every query term is a **bound parameter** — the engines'
full-text query grammars (PostgreSQL `tsquery`, SQLite FTS5 `MATCH`, SQL Server `CONTAINS`
conditions, MySQL boolean-mode `AGAINST`) are injectable even as text, so each term's value
is neutralized (bound as a quoted phrase, or fed through a plain-text query builder such as
`plainto_tsquery`) and never concatenated into SQL.

## Per-database index prerequisite (required)

`_search` lowers to each engine's native full-text facility, and **every one of them
requires a full-text index** on the searchable columns. Without the index the query errors
(or, on some engines, silently matches nothing) — the index is a deployment prerequisite,
not something BifrostQL creates for you. Create it once per searchable table.

### SQL Server — full-text catalog + index

```sql
CREATE FULLTEXT CATALOG ftCatalog AS DEFAULT;
CREATE FULLTEXT INDEX ON dbo.articles (title, body)
    KEY INDEX PK_articles ON ftCatalog;
```

`_search` emits `CONTAINS((title, body), @term)` per term, ANDed. Requires the SQL Server
Full-Text Search feature to be installed.

### PostgreSQL — GIN index on the tsvector

```sql
CREATE INDEX articles_fts_idx ON articles
    USING GIN (to_tsvector('english', coalesce(title,'') || ' ' || coalesce(body,'')));
```

`_search` emits `to_tsvector(cfg, title || ' ' || body) @@ plainto_tsquery(cfg, @term)`
(and `phraseto_tsquery` for a quoted phrase), ANDed per term. Match the index expression to
the configured `search-language` (`'english'` above) so the planner can use the index.

### MySQL / MariaDB — FULLTEXT index

```sql
ALTER TABLE articles ADD FULLTEXT INDEX articles_fts (title, body);
```

`_search` emits `MATCH(title, body) AGAINST(@term IN BOOLEAN MODE)` per term, ANDed.
Boolean mode is used so the pinned multi-term AND semantic holds regardless of MySQL's
natural-language relevance scoring. The FULLTEXT index must cover exactly the searchable
columns.

### SQLite — FTS5 external-content virtual table

SQLite full-text lives in a separate FTS5 virtual table. BifrostQL targets an
**external-content** index named `<table>_fts` whose rowid maps to the base table's integer
primary key:

```sql
CREATE VIRTUAL TABLE articles_fts USING fts5(
    title, body, content='articles', content_rowid='Id'
);

-- keep it in sync (triggers), or rebuild after bulk load:
INSERT INTO articles_fts(articles_fts) VALUES('rebuild');
```

`_search` emits `Id IN (SELECT rowid FROM articles_fts WHERE articles_fts MATCH @term)` per
term, ANDed. Because FTS5 external content correlates on a single integer rowid, the base
table must have a **single-column integer primary key** — a composite or absent primary key
is rejected with an actionable error. Standard trigger-based synchronization (an
`AFTER INSERT`/`UPDATE`/`DELETE` trio on the base table) keeps the index current in
production; the `rebuild` command above is convenient for bulk loads and tests.

## Notes

- `_search` is a read-side filter only; it does not affect mutations.
- Empty or whitespace-only `_search` adds no predicate (the query is unfiltered by search),
  rather than matching everything or nothing by accident.
- If a table declares `search` but the corresponding full-text index is missing, the query
  surfaces a database error — provision the index above before enabling search in
  production.
