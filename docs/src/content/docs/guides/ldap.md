---
title: "LDAP Directory Server from a Database"
description: "Publish users and groups from SQL tables as LDAPv3 entries, so ldapsearch, Grafana, and other directory clients can bind and search over LDAPS or StartTLS."
---

BifrostQL can answer LDAPv3 on a TCP port, so any tool that speaks LDAP —
`ldapsearch`, Grafana's LDAP authentication, an application's directory client —
can **read** your users and groups as directory entries. It is a
[protocol adapter](/BifrostQL/concepts/protocol-adapters/): the wire is LDAP, but
every read still executes through the same transformer pipeline as GraphQL, so
tenant isolation, soft-delete, and policy read guards are enforced on the wire.

This is a **read-only, deliberately narrow** front door. It is not an LDAP server
and it is not Active Directory: it publishes the tables you explicitly map, over
the operations listed below, and answers everything outside that surface with a
clean result code. There is **no write verb at all** — add, modify, delete, and
modifyDN are non-goals, not deferred features.

> Already have a running endpoint and want to validate `ldapsearch` or a real
> Grafana login end to end? Jump to the
> [LDAP Smoke Runbook](/BifrostQL/guides/ldap-smoke/).

## Enabling the front door

Register the adapter with `AddBifrostLdap`. Two seams are **hard requirements**
for authentication — an `ILdapCredentialStore` and an `ILdapPasswordHasher` — and
there is deliberately no default registration for either. A listener registered
without them still comes up, but every bind is refused with `unwillingToPerform`:
the front door can never authenticate against an ambient credential source it
invented for you.

```csharp
builder.Services.AddBifrostLdap(o =>
{
    o.Port = 389;                                   // default 389 (the LDAP port)
    o.LdapsPort = 636;                              // opt-in second port for implicit TLS; null = disabled
    o.TlsCertificatePath = "/etc/bifrost/ldap.pfx"; // serves BOTH LDAPS and StartTLS
    o.TlsCertificatePassword = pfxPassword;
    o.Endpoint = "/graphql";                        // which BifrostQL endpoint to read; null = the only one
    o.PagedResultsCookieSecret = cookieSecret;      // REQUIRED for multi-instance; see Paged results
    // o.AnonymousBindEnabled = true;               // opt-in; even then, discovery only (default off)
    // o.MemberOfEnabled = true;                    // opt-in reverse membership attribute (default off)
});

// REQUIRED for bind — the identity source and the hash verifier. No default registration.
builder.Services.AddSingleton<ILdapCredentialStore, MyDirectoryCredentialStore>();
builder.Services.AddSingleton<ILdapPasswordHasher, MyArgon2idHasher>();

// OPTIONAL — the structured bind audit log and lockout hook.
builder.Services.AddSingleton<ILdapBindObserver, MyBindAuditor>();
```

`LdapWireOptions`:

