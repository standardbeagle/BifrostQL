---
title: "LDAP Smoke Runbook"
description: "Manually verify that ldapsearch and a real Grafana LDAP login can reach the BifrostQL LDAP endpoint over a real network socket, complete a bind over LDAPS and StartTLS, read entries, and stay tenant-isolated — the end-to-end path the automated tests deliberately do not fake."
---

The [LDAP front door](/BifrostQL/guides/ldap/) answers LDAPv3 on a TCP port and
serves your mapped tables as directory entries, with every read executing through
the same transformer pipeline as GraphQL.

This page is the **manual** end-to-end smoke. It is deliberately **not** part of
`dotnet test`, because it needs a real `ldapsearch` binary and a real Grafana, and
a test that silently passed when neither was installed would be worse than no test
at all.

> **Honesty note.** The automated suite proves a great deal, over real loopback
> sockets and a real Kestrel host, but it proves it against BifrostQL's own client:
> `LdapListenerEndToEndTests` binds and searches over both the LDAPS port and a
> StartTLS upgrade against a running host; `LdapSearchWireTests` drives search,
> paging, cross-tenant isolation, and the filter grammar over a loopback socket;
> `LdapBindWireTests` covers the bind result surface; and
> `LdapProtocolAdapterConformanceTests` runs the shared adapter security-conformance
> kit through the wire. What none of them prove is **interoperability**: that
> OpenLDAP's client library and Grafana's LDAP stack accept our BER, our TLS, and
> our result codes. That is what the steps below are for.
>
> At the time of writing, these steps have **not** been executed — `ldapsearch` is
> not installed in the development environment this guide was written in, and no
> Grafana instance was available. If you have not run them yourself, do not claim
> the LDAP smoke passed.

## Prerequisites

- OpenLDAP client tools: `ldapsearch`, `ldapwhoami` (Debian/Ubuntu:
  `apt-get install ldap-utils`; macOS: bundled, or `brew install openldap`).
- A server certificate the client will trust. A self-signed certificate is fine if
  you pass its CA to the client explicitly — do **not** reach for
  `TLS_REQCERT never`, which would hide exactly the failure this step tests.
- Two accounts in **different tenants**, both able to bind. The tenant-isolation
  step is the one that cannot be faked from a single login.
- Grafana 10 or later, for the second half.

Example server wiring:

```csharp
builder.Services.AddBifrostLdap(o =>
{
    o.Port = 389;
    o.LdapsPort = 636;
    o.TlsCertificatePath = "/etc/bifrost/ldap.pfx";
    o.TlsCertificatePassword = Environment.GetEnvironmentVariable("LDAP_PFX_PASSWORD");
    o.Endpoint = "/graphql";
    o.PagedResultsCookieSecret = Environment.GetEnvironmentVariable("LDAP_COOKIE_SECRET");
    o.AnonymousBindEnabled = true;   // for the RootDSE step; optional
    o.MemberOfEnabled = true;        // only if you will test Grafana group mappings
    // o.BindAddress stays loopback unless you deliberately widen it.
});

builder.Services.AddSingleton<ILdapCredentialStore, MyDirectoryCredentialStore>();
builder.Services.AddSingleton<ILdapPasswordHasher, MyArgon2idHasher>();
```

