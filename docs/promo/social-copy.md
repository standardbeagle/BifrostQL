# BifrostQL promo video — social copy

Two demos, four files. Everything shown was verified live before it was filmed;
the "Verified" and "Do not claim" sections below say exactly how far that goes.

## Files

| File | Format | Duration | Use |
|---|---|---|---|
| `bifrostql-demo-1920x1080.mp4` | 1920x1080 landscape | ~40s | Main product demo — X, LinkedIn, README embed, YouTube |
| `bifrostql-demo-1080x1350.mp4` | 1080x1350 portrait (4:5) | ~40s | Feed-native cut of the same demo |
| `bifrostql-protocols-1920x1080.mp4` | 1920x1080 landscape | ~18s | Multi-protocol demo — the strongest differentiator |
| `bifrostql-protocols-1080x1350.mp4` | 1080x1350 portrait (4:5) | ~18s | Feed-native cut of the same |
| `bifrostql-desktop-builders-1920x1080.mp4` | 1920x1080 landscape | ~39s | The desktop-only builders — SQL console, visual query builder, form builder |
| `protocol-transcript.txt` | text | — | The verbatim session the protocol video renders |

Both landscape cuts are captioned with burned-in text and read fine muted.

## What is real, and the two edits

**Demo video.** The live BifrostQL UI (`src/BifrostQL.UI`, headless mode on
:5000) driving a SQLite database the app builds through its own Quick Start
flow, from the repo's bundled `ecommerce` schema and full seed — 7 tables,
4,825 rows. The GraphiQL beat is the server's own IDE at `/graphiql` answering
a real nested query against that same database. No mockups, no synthetic UI, no
invented numbers.

- **Edit:** one injected CSS rule hides the welcome screen's saved-connection
  list, which holds real customer hostnames. Nothing else is altered.

**Desktop builders video.** The Photino desktop shell (`./bifrostui`) against the
same SQLite database. These three panes are gated behind the desktop bridge and
are unreachable over HTTP by design — the designer never calls the network — so
they cannot appear in a browser capture and are absent from the demo video
above.

- **Edit:** none. It is driven by synthetic clicks because the webview exposes no
  DOM to a harness, but nothing on screen is altered.

**Protocol video.** A rendering of `protocol-transcript.txt` — a verbatim
capture of a real session against a running host that exposes one SQLite
database through GraphQL, the PostgreSQL wire protocol, the Redis wire
protocol, and OData v4 at once. Real `psql`, real `redis-cli`, real `curl`,
over real TCP sockets.

- **Edit:** the reveal is paced for legibility, and the demo password is masked
  in the echoed commands. Commands and output are otherwise unmodified.

## What the demo video shows, in order

1. Welcome screen — Try It Now / Connect to Database
2. Quick Start schema picker (Blog, E-commerce, CRM, Classroom, Project Tracker)
3. Data-size choice (sample vs full) and Launch
4. Explorer — 7 tables with column counts, row counts and FK badges
5. Products grid — `category_id` resolved to its label, related-row counts per row
6. Drill from a product into its reviews, with no query written
7. Orders grid — 800 rows, sortable and paged
8. Schema-generated edit form — required markers and FK pickers
9. GraphiQL — `orders -> customers` and `orders -> order_items -> products`

## What the desktop-builders video shows

1. Raw SQL console — a real grouped/joined query, syntax highlighting, row count
   and timing (`8 row(s) · 39 ms`), result grid, CSV/JSON export
2. Visual query builder — two tables added from the palette, the join
   `main.orders.(customer_id) = main.customers.(customer_id)` **derived from the
   schema's foreign key**, not typed
3. Form builder — the table picker listing every table in the model

## Verified before filming

Live, against the running stack:

- **GraphQL**: 136 generated types; paged queries, `sort` enums, the full filter
  operator set (`_eq _neq _gt _gte _lt _lte _in _nin _between _nbetween _contains
  _ncontains _starts_with _ends_with _like` and negations), `and`/`or` compounds,
  filtering *through* a relationship, single + many joins, `Aggregate`
  (`_count/_avg/_min/_max` with `groupBy`), `Pivot`, and the full mutation
  surface — insert, update, delete, and batch — each confirmed against the data.
- **PostgreSQL wire protocol**: real `psql` over TLS with SCRAM-SHA-256 —
  `SELECT` with `WHERE`/`ORDER BY`/`LIMIT`/`OFFSET`, a relationship `JOIN`, and
  `\dt` catalog emulation.
- **Redis wire protocol**: real `redis-cli` — `GET` (row as JSON), `HGETALL`
  (row as hash), `SCAN`.