| Option | Default | Meaning |
|--------|---------|---------|
| `Port` | `389` | TCP port the cleartext listener binds. StartTLS is the route to confidentiality on it. |
| `BindAddress` | `IPAddress.Loopback` | Address **both** listeners bind. An undeclared exposure posture is loopback; widening it (loopback → LAN → public) is an operator decision. |
| `LdapsPort` | `null` | Opt-in implicit-TLS port (conventionally `636`). Setting it **requires** a certificate — an LDAPS port with nothing to present aborts startup rather than becoming a second cleartext port. |
| `ServerCertificate` / `TlsCertificatePath` / `TlsCertificatePassword` | `null` | The certificate presented by LDAPS **and** by StartTLS — one certificate, so the two surfaces cannot diverge. A path that cannot be loaded, or a certificate with no private key, aborts startup; it is never treated as "no certificate configured". |
| `AllowInsecureSimpleBind` | `false` | **Development only.** Admits a credentialed bind on a cleartext connection. Enabling it logs a startup warning; it is never inferred from a missing certificate. |
| `AnonymousBindEnabled` | `false` | Whether an anonymous bind is admitted at all. Off by default; even when on, the session reaches only the RootDSE and the subschema. |
| `Endpoint` | `null` | Registered BifrostQL endpoint whose directory model this front door serves; `null` selects the single registered endpoint. |
| `MaxConnections` | `100` | Concurrent connections across **both** listeners, so opening LDAPS does not double the ceiling. Slots are taken at accept, before any TLS handshake. |
| `AuthenticationTimeout` | `30 s` | Pre-auth deadline. A connection that has not authenticated by then is closed, because failing binds keep a connection non-idle and would otherwise hold an admission slot for the whole idle window. |
| `IdleTimeout` | `5 min` | How long an authenticated connection may sit idle before it is closed. |
| `TlsHandshakeTimeout` | `30 s` | Deadline for a handshake on either surface; the admission slot is already held while it runs. |
| `MaxMessageLength` | `1 MiB` | Cap on one LDAPMessage, applied on the unauthenticated path before the body buffer is allocated. |
| `MaxNestingDepth` | `32` | How deeply a filter may nest. The decoder recurses per level, so an unbounded filter would overflow the stack — which is uncatchable and takes the whole host process down. |
| `MaxFilterComponents` | `1024` | Total filter nodes in one SearchRequest. |
| `MaxSearchAttributes` | `1024` | Attributes one SearchRequest may name. |
| `MaxOutstandingOperations` | `64` | In-flight operations on one connection. |
| `MaxSearchResults` | `1000` | Hard ceiling on entries from one search. A client's own `sizeLimit` can only narrow it. |
| `MaxPageSize` | `500` | Largest page the paged-results control may request; a larger request is narrowed, never honoured. |
| `SearchBatchSize` | `200` | Rows fetched per pipeline round trip while filling a page. |
| `MaxSearchDuration` | `30 s` | Wall-clock ceiling on one search; a client's `timeLimit` can only narrow it. |
| `MaxMembersPerEntry` | `1000` | Cap on member DNs resolved for one group entry. Exceeding it answers `adminLimitExceeded` rather than returning a silently truncated member list. |
| `MemberOfEnabled` | `false` | Whether entries publish the reverse `memberOf` attribute. Off by default: it costs an extra bounded query per search. |
| `PagedResultsCookieSecret` | `null` | Secret keying the HMAC over paging cookies. Unset generates a random per-process key — safe, but cookies stop validating across restarts and across instances. |
| `PagedResultsCookieTtl` | `10 min` | How long a paging cookie stays valid. |
| `MaxPasswordLength` | `4096` | A longer presented password is refused **before** the adaptive hasher runs, so an oversized password cannot be weaponized as a hash-DoS. |
| `MaxBindAttemptsPerSource` | `100` | Bind attempts admitted per connection source per window. |
| `MaxBindAttemptsPerAccount` | `10` | Bind attempts admitted per bind DN per window. |
| `BindRateLimitWindow` | `1 min` | The fixed window both bind caps are counted over. |

Every limit is validated at registration **and** at startup, so a value that would
disable a guard aborts the host rather than quietly serving unbounded reads.

## Mapping tables into the directory

Nothing is published without an explicit opt-in. A model opts in by declaring a
base DN; a table opts in by declaring the objectClass its rows present as, the DN
template that names each entry, and the attribute→column mappings a search
returns. These are the six
[metadata keys](/BifrostQL/concepts/schema-generation/) under `MetadataKeys.Ldap`:

```csharp
e.Metadata = new[]
{
    ":root { ldap-base-dn: dc=example,dc=com }",

    "dbo.users { ldap-object-class: inetOrgPerson; " +
                "ldap-dn-template: uid={username},ou=people; " +
                "ldap-attributes: uid=username,cn=full_name,mail=email; " +
                "ldap-credential: password_hash }",

    "dbo.groups { ldap-object-class: groupOfNames; " +
                 "ldap-dn-template: cn={name},ou=groups; " +
                 "ldap-attributes: cn=name,description=description; " +
                 "ldap-member: members }",
};
```

| Key | Level | Meaning |
|-----|-------|---------|
| `ldap-base-dn` | model (`:root`) | The base DN every entry is rooted under. Required once any table opts in — a mapped model with nowhere to root its entries is a startup failure. |
| `ldap-object-class` | table | Comma-separated objectClasses the rows present as. **Presence of this key is the opt-in**; a present-but-empty value is rejected. |
| `ldap-dn-template` | table | The DN, relative to the base DN. The leftmost component is the RDN — `attribute={column}` — and every remaining component is a static `attribute=value` path segment. |
| `ldap-attributes` | table | Comma-separated `attribute=column` mappings the directory returns. The RDN's naming attribute must appear here, or a search could never surface the value the entry is named by. |
| `ldap-credential` | table | The column holding the password hash simple bind verifies against. See the hard rule below. |
| `ldap-member` | table | The relationship whose target rows are the entry's group members, surfaced as `member`. |

