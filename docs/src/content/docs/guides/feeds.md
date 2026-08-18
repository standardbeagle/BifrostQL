---
title: "RSS and Atom Feeds from SQL Tables"
description: "Publish a table as an opt-in RSS 2.0 or Atom 1.0 feed with feed metadata, absolute-link configuration, revocable feed tokens, and conditional-GET caching."
---

BifrostQL can publish a table as a syndication feed — RSS 2.0 or Atom 1.0 — over an opt-in HTTP front door mounted on its own branch (default `/feeds`). Like every other non-GraphQL front door it **owns only its wire and codec**: reads execute through `IQueryIntentExecutor`, so tenant isolation, soft-delete, and policy scope apply unskippably, and identity is projected through the shared `IBifrostAuthContextFactory`. See [Authoring a Protocol Adapter](/BifrostQL/guides/protocol-adapters/) for the underlying contract.

The feed endpoint is **off by construction**. A host that never calls `AddBifrostFeeds` registers nothing and `UseBifrostFeeds` is inert; enabling it logs a startup warning, because exposing an authenticated data front door is a posture change worth surfacing.

## Opt a table into feed publication

A table becomes a feed by declaring `feed-*` schema metadata. `feed-timestamp` is the only required key — its presence is what opts the table in.

```
main.posts { feed-timestamp: published_at }
main.posts { feed-title: title }
main.posts { feed-body: body }
main.posts { feed-link: https://feeds.example.test/posts/{slug} }
```

| Metadata key | Required | Purpose |
|---|---|---|
| `feed-timestamp` | **yes** | The date/time column that dates each item and orders the feed newest-first. Declaring it opts the table into feed publication. |
| `feed-title` | no | A title column, or a `{column}` template (e.g. `Post: {title}`). A bare name with no `{…}` is the column shorthand. |
| `feed-body` | no | The column whose value becomes the item body / Atom `content`. |
| `feed-link` | no | A per-item link, usually an absolute-URL template such as `https://site/posts/{slug}`. A template with no `{…}` is literal text. |

Only the slice-validated `{column}` placeholder grammar is expanded, against schema-derived columns — there is no custom parser and no row value is ever re-scanned, so a row value that itself contains `{another-column}` stays inert literal text.

**Absolute links / base URL.** Feed readers dereference `<link>`/`<id>` out of band, so item links should be **absolute**. Put the site's base URL directly in the `feed-link` template (`https://feeds.example.test/posts/{slug}`); the feed itself does not synthesize a host from the request. The feed-level `<link>`/`<id>` come from `FeedOptions.Link` (below), also an absolute URL you supply.

## Register and mount

`AddBifrostFeeds` takes a `FeedOptions` (presentation + bounds) and a mounting callback; `UseBifrostFeeds` mounts the branch.

```csharp
services.AddBifrostFeeds(
    new FeedOptions
    {
        Title       = "Example Feed",
        Link        = "https://feeds.example.test/",   // feed self/site URL (absolute)
        Author      = "Example Operator",               // required — an Atom feed is invalid without an author
        Description = "Latest posts",                   // RSS requires a description; Atom has no equivalent
        MaxItems    = 50,                               // server-side ceiling a requested limit is clamped to
        DefaultItems = 20,                              // page size when the caller sends no limit
    },
    o =>
    {
        o.RoutePrefix = "/feeds";        // default
        o.Endpoint    = "/graphql";      // which registered endpoint's model/connection reads resolve against
    });

// If the host accepts Bearer tokens, run authentication BEFORE the feed branch.
app.UseAuthentication();
app.UseBifrostEndpoints();
app.UseBifrostFeeds();
```

`FeedOptions.Title`/`Link`/`Description`/`Author` are operator-supplied channel metadata — never row data — but the writers still XML-escape them. `MaxItems`/`DefaultItems` are the only bounds: a caller's requested `limit` is clamped under `MaxItems`, so no request can widen the page.

With multiple registered endpoints, set `Endpoint` explicitly; an unknown path fails fast rather than silently falling back to another database.

## URL, query, and format behavior

A feed is served at `<prefix>/<table>`, with an optional format suffix:

| Route | Result |
|---|---|
| `/feeds/posts` | Format-negotiated (see below), default RSS 2.0. |
| `/feeds/posts.rss` | RSS 2.0 explicitly. |
| `/feeds/posts.atom` | Atom 1.0 explicitly. |

**Format negotiation** is deterministic: an explicit `.rss`/`.atom` suffix wins; otherwise an `Accept` header naming `application/atom+xml` selects Atom; otherwise RSS 2.0 is the default. An unrecognized `Accept` falls through to RSS, never a 406.