- **OData v4**: service document, CSDL `$metadata`, `$filter`, `$select`,
  `$orderby`, `$top`, `$count`, `$expand`, and signed `$skiptoken` paging.
- **Cross-protocol consistency**: GraphQL, pgwire and OData return byte-identical
  top-3 rows for the same question.
- **Fail-closed posture**: unauthenticated RESP → `NOAUTH`; wrong password →
  `WRONGPASS`; RESP writes disabled by default; `DROP TABLE` over pgwire →
  clean "only SELECT statements are supported" with the connection surviving;
  OData with no credentials → 401 **including `$metadata`**.
- **Authorization, cross-protocol** (this is what earns the "same authorization
  pipeline" line in the LinkedIn copy — it was written before it was checked).
  With a role-qualified column policy on `products`
  (`policy-read-deny: price,compare_at_price; policy-read-deny-roles: analyst`),
  the same table read by two identities on three doors: the full identity reads
  `price` over pgwire, RESP and OData; the `analyst` identity is refused it on
  every one, while still reading the columns it is allowed. OData goes furthest —
  the analyst's CSDL `$metadata` has no `price` property at all, so the column is
  not merely denied but invisible, and `$select=price` comes back as an *unknown*
  property rather than a denied one.

Test suites run green alongside: 1,409 `BifrostQL.Server.Test` (covers the gRPC,
S3 and Prometheus adapters not stood up live here) and 624 `edit-db`.

## Do not claim

Verification turned up real limits. Keep them out of the copy.

**Fixed since filming:** two foreign keys to the same table used to collapse into
one join, so the Orders grid showed a raw id for billing and a street label for
shipping. `orders` now exposes `billing_address` and `shipping_address`
separately and both resolve to labels. The footage predates the fix, which is why
beat 5 films Products rather than Orders — re-shoot that beat before reusing the
foreign-key caption over an Orders frame.

- **`psql \d <table>` is not supported** — it issues a regex (`~`) query outside
  the SQL subset. `\dt` works. Do not promise unqualified BI-tool introspection.
- **OData key access (`/odata/products(1)`) returns `NotImplemented`.** Collection
  queries are the supported surface.
- **pgwire TLS is client-initiated**, as in real Postgres: a client asking for
  `sslmode=disable` gets a plaintext session. The listener is loopback by
  default; anyone widening that posture needs TLS terminated in front.
- **A column policy costs an RESP caller the whole row.** RESP has no column
  projection, so `HGETALL` asks for every column and the read guard refuses the
  lot: the analyst reads *nothing* from `products` over RESP while reading the
  permitted columns fine over pgwire and OData. Do not describe column-level
  policy as behaving uniformly across the doors — the enforcement is uniform, the
  granularity is not.
- **Declaring any policy on a table denies every non-admin caller until
  `policy-actions` grants the action.** Worth knowing before demoing policy live;
  it looks like a broken build.

## Caption — X / Twitter (product demo)

> Point BifrostQL at a SQL database. Get a full GraphQL API — every table, every
> column, every relationship — generated at startup.
>
> No codegen. No mapping files. No resolvers to write.
>
> 7 tables, 4,825 rows, zero configuration. MIT.
> github.com/standardbeagle/BifrostQL
>
> #GraphQL #dotnet #SQL #OpenSource #DeveloperTools

## Caption — X / Twitter (protocol demo)

> Same SQLite database. Same server. Four front doors.
>
> psql talks to it over the Postgres wire protocol. redis-cli reads rows as
> hashes. Excel talks OData. Your app talks GraphQL.
>
> One schema, read once at startup. Every door enforces the same auth and the
> same read-only guards.
>
> #GraphQL #Postgres #Redis #OData #dotnet #OpenSource

## Caption — LinkedIn (long)

> Most teams building an API layer over an existing database spend the first
> sprint writing the same thing twice: a schema that already exists in the
> database, restated in application code.
>
> BifrostQL reads the schema at startup and generates the API from it —
> queries, mutations, filters, pagination, aggregates, and the relationships
> between your tables. The demo below points it at a 7-table, 4,825-row
> e-commerce database and browses, filters, drills through foreign keys, and
> answers a nested GraphQL query, with no configuration written at any point.
>
> The second clip is the part I did not expect to be able to record: the same
> database, the same running server, answering `psql` over the PostgreSQL wire
> protocol, `redis-cli` over RESP, and OData for Excel and Power BI — all at
> once, all through the same authorization pipeline. A BI analyst connects with
> the tool they already have. Nobody writes an adapter.
>
> MIT licensed: github.com/standardbeagle/BifrostQL

## Reproducing this

Everything needed is in `capture/` — the capture scripts, the caption/encode
step, and the transcript script, with a README covering the beat-timing
workflow and the honesty rules for this footage.
