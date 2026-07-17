---
written_at: 2026-07-17T19:52:00Z
source_event: task:01KXHR3116R4N68T7PSG4YSGZJ
module: bifrostql
category: best-practices
confidence: high
sources:
  - task:01KXHR3116R4N68T7PSG4YSGZJ
  - git:dbe3abe
tags: [protocol-adapter, mcp, credential-store, sync-over-async, oidc, fail-closed, error-handling-on-the-wire]
status: steering
recurrence: 1
---

# Sync-over-async bridge at a seam is interim, not permanent — it must be named and scheduled to dissolve

**Lesson.** MCP auth slice D added `IMcpCredentialStore` (the MCP analogue of
`IPgCredentialStore`, `src/BifrostQL.Server/Pgwire/IPgCredentialStore.cs`) —
a real OIDC token exchange, which is genuinely async (network I/O to the
IdP). It plugs into `BifrostMcpServerFactory`'s `userContextProvider` seam,
which is `Func<IDictionary<string,object?>>` — synchronous, because it
predates any transport that needs it to be otherwise (stdio only, no HTTP
transport slice yet). The implementer bridged with
`.GetAwaiter().GetResult()` at the single `ValidateBearer` call site in
`src/BifrostQL.Mcp/BifrostMcpAdapter.cs`. Review (pass_with_changes, 0
blockers, 1 advisory, no rewind) confirmed the bridge is safe *today*
(`StdioServerTransport` has no `SynchronizationContext`, so no deadlock is
reachable) but flagged it as load-bearing only because of that specific
transport fact — it is not safe by construction, it is safe by the current
absence of an HTTP transport.

**Why this is durable, not a one-off.** The task itself documents the
expiry condition in its own commit message: "When the HTTP transport slice
makes the provider async, that bridge should dissolve." That is a
correctness fact about a future slice (MCP HTTP transport), not a stylistic
preference — if a future implementer widens `.GetAwaiter().GetResult()`
usage, adds a second sync-over-async bridge elsewhere in the adapter, or
simply forgets this one exists when the provider signature changes to
`Func<Task<IDictionary<string,object?>>>`, the deadlock-safety argument
silently stops holding (a real `SynchronizationContext`-bearing host, e.g.
ASP.NET Core Kestrel behind the HTTP transport, reintroduces exactly the
deadlock class this review ruled out). This generalizes beyond MCP: any
adapter seam where an owned-elsewhere synchronous contract (`Func<T>`, not
`Func<Task<T>>`) is bridged to a newly-async implementation is provisional
by construction, and the bridge's safety argument must be re-derived — not
assumed to still hold — the moment either side of the seam changes.

**Second, distinct finding: throw-vs-null contract discipline at a
credential-store boundary.** `IMcpCredentialStore`'s contract (mirroring
`IPgCredentialStore`) is null-on-failure, never an ambient principal — and
that half is enforced and tested. But the contract is silent on what
happens if the store *throws* (e.g. `HttpRequestException` from IdP
network I/O) rather than returning null. Today it rethrows unwrapped past
`CallToolAsync`'s narrow catch (`ToolPromptException` /
`BifrostExecutionError`) to the MCP SDK as a JSON-RPC fault instead of the
intended clean "Tenant context required" fail-closed message. Reviewer
accepted this as non-blocking (fail-closed direction: no identity minted,
no tenant leak; not a regression — the pre-existing sync
`ValidateBearerToken` delegate has the same gap) but it is the same defect
class as protocol-adapter-security.md invariants 1 and 5: a caught-type
allow-list at a wire/tool boundary that doesn't cover the full exception
family a real I/O-backed implementation can throw. A store contract that
specifies the null path but not the throw path leaves every future adapter
of that contract to independently rediscover the gap.