**Query parameters** (both optional, both bounded):

| Param | Meaning |
|---|---|
| `since` | ISO-8601 lower bound; items strictly older are dropped. Normalized to UTC regardless of the offset. |
| `limit` | Requested page size, clamped under `MaxItems`. |

A malformed or overflowing `since`/`limit` (e.g. a 29-digit limit) collapses to a clean `400` — never an unhandled parse fault. Only `GET` and `HEAD` are served; any other method is `405` with an `Allow: GET, HEAD` header. `HEAD` returns the identical status/content-type/length/validators with no body.

Items are ordered newest-first by `feed-timestamp`, with the **full primary key** appended ascending as a deterministic tiebreak, so rows sharing a timestamp (including composite-key tables) order stably across requests. Each item id is a deterministic RFC-4122 v5 GUID derived from the complete primary key plus the timestamp, stable across processes and providers — a null key/timestamp fails closed rather than emitting an item with an ambiguous identity.

## Identity: Bearer vs a host-owned revocable token

The feed authenticates **before** any model lookup, planning, cache evaluation, or rendering, so an unauthenticated request gets the identical `401` for every table name and existence is never leaked before the gate. Two identity paths are accepted:

- **Bearer (preferred).** Identity is read from `HttpContext.User` and projected by the shared `IBifrostAuthContextFactory` (subject → user id, tenant claim → tenant id, roles → roles). Run `UseAuthentication` before `UseBifrostFeeds`.
- **`?token=` query credential.** For readers that cannot send an `Authorization` header. BifrostQL invents **no** built-in token store: minting, revocation, expiry, and rotation are entirely the host's, behind `IFeedCredentialStore`. A deployment that only accepts Bearer registers no store, and every scoped-token request then fails closed. The store owns the (constant-time, anti-enumeration) token comparison; the feed never sees raw secret material. An unknown, revoked, or disabled token resolves to `null` → the same fail-closed `401`.

:::caution[A query-string token leaks into access logs]
A `?token=` value is recorded by intermediaries and access logs. **Prefer `Authorization: Bearer`**, which takes precedence when both are present. Every response to a query-token request is served `Cache-Control: private, no-store` so a shared cache never retains it; Bearer responses are `Cache-Control: private`.
:::

Every auth-failure class — no credential, unknown table, unknown/revoked token, token scoped to another table — is **byte-identical on the wire**: a bare `401` with a `WWW-Authenticate: Bearer` challenge and no body, across `GET`/`HEAD` and every format variant. There is no existence or credential-validity oracle. A read denied by policy (or any Bifrost-internal read error) maps to the same sanitized `404` an unknown table gives; full detail is logged server-side only.

## Caching semantics (conditional GET)

Each response carries a strong `ETag` and a `Last-Modified` derived **only** from the authorized, transformer-filtered result set plus the request representation (format + a token-free identity partition). A matching `If-None-Match` (or `If-Modified-Since`) yields `304 Not Modified` with the validator still present and no body.

Two properties are structural, not conventional:

- **Cross-tenant non-reuse.** The validator folds an identity partition, so two different principals never share an `ETag` even on byte-identical content — tenant A's validator can never let tenant B revalidate, and a shared cache cannot serve A's representation to B.
- **Token-free.** The partition is derived from projected claims, never the raw feed token, so two different tokens mapping to the **same** principal produce the **same** `ETag` (cache-shareable), while different principals never collide.

An empty authorized result set uses a fixed deterministic `Last-Modified` (Unix epoch) and a stable `ETag`, so polling an unchanged empty feed gets a deterministic `304` rather than a wall-clock value that defeats conditional GET.

## Safety: hostile content stays inert

Every value is written through `XmlWriter`, which escapes `< > & "` unconditionally; there is **no CDATA and no string-spliced markup** anywhere. A hostile row value — a title containing `</item></channel><script>…`, a body containing `]]>` — is emitted as inert escaped text and can never break out of its element. This holds for both the RSS and Atom writers and is asserted end-to-end by parsing the output with the .NET XML APIs and confirming the document structure is unchanged.

## Non-goals

The feed surface is deliberately minimal. It does **not**:

- implement **WebSub**/PubSubHubbub or any push/subscription protocol — it is a pull surface only;
- **aggregate** across tables or endpoints — one feed maps to one table;
- **sanitize** row HTML — values are escaped, not scrubbed; a body is emitted verbatim as escaped text (Atom `content type="html"`), and it is the publisher's responsibility that stored content is appropriate;
- expose any **write** path — feeds are read-only; there is no feed mutation, and reads still cross the full transformer pipeline.