A table hidden with `visibility: hidden` is excluded from the directory entirely.

### DN design

An entry's DN is its RDN plus the template's static components plus the base DN.
`uid={username},ou=people` under `dc=example,dc=com` names a row with
`username = 'ada'` as:

```
uid=ada,ou=people,dc=example,dc=com
```

The static tail (`ou=people`) is the entry family's **container**, which is what a
one-level search addresses. Containers are derived from the templates; they are not
separate entries you configure.

Design rules the model enforces at load, so a typo fails fast rather than
publishing the wrong directory:

- **Exactly one placeholder, in the RDN.** Only the leftmost component may carry
  `{column}`; a placeholder anywhere else is rejected, so a DN can never depend on
  unresolved input.
- **The naming attribute must be a returned attribute.**
- **No duplicate attributes** in `ldap-attributes` (matched case-insensitively).
- **Attribute syntax must match the column.** A well-known attribute carries a
  required LDAP syntax — mapping `description` (a DirectoryString) onto an INTEGER
  column is rejected. Attributes outside the well-known registry are unconstrained,
  which is the escape hatch for mapping arbitrary columns.
- **No two tables may share a DN namespace.** Templates that normalize to the same
  shape are a rejected collision, not a silent overlap.

### The credential column never leaves

`ldap-credential` names a column used for bind verification **only**. It is never a
searchable or returned attribute, and the mapping is rejected at model load if:

- the same column also appears as a source in `ldap-attributes` — under **any**
  attribute name; or
- the same column is the DN template's RDN naming column, which would put the
  password hash in every entry's DN.

Both are refused at the parse boundary, so there is no configuration that publishes
a password hash. A search that *filters* on the credential column matches nothing
and fetches nothing extra, and the column never appears in the subschema.

## Authentication and identity mapping

A client authenticates with an LDAP **simple bind**: a bind DN and a password.
`ILdapCredentialStore.FindAsync(bindDn)` resolves the DN to an
`LdapCredentialRecord(PasswordHash, Principal, Enabled)`:

- **`PasswordHash`** — the stored credential in a self-describing hash format
  (bcrypt `$2*`, Argon2id `$argon2id$`). It is a **hash**, never a plaintext
  password: the bind path only ever hands it to `ILdapPasswordHasher.Verify`, and
  there is no code path that compares a plaintext.