**Apply when.** (a) Any protocol-adapter seam bridges a provider-owned
synchronous contract to a genuinely async implementation (I/O-backed
credential/token/lookup stores are the recurring case) — treat the bridge
as provisional, comment it with the exact condition that dissolves it (as
this slice did), and add a tracking note/task so the next slice that makes
the seam's contract async is the one that removes the bridge rather than
stacking a second one beside it. (b) Any credential/identity-store
interface (`I*CredentialStore`) that documents null-on-failure should also
state — and test — what happens when the implementation throws instead:
either the caller's catch is widened to the full exception family the
implementation can realistically throw (per invariant 5's pattern), or the
interface contract requires implementations to catch-and-return-null
themselves, but pick one and make it explicit rather than leaving it
implicit in "well the null path is fail-closed so it's probably fine."

**Cross-references.** `.claude/rules/protocol-adapter-security.md`
invariant 5 (catch the full parse/IO-exception family at wire boundaries,
not just the obvious one) and invariant 1 (custom exceptions must derive
from the caught base) are the closest existing invariants to finding (b)
above but neither was written with a *store dependency's* thrown exception
in mind — both are about decode/protocol-exception types the adapter
itself defines. This is a new, narrower case: a third-party-shaped
dependency (a credential store) thrown exception escaping a tool-call catch.
`docs/solutions/bifrostql/mcp-write-path-spine-2026-07-15.md` (recorded on
`feat/mcp-slice4-bifrost-mutate`, not yet on main) established the sibling
lesson for MCP write paths: sanitize-boundary functions and catch
allow-lists are the two places a clean, reviewed adapter slice quietly
drifts. This doc extends that observation to the credential-store/identity
seam specifically, plus the new sync-over-async-bridge-has-an-expiry-date
lesson, which has no prior write-up in this workspace.

---

## UPDATE 2026-07-17T21:00:00Z — bridge dissolved, resolution pattern banked

**source_event:** task:01KX7ZNT4NVCWF2T6PMKQKWSD6 (MCP slice 5), commits
bb128b2 (`feat(mcp): host the MCP server over Streamable HTTP with
per-session identity`), 74fa702, a3719e6, 8a37ce8.

The tracking condition this doc named ("when the HTTP transport slice makes
the provider async, that bridge should dissolve") fired exactly as
predicted, and slice 5 removed the `.GetAwaiter().GetResult()` bridge for
the HTTP path rather than stacking a second sync-over-async bridge beside
it — confirming the "name the expiry condition" half of this lesson's advice
paid off. The concrete resolution, verified by an independent
correctness-review pass (pass, high confidence, 0 blockers) that explicitly
re-derived the deadlock-safety argument rather than trusting the diff:

**Pattern: resolve-once-per-session, snapshot to a frozen dict, sync
provider returns a plain copy.** `BifrostMcpHttpExtensions.ConfigureSessionAsync`
(`src/BifrostQL.Mcp/BifrostMcpHttpExtensions.cs:84-114`) runs once per MCP
session, at the `initialize` request, while that request's ASP.NET request
scope (and its `SynchronizationContext`) is still live:

1. The bearer is extracted from *that request's* `Authorization` header.
2. The genuinely-async credential exchange
   (`BifrostMcpAdapter.ResolveBearerPrincipalAsync`) is `await`ed directly —
   `ConfigureAwait(false)`, but still a real `await`, never
   `.GetAwaiter().GetResult()`.
3. The resolved principal is projected through the shared
   `IBifrostAuthContextFactory` **synchronously, exactly once**, and the
   result is snapshotted into a frozen dictionary
   (`BifrostMcpAdapter.CreateProjectionProvider`).
4. The `userContextProvider` seam — still `Func<IDictionary<string,object?>>`,
   unchanged sync contract — is satisfied by a provider that just returns a
   **plain copy of the already-computed frozen dict** on every subsequent
   tool call for that session. No async work, no blocking-wait, happens on
   any later request.

This is the general answer to "an existing seam is a sync `Func<T>` but the
new dependency behind it is genuinely async, and the seam is called
per-call inside a host that has a real `SynchronizationContext`
(ASP.NET/Kestrel)": don't widen the seam to `Func<Task<T>>` and don't bridge
with a blocking wait at each call site. Instead, find the coarser-grained
lifecycle boundary the seam's value is actually stable across (here: one
value per MCP session, not per tool call), do the async work once at that
boundary while a request scope legitimately owns the async context, and let
every narrower-grained sync call site read a cheap immutable snapshot. It
works because MCP HTTP identity is fixed for the life of a session by the
bearer presented at `initialize` — the technique applies wherever the
async-derived value's lifetime is provably coarser than the sync seam's call
frequency; it is not a general substitute for `Func<Task<T>>> when the value
can legitimately change per call.

**Confirms, doesn't just restate, the original lesson's core claim:** a
sync-over-async bridge at a seam is provisional, and naming its exact
dissolve condition in the docs/commit message is what let a *different*
implementer (slice 5, six days later) resolve it correctly on the first
attempt instead of rediscovering the deadlock risk or papering over it with
a second `.GetAwaiter().GetResult()`.

No new blocker or gap found in this slice's identity handling; the
throw-vs-null credential-store gap flagged as finding (b) above is
unchanged by this update — HTTP path still relies on the same
`ResolveBearerPrincipalAsync`/`UnmappedOidcIssuerException` sanitization
invariant 3 already covers, not a new fix.
