---
title: "Point Grafana at any database via the Postgres wire protocol"
published: false
description: "BifrostQL answers the PostgreSQL frontend/backend protocol over any database it fronts, so Grafana's stock postgres datasource connects — with tenant filters, policy guards, and an identity-filtered catalog still enforced on the wire."
tags: postgres, grafana, dotnet, database
canonical_url: https://dev.standardbeagle.com/BifrostQL/guides/pgwire/
---

Grafana has a PostgreSQL datasource. It expects a server that answers the postgres
frontend/backend protocol, replies to `information_schema` queries, and returns rows.
None of that requires an actual PostgreSQL server on the other end.

BifrostQL implements the wire protocol as a front door over whatever database it is
already pointed at — SQL Server, MySQL, SQLite, Postgres. Grafana connects with its
stock postgres driver, the table picker populates, and a chart renders. The part
that matters: every row that comes back has gone through the same tenant filter,
soft-delete hiding, and policy read guards as a GraphQL query against the same
server. The wire changed. The pipeline did not.

## Turning the port on

Two dependencies are hard requirements, checked at startup so the port can never
come up anonymous or unencrypted:

```csharp
builder.Services.AddBifrostPgwire(o =>
{
    o.Port = 5432;                                 // default 5432
    o.AuthMethod = PgAuthMethod.ScramSha256;       // default; secret never crosses the wire
    o.MaxConnections = 100;                        // default; N+1th refused with 53300
    o.Endpoint = "/graphql";                       // which BifrostQL endpoint to read
    o.ServerCertificate = LoadServerCertificate(); // REQUIRED — no cert, no start
});

// REQUIRED — there is no default registration.
builder.Services.AddSingleton<IPgCredentialStore, MyCredentialStore>();
```

`PgWireAdapter.StartAsync` throws if `ServerCertificate` is null, and the connection
handler takes `IPgCredentialStore` as a constructor dependency with nothing registering
a default, so a host missing either one fails at boot rather than serving. TLS is
client-initiated per the protocol — the adapter answers `SSLRequest` by upgrading the
socket, STARTTLS-style, rather than sitting behind Kestrel HTTPS.

The listener binds `IPAddress.Loopback` by default. Widening that is a deployment
decision someone has to make on purpose.

One detail worth copying if you ever write a protocol adapter: the connection slot is
taken at **accept**, before the read, before the TLS handshake, before authentication.
A cap applied after the handshake only bounds sessions that already got in, which
leaves an unauthenticated peer free to make the server do handshake work. The
admission counter has to sit at the front.

## Identity maps through the same seam as everything else

A pg client sends a startup username. `IPgCredentialStore.FindAsync(username)` returns
a `PgLogin(Secret, Principal)`. Under SCRAM-SHA-256 the secret is the PBKDF2 input and
never travels; under `Cleartext` it is compared in constant time over the TLS-wrapped
socket.

The `Principal` is a candidate identity. It still gets projected through
`IBifrostAuthContextFactory`, the same fail-closed seam the HTTP GraphQL gate and the
binary WebSocket gate use. A subject-less principal or an issuer this deployment has no
claim mapper for is rejected there. An unknown username resolves to `null` and
authentication fails; a credential store that hands back an ambient identity on a failed
lookup is a bug in the store.

## A deliberately small SQL surface

This is a read-only front door with an allowlist grammar. It parses to a typed AST and
maps that onto a programmatic `GqlObjectQuery` with every literal bound as a parameter.
Your SQL text is never rebuilt and never forwarded, so a hostile string stays data.

What it accepts: a single `SELECT`, `*` or an explicit column list, one table with an
optional alias, one `INNER JOIN` on a single equality where it maps to a forward
single-column FK relationship, `WHERE` with `AND`/`OR`/parens, the comparison operators
plus `LIKE` / `IN` / `BETWEEN` / `IS NULL`, `ORDER BY`, `LIMIT`/`OFFSET`, and `$N`
placeholders in value positions.

Everything else is refused with a SQLSTATE that tells the client which kind of refusal
it was. The distinction is pinned by tests:

```csharp
[Theory]
[InlineData("SELECT id FROM users WHERE id IN (SELECT id FROM users)")] // subquery
[InlineData("SELECT count(id) FROM users")]                            // function call
[InlineData("SELECT id FROM users GROUP BY id")]                       // GROUP BY
[InlineData("SELECT id FROM users UNION SELECT id FROM users")]        // set op
[InlineData("SELECT id FROM users LEFT JOIN users u ON id = u.id")]    // non-inner join
public async Task OutOfSubset_RecognizedConstructs_AreFeatureNotSupported(string sql)
{
    var ex = await Rejected(sql, UsersOnlyModel());
    ex.Should().BeOfType<PgQueryTranslationException>()
        .Which.SqlState.Should().Be(PgWireProtocol.SqlStateFeatureNotSupported);
}

[Theory]
[InlineData("UPDATE users SET name = 'x'")]                            // write
[InlineData("DELETE FROM users WHERE id = 1")]                         // write
[InlineData("SELECT id FROM users; DROP TABLE users")]                 // second statement
[InlineData("SELECT id FROM users -- comment")]                        // comment
public async Task OutOfSubset_UnrecognizedStatements_AreSyntaxError(string sql)
{
    var ex = await Rejected(sql, UsersOnlyModel());
    ex.Should().BeOfType<PgQueryTranslationException>()
        .Which.SqlState.Should().Be(PgWireProtocol.SqlStateSyntaxError);
}
```