With the model mapped as in the
[LDAP guide](/BifrostQL/guides/ldap/#mapping-tables-into-the-directory).

## Step 1 — Discovery without binding

```bash
ldapsearch -H ldap://localhost:389 -x -s base -b "" "(objectClass=*)"
```

**Expected:** the RootDSE, listing `namingContexts` (your base DN),
`supportedLDAPVersion: 3`, `subschemaSubentry: cn=subschema`, and
`vendorName: BifrostQL`. Then:

```bash
ldapsearch -H ldap://localhost:389 -x -s base -b "cn=subschema" "(objectClass=*)"
```

**Expected:** the subschema entry exists but is a **bare skeleton** — no
objectClasses, no attributeTypes. That is the anonymous session's ceiling, not a
bug. Finally:

```bash
ldapsearch -H ldap://localhost:389 -x -b "ou=people,dc=example,dc=com" "(objectClass=*)"
```

**Expected:** `insufficientAccessRights (50)`. An anonymous session never reaches
directory data. If this returns entries, stop — the anonymous ceiling is broken.

## Step 2 — The cleartext gate

```bash
ldapsearch -H ldap://localhost:389 \
  -D "uid=ada,ou=people,dc=example,dc=com" -w "$ADA_PASSWORD" \
  -b "ou=people,dc=example,dc=com" "(uid=ada)"
```

**Expected:** `Confidentiality required (13)` and **no entries**. This is the gate
working: a credentialed bind is refused on a cleartext transport before the
password is even resolved. If this succeeds, check whether
`AllowInsecureSimpleBind` has been left enabled — it is development-only and logs a
warning at every startup.

## Step 3 — StartTLS on the cleartext port

```bash
LDAPTLS_CACERT=/path/to/ca.crt ldapsearch -H ldap://localhost:389 -ZZ \
  -D "uid=ada,ou=people,dc=example,dc=com" -w "$ADA_PASSWORD" \
  -b "ou=people,dc=example,dc=com" "(uid=ada)" uid cn mail
```

**Expected:** exactly one entry, DN `uid=ada,ou=people,dc=example,dc=com`, carrying
`uid`, `cn`, and `mail`. `-ZZ` (not `-Z`) makes StartTLS mandatory, so a silent
fallback to cleartext fails the command instead of passing the test.

**Also expected:** no attribute anywhere in the output resembles a password hash.
Grep for it explicitly — `| grep -i -e pass -e '\$2y\$' -e argon2` should find
nothing.

## Step 4 — LDAPS on its own port

```bash
LDAPTLS_CACERT=/path/to/ca.crt ldapsearch -H ldaps://localhost:636 \
  -D "uid=ada,ou=people,dc=example,dc=com" -w "$ADA_PASSWORD" \
  -b "dc=example,dc=com" "(objectClass=inetOrgPerson)" uid cn
```

**Expected:** the same entries Step 3 returned for the same identity. The two
confidential routes converge on one session loop, so a difference between them is a
defect, not a configuration nuance.

```bash
LDAPTLS_CACERT=/path/to/ca.crt ldapwhoami -H ldaps://localhost:636 \
  -D "uid=ada,ou=people,dc=example,dc=com" -w "$ADA_PASSWORD"
```

**Expected:** an `unwillingToPerform` result — `whoami` is an extended operation
this front door does not implement, and refusing it cleanly (rather than hanging or
dropping the connection) is the behavior under test.

## Step 5 — Tenant isolation across two logins

Run the **same** search twice, changing only the bind DN:

```bash
for who in ada:"$ADA_PASSWORD" bob:"$BOB_PASSWORD"; do
  user="${who%%:*}"; pass="${who#*:}"
  echo "== $user =="
  LDAPTLS_CACERT=/path/to/ca.crt ldapsearch -LLL -H ldaps://localhost:636 \
    -D "uid=$user,ou=people,dc=example,dc=com" -w "$pass" \
    -b "dc=example,dc=com" "(objectClass=*)" dn
done
```

**Expected:** two **disjoint** DN lists. No DN appears under both identities. Then
the sharper check — ask one identity for the other's entry by its exact DN:

```bash
LDAPTLS_CACERT=/path/to/ca.crt ldapsearch -LLL -H ldaps://localhost:636 \
  -D "uid=ada,ou=people,dc=example,dc=com" -w "$ADA_PASSWORD" \
  -s base -b "uid=bob,ou=people,dc=example,dc=com" "(objectClass=*)"

LDAPTLS_CACERT=/path/to/ca.crt ldapsearch -LLL -H ldaps://localhost:636 \
  -D "uid=ada,ou=people,dc=example,dc=com" -w "$ADA_PASSWORD" \
  -s base -b "uid=nobody-at-all,ou=people,dc=example,dc=com" "(objectClass=*)"
```

**Expected:** both commands answer **identically** — `No such object (32)` with an
empty diagnostic. A real-but-foreign entry must be indistinguishable from one that
does not exist. Any difference between these two outputs is an existence oracle and
a real defect.

## Step 6 — Paged results

```bash
LDAPTLS_CACERT=/path/to/ca.crt ldapsearch -H ldaps://localhost:636 \
  -E pr=50/noprompt \
  -D "uid=ada,ou=people,dc=example,dc=com" -w "$ADA_PASSWORD" \
  -b "ou=people,dc=example,dc=com" "(objectClass=*)" dn
```

**Expected:** the full result set, walked in pages of 50, ending cleanly. Count the
DNs and confirm there are no duplicates and no gaps against a known row count.
`ldapsearch` handles the cookie itself; what you are testing is that our cookie
survives its round trip.

If you run more than one BifrostQL instance behind a load balancer, run this step
through the balancer. A page that fails partway with
`Critical extension is unavailable (12)` means `PagedResultsCookieSecret` is unset
or differs between instances.

## Step 7 — Grafana login

Configure Grafana as in the
[LDAP guide](/BifrostQL/guides/ldap/#grafana), then:

```bash
grafana cli admin ldap-status      # or the Server Admin → Authentication page
```

**Expected:** the connection to port 636 reports available.

Then, from the Grafana UI's **Server Admin → Users → LDAP** debug page, look up a
user by their `uid`.

**Expected:** the bind DN resolves, the user's `cn` and `mail` come back, and the
mapped org role matches your `group_mappings`. Finally, log in through the Grafana
sign-in form as that user.

**Expected:** the login succeeds and the user lands with the mapped role.

**Also expected, and worth checking explicitly:** a user belonging to the *other*
tenant cannot be found by this service account at all. Grafana's service account is
a real directory identity, so its tenant scope bounds who it can authenticate.

## What to record

For a release smoke, note for each step: ran (y/n), passed (y/n), and the client
versions (`ldapsearch -VV`, Grafana version). Record specifically:

- Step 2 refused with `13`, not a success.
- Step 5's two lookups produced byte-identical output.
- Step 3's output contained no hash-like attribute.

Anything that fails is a real defect — do not paper over it, and do not relax the
client (`TLS_REQCERT never`, dropping `-ZZ`) to make a step pass.

## Troubleshooting

- **`Can't contact LDAP server (-1)` on 636 but 389 works.** The LDAPS listener did
  not bind. `LdapsPort` requires a certificate; without one, registration aborts —
  check the startup log rather than the client.
- **`Confidentiality required (13)` when you expected success.** The connection is
  not confidential. `-Z` allows a silent fallback; use `-ZZ`, or `ldaps://`.
- **`Server is unavailable (52)` from StartTLS.** No certificate is configured, so
  the listener is cleartext-only and says so rather than pretending to upgrade.
- **`Operations error (1)` from StartTLS.** It was sent out of order — after a bind,
  or on a connection that is already TLS. StartTLS belongs before the bind.
- **`Invalid credentials (49)` for a password you are sure of.** Check for a tripped
  rate limit: `MaxBindAttemptsPerAccount` defaults to 10 per minute, and a
  rate-limited attempt is deliberately indistinguishable from a wrong password on
  the wire. Your `ILdapBindObserver` audit records distinguish them server-side.
- **`Insufficient access rights (50)` after a successful bind.** The bound principal
  projected to an empty user context, or the session is anonymous and reached for
  data. Check the claims your `ILdapCredentialStore` puts on the principal.
- **`No such object (32)` for a DN you can see in the database.** Either the row is
  outside the bound identity's tenant/policy scope, or the base DN does not match
  the template. Both answer identically by design — check the mapping first.
- **A search returns zero entries with `success`.** Likely an `extensibleMatch`
  filter, which is unsupported and matches nothing rather than everything.
- **`Size limit exceeded (4)`.** The server ceiling (`MaxSearchResults`, default
  1000) was reached. The entries returned are real; the set is partial. Page instead.
- **`Admin limit exceeded (11)`.** A group has more members than
  `MaxMembersPerEntry`. The search is refused rather than returning a truncated
  member list.

## See also

- [LDAP Directory Endpoint](/BifrostQL/guides/ldap/) — the full operator guide.
- [pgwire BI-Tool Smoke Runbook](/BifrostQL/guides/pgwire-bi-smoke/) — the sibling manual smoke.
- [RESP Smoke Runbook](/BifrostQL/guides/resp-smoke/) — the same shape for the Redis front door.