- **`Principal`** — the `ClaimsPrincipal` the account maps to. This is the
  *candidate* identity only: it is still projected through
  [`IBifrostAuthContextFactory`](/BifrostQL/guides/protocol-adapters/#identity-the-auth-context-factory),
  the same fail-closed seam the HTTP GraphQL and pgwire gates use. A subject-less
  or unmapped-issuer principal is rejected there.
- **`Enabled`** — whether the account may bind at all.

**Fail-closed, always.** An unknown DN resolves to `null` and the bind fails — a
store must never hand back an ambient or anonymous identity to stand in for a
failed lookup. A store that *throws* is treated as an unknown DN, logged
server-side, and never surfaced on the wire. The tenant and policy claims on the
mapped principal are what scope every subsequent read; a principal that projects to
an **empty** user context is rejected rather than degraded to anonymous.

### One answer for every failure

Unknown DN, wrong password, disabled account, subject-less principal, unmapped
issuer, oversized password, and a tripped rate limit all return the **same**
`invalidCredentials`, and the connection stays open so a client can retry. The
hash verify runs **unconditionally** — against the account's hash, or against the
hasher's decoy hash when the DN is unknown — and the existence and enabled checks
are AND-ed *after* it, so an unknown DN and a wrong password do the same work and
are timing-indistinguishable.

### Anonymous binds

Off by default. An anonymous bind (empty DN and empty password) is refused with
`invalidCredentials` unless `AnonymousBindEnabled` is set. Even when enabled, an
admitted anonymous session may read **only** the RootDSE and the subschema
subentry; any other base is refused with `insufficientAccessRights`. The subschema
an anonymous session reads is a bare skeleton — the entry exists, but it names no
objectClass and no attribute type, so the directory's shape cannot be enumerated
without binding.

### Rate limiting and the bind audit

Two independent caps apply per `BindRateLimitWindow`: per connection source
(`MaxBindAttemptsPerSource`, bounding one client spraying credentials across many
accounts) and per bind DN (`MaxBindAttemptsPerAccount`, bounding a brute force
against one account). A tripped cap refuses the attempt **before** any hash work,
and — as above — is indistinguishable from a wrong password on the wire.

Every attempt is reported to the optional `ILdapBindObserver` as an
`LdapBindAuditRecord(Outcome, BindDn, Source, TimestampUtc)`. This one seam is both
the structured bind audit log and the **lockout hook**: a deployment's lockout
policy observes the failure stream and reacts, typically by disabling the account
in its own store so the next `FindAsync` returns `Enabled: false`. The record
carries neither the presented password nor the stored hash, so the audit trail
cannot become a credential-disclosure channel. The `Outcome` classification
(`RateLimited`, `PasswordTooLong`, `AnonymousDisabled`, …) is finer than the wire
code deliberately: it is server-side only, so it can drive policy without becoming
an enumeration oracle. An observer that throws is logged and ignored — auditing
never breaks a bind.

## Transport security

**A credentialed bind is refused on a cleartext connection.** The transport gate is
the first statement of the bind path, ahead of the authenticator, the rate limiter,
the store, and the hasher: the presented secret is never resolved, compared, or used
to select a code path where a passive observer already has it, and the password
bytes are zeroed rather than carried further. The refusal is
`confidentialityRequired` and it talks about the transport only — identical for a
real DN and a fabricated one, so it cannot become an enumeration oracle — and it
leaves the connection open so the client can StartTLS and retry.

An **anonymous** bind is exempt from the gate: it carries no secret to protect.

Two routes to confidentiality, sharing one certificate and one session loop:

- **LDAPS** (implicit TLS) on `LdapsPort`. The handshake completes before the first
  LDAP byte. A connection slot is taken at accept, before the handshake, so a peer
  that stalls mid-handshake is bounded by `TlsHandshakeTimeout` rather than holding
  a slot for the idle window. A failed handshake closes silently — nothing sayable
  on that wire would be readable.
- **StartTLS** on the cleartext port. Only the pre-bind, not-yet-confidential state
  negotiates. Every other state is refused with the session left exactly as it was:
  no certificate configured answers `unavailable` and the listener stays cleartext;
  already confidential or already bound answers `operationsError`. Anything the peer
  **pipelined** behind the StartTLS request is a fatal protocol error before the
  success response and before any handshake — those bytes were written in the clear
  on the assumption they would be processed, and carrying them across the upgrade
  would let an attacker inject cleartext requests a client believes were protected.

After a successful upgrade the session reads and writes through the confidential
stream with a fresh decoder over a fresh buffer, so no byte and no decoder state
survives it. Nothing returns a session to cleartext.

### Exposure posture

Both listeners bind `BindAddress`, which defaults to `IPAddress.Loopback`. A
BifrostQL front door is never published to every network the host sits on merely by
being registered. Widening the posture is an operator decision that has to be
written down in configuration.

## Supported operations

| Operation | Behavior |
|-----------|----------|
| **Bind** (simple) | Verified against `ILdapCredentialStore` + `ILdapPasswordHasher`. Refused with `confidentialityRequired` on a cleartext transport, `unwillingToPerform` when no authenticator is registered, `invalidCredentials` for every authentication failure class. A failed bind leaves the session **unauthenticated** — it never downgrades an already-bound session. |
| **Search** | Executes as bounded, transformed query intents. Zero or more `SearchResultEntry` messages then exactly one `SearchResultDone`, on every path — success, refusal, or fault. |
| **Unbind** | Closes the connection. No response, by protocol. |
| **Abandon** | A silent no-op. The loop answers one request at a time, so by the time an Abandon is decoded the operation it names has completed. Cancellation of a search that *is* in flight comes from the connection token and the search's own deadline, both linked — a client that drops the connection stops the work against the database. |
| **Extended: StartTLS** | The transport upgrade described above. |
| **Extended: anything else** | `unwillingToPerform`, echoing only the caller's own requested OID. |
| **Add / Modify / Delete / ModifyDN / Compare** | Not implemented. An unrecognized protocolOp is a fatal protocol error: the server sends a Notice of Disconnection and closes rather than guessing a response shape. |

**Scopes.** `baseObject`, `singleLevel`, and `wholeSubtree` are all served. A base
that names one entry narrows by the RDN value as a bound **parameter**, never an
interpolation.

## Supported filters

| Filter | Support |
|--------|---------|
| `(attr=value)` equality | Pushed down as a parameter, then evaluated exactly. |
| `(attr=initial*any*final)` substrings | Supported. An escaped literal `*` in a fragment stays a literal. |
| `(attr>=value)` / `(attr<=value)` | Supported. |
| `(attr=*)` present | Supported. |
| `(&…)` / `(\|…)` / `(!…)` | Supported, bounded by `MaxNestingDepth` and `MaxFilterComponents`. |
| `(attr~=value)` approximate | Accepted, evaluated as equality. It is not a phonetic match — the honest reading of "approximate" against a relational column. |
| `(attr:rule:=value)` extensibleMatch | **Not supported.** Decoded so the filter tree stays complete, then treated as undefined: it constrains nothing and **matches nothing**. A search using it returns zero entries rather than everything. |

Filter pushdown is a sound **over-approximation**: the pipeline narrows the fetch,
and every fetched row is then evaluated exactly before it becomes an entry. A
filter can only ever narrow what the bound identity was already permitted to read —
there is no code path by which one widens it. Values bind as query parameters and
are never concatenated into SQL.

## Attributes, members, and the subschema

A search returns the attributes it was asked for, or all mapped attributes when it
names none. `1.1` (the no-attributes OID) returns bare DNs, and `typesOnly` returns
attribute types with no values.

**`member`** is synthesized for an entry whose table declares `ldap-member`. Values
are DNs built from the *target* family's own mapping, escaped like any other DN.
Both join legs run as transformed intents under the bound identity, so a member the
caller cannot see never appears — including when the junction row itself is visible
but the target row is not. Membership is **not** expanded transitively, and a cyclic
membership terminates. A group with more members than `MaxMembersPerEntry` answers
`adminLimitExceeded` for the whole search rather than returning a truncated member
list that would misreport who is in the group.

**`memberOf`** is the reverse, and is **off by default** (`MemberOfEnabled`). Two
limits are worth stating plainly:

- It is resolved only when the client actually asks for it, since it costs an extra
  bounded query.
- It is **absent for a direct one-to-many** relationship. That kind carries its
  back-reference on the member row, which a search does not project; publishing it
  would require guessing a second single-column key. An absent attribute is honest,
  whereas a partially-correct one would understate the groups an entry belongs to.

A **composite-key** membership relationship is refused: a many-to-many with a
composite bridge is a startup failure, and a composite multi-link resolves nothing
rather than joining on a partial key.

**The subschema** is generated from the entry families the session could actually
reach, using the same index the search path resolves against — introspection is
filtered by the same authorization as the data path, never by a separate, weaker
notion of visibility. Credential columns are absent structurally, because an
attribute type reaches the subschema only by being a published attribute and the
parser refuses to publish a credential column at all.

## Search bounds and paged results

Every bound is the **minimum** of the server's ceiling and what the client asked
for, so a client can narrow a limit and never raise one. Reaching a limit is
reported rather than silently truncating: `sizeLimitExceeded` for the result
ceiling, `timeLimitExceeded` for the duration, `adminLimitExceeded` for the
membership fan-out. A truncated result that looks complete is worse than an
explicitly partial one.

**Paged results** (RFC 2696, OID `1.2.840.113556.1.4.319`) is the only request
control this front door implements. A page larger than `MaxPageSize` is narrowed.
The response carries a resume cookie; an **empty** cookie is the protocol's own
"that was the last page", so a client always learns where it stands.

The cookie is integrity-protected and **bound to the search shape and the
identity** that was issued it. A cookie that is forged, tampered with, expired,
replayed by a different identity, or replayed into a different search is refused
outright with `unavailableCriticalExtension` — never silently degraded into a fresh
scan from the beginning, which would hide the tampering from the client and the
operator alike.

> **Set `PagedResultsCookieSecret` for any deployment with more than one instance.**
> Leaving it unset generates a random per-process key. That is safe — a predictable
> default would make cookies forgeable — but outstanding cookies stop validating on
> restart and across instances behind a load balancer, so a client paging through a
> result set gets a refusal partway. The unset case logs a warning at startup.

Results are ordered by the table's key columns — all of them, so a composite key
orders fully — because without a total order the database may return rows in a
different order between pages, and paging would repeat and skip entries.

## Unsupported controls

A control this server does not implement and that the client marked **critical**
makes the whole operation unserviceable: it is refused with
`unavailableCriticalExtension` and nothing executes, per RFC 4511 §4.1.11. A
non-critical unsupported control is ignored and the search runs. Server-side sort
(`1.2.840.113556.1.4.473`) and VLV are in the unsupported set.

## Tenant isolation in one namespace

There are **no per-tenant subtrees**. Every mapped entry lives in one DN namespace
under the single base DN, and isolation is enforced by the pipeline rather than by
the shape of the tree: each search runs as a query intent under the bound identity,
and the tenant, policy, and soft-delete transformers AND their predicates onto it.

The consequences are worth being explicit about, because they are what a reviewer
should check:

- Two identities issuing the **same** request text get disjoint answers.
- A search for another tenant's entry **by its exact DN** is answered identically
  to a search for a DN that does not exist. Both produce `noSuchObject` with an
  empty diagnostic, so the wire never confirms that an account exists.
- A filter naming another tenant's attribute value matches nothing.
- A `member` list never names an entry outside the caller's scope, in either
  direction of the join.
- A session that carries **no** projected identity is refused rather than run
  unscoped. This is the difference between "the pipeline scopes an empty context to
  nothing" (which it may or may not) and "this front door never runs an unscoped
  read" (which it does not).

Do not try to model tenancy as DN structure. Map the tables and let the pipeline
scope them.

## Result codes

| Condition | Result code |
|-----------|-------------|
| Success | `success` (0) |
| Malformed request control | `protocolError` (2) on the operation; the session survives |
| Fatal wire violation / unknown protocolOp | `protocolError` (2) as a Notice of Disconnection, then close |
| Search hit the result ceiling | `sizeLimitExceeded` (4), with the entries found so far |
| Search hit the time ceiling | `timeLimitExceeded` (3) |
| Membership fan-out over `MaxMembersPerEntry` | `adminLimitExceeded` (11) |
| Unsupported **critical** control, or a bad/forged/expired paging cookie | `unavailableCriticalExtension` (12) |
| Credentialed bind on a cleartext transport | `confidentialityRequired` (13) |
| Any bind failure whatsoever | `invalidCredentials` (49) |
| Search before binding; anonymous session reaching for data; session with no projected identity | `insufficientAccessRights` (50) |
| StartTLS with no certificate configured | `unavailable` (52) |
| StartTLS out of order | `operationsError` (1) |
| Unsupported extended operation; bind or search on a listener with no seam to serve it | `unwillingToPerform` (53) |
| Base names nothing published, is malformed, is outside the base DN, or names a row this identity cannot see | `noSuchObject` (32) |
| Any internal fault | `operationsError` (1), diagnostic blanked |

**Diagnostics are sanitized.** An internal exception can wrap raw driver or
transformer text — qualified table names, context-key names — so it is logged
server-side and the wire gets a result code and nothing else. A denial names
neither the table nor the column nor the context key it was denied by. The one
message class forwarded verbatim is the adapter's own protocol text, which is built
from the caller's own request.

## Non-goals

The LDAP front door is deliberately bounded. Explicitly **not** provided:

- **No writes.** Add, modify, delete, and modifyDN do not exist on this wire. This
  is a directory *view* of your tables; change them through GraphQL or the mutation
  pipeline.
- **No SASL, no Kerberos/GSSAPI.** Simple bind over a confidential transport is the
  only authentication mechanism; a SASL bind choice is refused with the same uniform
  `invalidCredentials`, and every non-StartTLS extended operation with
  `unwillingToPerform`.
- **No replication.** There is no replica agreement, no changelog, no `syncrepl`.
- **No referrals or chaining.** A base outside the configured namespace is
  `noSuchObject`, never a referral to somewhere else.
- **Not Active Directory.** No AD schema, no domain controller behaviors, no
  `sAMAccountName`/`objectSid` semantics, no group policy, no LDAP ping.
- **No ACIs / no directory-side access control.** Authorization is the BifrostQL
  policy and tenant model, applied by the pipeline. There is no per-entry ACL to
  configure here, and nothing in LDAP configures one.
- **No extensibleMatch, no server-side sort, no VLV, no persistent search.**
- **No schema modification.** The subschema is generated from your mapping
  metadata and is read-only.

## Connecting clients

### ldapsearch

```bash
# Discovery: the RootDSE is readable without binding when anonymous bind is enabled.
ldapsearch -H ldap://localhost:389 -x -s base -b "" "(objectClass=*)"

# A real read, over StartTLS on the cleartext port.
ldapsearch -H ldap://localhost:389 -ZZ \
  -D "uid=ada,ou=people,dc=example,dc=com" -W \
  -b "ou=people,dc=example,dc=com" "(uid=ada)" uid cn mail

# The same, over LDAPS.
ldapsearch -H ldaps://localhost:636 \
  -D "uid=ada,ou=people,dc=example,dc=com" -W \
  -b "dc=example,dc=com" "(objectClass=inetOrgPerson)"

# Paged, for a large result set.
ldapsearch -H ldaps://localhost:636 -E pr=100/noprompt \
  -D "uid=ada,ou=people,dc=example,dc=com" -W \
  -b "ou=people,dc=example,dc=com" "(objectClass=*)"
```

`-ZZ` requires StartTLS to succeed rather than falling back to cleartext; use it
rather than `-Z`. A credentialed bind without `-ZZ` or `ldaps://` is refused with
`Confidentiality required (13)`, which is the gate doing its job.

### Grafana

Grafana authenticates users by binding as a service account, searching for the
user, then rebinding as that user. Point it at the LDAPS port:

```toml
# grafana.ini
[auth.ldap]
enabled = true
config_file = /etc/grafana/ldap.toml
```

```toml
# /etc/grafana/ldap.toml
[[servers]]
host = "bifrost.internal"
port = 636
use_ssl = true
start_tls = false
ssl_skip_verify = false
root_ca_cert = "/etc/grafana/bifrost-ca.crt"

bind_dn = "uid=grafana,ou=people,dc=example,dc=com"
bind_password = "${GRAFANA_LDAP_BIND_PASSWORD}"

search_filter = "(uid=%s)"
search_base_dns = ["ou=people,dc=example,dc=com"]

[servers.attributes]
username = "uid"
name = "cn"
email = "mail"

# Group mappings require the group search below to return entries this
# service account can see; memberOf is off by default on the BifrostQL side.
[[servers.group_mappings]]
group_dn = "cn=admins,ou=groups,dc=example,dc=com"
org_role = "Admin"

[[servers.group_mappings]]
group_dn = "*"
org_role = "Viewer"
```

Two things to get right, both consequences of the sections above:

- The **service account is a real directory identity**, so what Grafana can find is
  what that identity's tenant and policy scope permits. A service account scoped to
  one tenant cannot authenticate users of another — which is usually what you want,
  and always what you get.
- **`group_search_filter` needs `member`**, which requires the group table to
  declare `ldap-member`. Grafana's `memberOf`-style mapping needs
  `MemberOfEnabled = true` on the BifrostQL side, and does not work for a direct
  one-to-many membership relationship (see above).

Validating a real Grafana login end to end is the
[LDAP Smoke Runbook](/BifrostQL/guides/ldap-smoke/).

## See also

- [Protocol Adapters (concept)](/BifrostQL/concepts/protocol-adapters/) — why an adapter owns only its wire and codec.
- [Authoring a Protocol Adapter](/BifrostQL/guides/protocol-adapters/) — the intent APIs and conformance kit this front door is built on.
- [LDAP Smoke Runbook](/BifrostQL/guides/ldap-smoke/) — validate `ldapsearch` and Grafana end to end.
- [PostgreSQL Wire Protocol (pgwire)](/BifrostQL/guides/pgwire/) — the sibling read-only SQL front door.
- [Authentication](/BifrostQL/guides/authentication/) — how principals and claims are mapped.
- [Multi-Tenant Org Model](/BifrostQL/guides/org-model/) — the tenant model the directory is scoped by.