`0A000 feature_not_supported` means the parser understood you and declines. `42601
syntax_error` means it never recognized the statement at all — which is where writes
land, because `UPDATE` and `DELETE` are not statements this grammar has.

A small allowlist is easier to keep safe than a large surface you have to remember to
lock down. Aggregations, CTEs, and window functions all live outside it; if your
dashboard needs them, it is asking for a different front door.

Prepared statements work, in TEXT format only. A BINARY parameter or result format code
in a `Bind` message comes back as a clean `0A000` rather than being reinterpreted as
something it isn't.

## Tenant isolation holds on the wire

The interesting claim is that a caller who asks for a whole table gets only their slice
of it, without the adapter writing a single predicate. That is an integration test
against a seeded database, two identities, one query string:

```csharp
[Fact]
public async Task SameSelect_TwoTenants_SeeDisjointRowSetsOverTheWire()
{
    const string sql = "SELECT id, tenant_id, name FROM orders";

    var tenantA = await _harness.QueryAsync(TenantPrincipal("user-a", "tenant-a"), sql);
    var tenantB = await _harness.QueryAsync(TenantPrincipal("user-b", "tenant-b"), sql);

    var namesA = tenantA.Rows.Select(r => r[2]).ToList();
    var namesB = tenantB.Rows.Select(r => r[2]).ToList();
    namesA.Should().BeEquivalentTo(new[] { "a-first", "a-second" });
    namesB.Should().BeEquivalentTo(new[] { "b-first", "b-second", "b-third" });

    tenantA.Rows.Should().OnlyContain(r => r[1] == "tenant-a");
    tenantB.Rows.Should().OnlyContain(r => r[1] == "tenant-b");
    namesA.Should().NotIntersectWith(namesB);
}
```

Reads execute through `IQueryIntentExecutor`, so registered filter transformers apply
unconditionally. There is no adapter API that skips them.

## The catalog is filtered by the same check

Grafana's table dropdown comes from `information_schema`. Metabase runs a full JDBC sync
against `pg_catalog`. If introspection answered from the raw model, a table you are
denied would still be visible by name — an existence oracle sitting next to a query path
that carefully hides the rows.

The emulated catalog runs the same `PolicyEvaluator` read check the query path runs, and
fails closed on metadata it cannot evaluate:

```csharp
memberNames.Should().Contain(new[] { "orders", "profiles" });
memberNames.Should().NotContain("audit_log");   // policy denies read to non-admins
memberNames.Should().NotContain("broken");      // malformed policy → fail closed

// Admin additionally sees the read-denied table, still not the broken one.
adminNames.Should().Contain(new[] { "orders", "profiles", "audit_log" });
adminNames.Should().NotContain("broken");       // fail closed for everyone
```

The unparseable-policy table is hidden from the admin too. When the evaluator cannot
decide, the answer is no.

The server reports itself as `PostgreSQL 16.0 (BifrostQL)`, which is what
`SELECT version()` returns and what driver version-sniffing reads.

## Errors split two ways

Out-of-subset queries produce a curated pg `ErrorResponse` naming the unsupported
feature, and the connection survives so you can fix the query on the same session.
Anything deeper — an execution fault in the pipeline — collapses to a generic `XX000
internal_error` with fixed text, because internal exception messages can carry driver
detail, schema names, and identifiers that have no business on a client wire. The real
exception goes to the server log.

So a chart that fails with a bare "internal error" is behaving as designed. Check the
server logs for the cause.

## What I actually ran

The pgwire suite, on this checkout:

```
$ dotnet test tests/BifrostQL.Server.Test/BifrostQL.Server.Test.csproj \
    -f net10.0 --no-build --filter "FullyQualifiedName~BifrostQL.Server.Test.Pgwire"

Passed!  - Failed: 0, Passed: 124, Skipped: 0, Total: 124, Duration: 8 s
```

Those 124 cover the handshake and `SSLRequest` path, SCRAM-SHA-256, the subset parser
and extended protocol, catalog filtering, connection and session-state limits, cancel
requests, listener posture, and the adapter conformance kit. One suite,
`PgWireBiToolWireQueryTests`, replays the wire query sequences Grafana and Metabase
actually issue — including psql 16's literal `\dt` query — against the in-process
catalog responder.

What I did **not** run: `psql` is not installed on this machine, and I did not stand up
Grafana or Metabase containers. The commands in the repo runbook — `psql "host=localhost
port=5432 user=<login> dbname=bifrost sslmode=require"`, then Grafana's *Add new
connection → PostgreSQL* with SSL mode `require` — are the documented path, not a
transcript of something I executed here. The repo is explicit about the same split: the
automated gate proves the catalog and read path answer what those drivers send, and a
real cross-process connection to a real Grafana build is a manual smoke someone runs by
hand before a release.

Take the 124 as evidence about the wire. Take the Grafana screenshot you produce
yourself as evidence about Grafana.
