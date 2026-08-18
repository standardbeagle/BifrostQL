---
title: "Serve RSS/Atom feeds straight from your tables"
published: false
description: "Publish a SQL table as an RSS 2.0 or Atom 1.0 feed with one metadata key — including conditional GET, per-principal ETags, and XML escaping that survives hostile row content."
tags: rss, api, dotnet, webdev
canonical_url: https://dev.standardbeagle.com/BifrostQL/guides/feeds/
---

One metadata key turns a table into a syndication feed:

```text
main.posts { feed-timestamp: published_at }
```

That is the whole opt-in. `GET /feeds/posts` now returns valid RSS 2.0 with items ordered
newest-first, `GET /feeds/posts.atom` returns Atom 1.0 of the same rows, and both carry an `ETag` and
a `Last-Modified` so a polling reader gets a `304` when nothing has changed. Reads go through the
same query pipeline your GraphQL API uses, so tenant isolation, soft-delete, and policy scope apply
without the feed code knowing they exist.

Everything below is output from a real host I ran against a SQLite database with three rows in
`posts`.

## Describe the item

`feed-timestamp` is required and is what opts the table in. The other three keys are optional and
shape the item:

```text
main.posts { feed-timestamp: published_at }
main.posts { feed-title: title }
main.posts { feed-body: body }
main.posts { feed-link: https://feeds.example.test/posts/{slug} }
```

`feed-title`, `feed-body`, and `feed-link` accept either a bare column name or a `{column}` template
(`Post: {title}`). Only the validated placeholder grammar expands, and only against schema-derived
column names — a row value that happens to contain `{another-column}` stays inert literal text,
because row values are never re-scanned.

Item links should be absolute, since a reader dereferences them out of band with no knowledge of your
request host. Put the site URL directly in the template. The feed-level `<link>` and `<id>` come from
the `FeedOptions.Link` you supply at registration.

The timestamp column must actually be date/time-typed. My first attempt declared it on a SQLite
`TEXT` column and the host refused to load the model at all:

```
Invalid BifrostQL metadata configuration:
  main.posts [feed-timestamp]: 'published_at' - column 'published_at' has type 'TEXT';
  feed timestamps must be date/time-typed
```

## Register and mount

The endpoint is off by construction. A host that never calls `AddBifrostFeeds` registers nothing, and
`UseBifrostFeeds` is inert, so you can call it unconditionally.

```csharp
services.AddBifrostFeeds(
    new FeedOptions
    {
        Title        = "Example Feed",
        Link         = "https://feeds.example.test/",
        Author       = "Example Operator",   // required — Atom is invalid without one
        Description  = "Latest posts",       // RSS requires it; Atom has no equivalent
        MaxItems     = 50,                   // server-side ceiling
        DefaultItems = 20,                   // page size when the caller sends no limit
    },
    o =>
    {
        o.RoutePrefix = "/feeds";
        o.Endpoint    = "/graphql";
    });

app.UseAuthentication();   // before the feed branch, if you accept Bearer
app.UseBifrostEndpoints();
app.UseBifrostFeeds();
```

Enabling it logs a startup warning, because a new authenticated data front door is a posture change
worth seeing in the log. `MaxItems` and `DefaultItems` are the only bounds, and a caller's requested
`limit` is clamped under `MaxItems` — no request can widen the page.

## Identity: bring your own token store

The feed authenticates before any model lookup, planning, or rendering, so an unauthenticated request
gets the same `401` for every table name and existence never leaks ahead of the gate.

Bearer is the preferred path: identity comes from `HttpContext.User`, projected through the same
`IBifrostAuthContextFactory` every other transport uses.

For readers that cannot send an `Authorization` header there is a `?token=` credential, and BifrostQL
ships no token store for it. Minting, revocation, expiry, rotation, and the constant-time comparison
are all yours, behind `IFeedCredentialStore`:

```csharp
public sealed class DemoFeedCredentialStore : IFeedCredentialStore
{
    public Task<FeedCredential?> ResolveAsync(string token, CancellationToken ct)
    {
        var ok = /* constant-time compare against your stored secret */;
        if (!ok) return Task.FromResult<FeedCredential?>(null);

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "feed-reader")], "feed-token");
        return Task.FromResult<FeedCredential?>(new FeedCredential(
            new ClaimsPrincipal(identity), new[] { "posts" }, Enabled: true));
    }
}
```

The returned principal is a candidate — it still passes through the shared identity factory, which
rejects a subject-less one. A deployment that only accepts Bearer registers no store, and every
scoped-token request then fails closed.

A query-string token leaks into access logs and intermediaries, so prefer Bearer, which wins when
both are present. Every response to a query-token request is served `Cache-Control: private,
no-store`; Bearer responses get `Cache-Control: private`.

## The wire

```
$ curl -s -D- "http://127.0.0.1:5306/feeds/posts?token=demo-token"
HTTP/1.1 200 OK
Content-Type: application/rss+xml; charset=utf-8
Cache-Control: private, no-store
ETag: "9BE12466B294C7FCBD60C3F508C37FF32FD3EAC4A72B8B94773B2CBF241B2CDA"
Last-Modified: Sun, 09 Aug 2026 08:15:00 GMT

<?xml version="1.0" encoding="utf-8"?>
<rss version="2.0">
  <channel>
    <title>Example Feed</title>
    <link>https://feeds.example.test/</link>
    <description>Latest posts</description>
    <lastBuildDate>Sun, 09 Aug 2026 08:15:00 GMT</lastBuildDate>
    <item>
      <title>A title with &lt;/item&gt;&lt;script&gt;alert(1)&lt;/script&gt;</title>
      <link>https://feeds.example.test/posts/hostile</link>
      <description>Escaped, never spliced.</description>
      <guid isPermaLink="false">cfd662a4-88f1-555a-a09d-24b1c6b6e6c3</guid>
      <pubDate>Sun, 09 Aug 2026 08:15:00 GMT</pubDate>
    </item>
    …
```

That first item's title is a row value I planted specifically to try to break out of its element.
Every value is written through `XmlWriter`, with no CDATA and no string-spliced markup anywhere, so
it comes back as escaped text and the document structure is unchanged.

The same rows as Atom, one item:

```
$ curl -s "http://127.0.0.1:5306/feeds/posts.atom?token=demo-token&limit=1"
<feed xmlns="http://www.w3.org/2005/Atom">
  <id>https://feeds.example.test/</id>
  <title>Example Feed</title>
  <updated>2026-08-09T08:15:00Z</updated>
  <author><name>Example Operator</name></author>
  <link rel="self" href="https://feeds.example.test/" />
  <entry>
    <id>urn:uuid:cfd662a4-88f1-555a-a09d-24b1c6b6e6c3</id>
    <title>A title with &lt;/item&gt;&lt;script&gt;alert(1)&lt;/script&gt;</title>
    <updated>2026-08-09T08:15:00Z</updated>
    <link rel="alternate" href="https://feeds.example.test/posts/hostile" />
    <content type="html">Escaped, never spliced.</content>
  </entry>
</feed>
```

Format selection is deterministic: an explicit `.rss`/`.atom` suffix wins, then an `Accept` header
naming `application/atom+xml` selects Atom, and everything else — including an unrecognized `Accept`
— falls through to RSS rather than a `406`.

Item ids are RFC-4122 v5 GUIDs derived from the complete primary key plus the timestamp, so they are
stable across processes, providers, and restarts. Ordering is newest-first by `feed-timestamp` with
the full primary key appended ascending, which keeps rows that share a timestamp — composite keys
included — in a stable order across requests.

## Two query parameters

`since` takes an ISO-8601 lower bound and drops items strictly older than it, normalized to UTC
whatever offset you send. `limit` requests a page size and is clamped under `MaxItems`. Both are
optional, both are bounded, and a malformed or overflowing value collapses to a clean `400` rather
than an unhandled parse fault reaching the connection.

`HEAD` is served alongside `GET` and returns the identical status, content type, content length, and
validators with no body — which is what a well-behaved reader uses to poll before it decides to
fetch.

## Conditional GET

Replay the `ETag` and the second request costs the reader nothing:

```
$ curl -s -i -H 'If-None-Match: "9BE1…2CDA"' "http://127.0.0.1:5306/feeds/posts?token=demo-token"
HTTP/1.1 304 Not Modified
ETag: "9BE12466B294C7FCBD60C3F508C37FF32FD3EAC4A72B8B94773B2CBF241B2CDA"
Last-Modified: Sun, 09 Aug 2026 08:15:00 GMT
```

The validator is computed from the authorized, transformer-filtered result set plus the request
representation — the format and an identity partition. Two properties fall out of that. Two different
principals never share an `ETag`, even on byte-identical content, so tenant A's validator can never
let tenant B revalidate and a shared cache cannot serve A's bytes to B. And the partition is derived
from projected claims rather than the raw token, so two tokens mapping to the same principal produce
the same `ETag` and stay cache-shareable.

An empty authorized result set uses a fixed `Last-Modified` at the Unix epoch and a stable `ETag`,
so polling an unchanged empty feed keeps returning a deterministic `304` instead of a wall-clock
value that would defeat the whole mechanism.

## Everything else is a clean status code

From the same run:

| Request | Result |
|---|---|
| no credential | `401` + `WWW-Authenticate: Bearer`, empty body |
| unknown table, no credential | `401` — identical bytes |
| wrong token | `401` — identical bytes |
| `POST /feeds/posts` | `405` with `Allow: GET, HEAD` |
| `limit=99999999999999999999999999999` | `400` |

Every authentication failure class is byte-identical, so the endpoint is not an oracle for table
existence or token validity. A read denied by policy maps to the same sanitized `404` an unknown
table gives, with the detail logged server-side.

The surface stays small on purpose: no WebSub or push, no aggregation across tables, no HTML
sanitizing (values are escaped, not scrubbed), and no write path. One table, one feed, one metadata
key to start.
