---
paths:
  - "src/BifrostQL.Server/**"
  - "src/BifrostQL.Mcp/**"
  - "src/BifrostQL.Aws/**"
  - "src/BifrostQL.Core/Resolvers/**"
  - "src/BifrostQL.Core/Modules/**"
  - "src/BifrostQL.Core/Storage/**"
  - "tests/BifrostQL.AdapterConformance/**"
  - "tests/BifrostQL.Server.Test/**"
  - "tests/BifrostQL.Mcp.Test/**"
---

# Protocol adapter security invariants

Review checklist for every front door (`IProtocolAdapter` and HTTP mounts). Code comments cite these by number. Keep the numbers stable. Incident write-ups: `docs/solutions/bifrostql/`; full pre-trim text: `git show 58d13dfb:.claude/rules/protocol-adapter-security.md`.

1. **Protocol exceptions derive from the base the connection handler catches** (or are added to its filter). Untrusted regex uses `NonBacktracking` + `Regex.InfiniteMatchTimeout`. A wall-clock match timeout on a hot path turns host load into client errors. Use one only on cold backtracking paths, mapped to the adapter's exception. Pin with a reflection fact over the type's static `Regex` fields.

2. **Constant-time compares run unconditionally.** Compare first, then AND the existence check. Decoys must match real users on every observable, not just timing: a deterministic per-username salt and iteration count (`PgScramVerifier.Decoy(username)`). Fact: two connections for the same unknown user get byte-equal server-first messages.

3. **Never forward internal exception text (`BifrostExecutionError.Message`, driver text, transformer text) onto a wire.** Forward verbatim only an adapter-owned type whose every throw site builds text from the caller's own arguments or the projected schema. A comment saying "already wire-safe" is the tell, so read the throw sites. Client-supplied name lookups use `TryGet*` and map a miss to an identifier-free error. Keep a stable machine code and a retry/no-retry category.

4. **Introspection/metadata is filtered by the data path's own evaluator** (`PolicyEvaluator.CanAct` / `IsColumnAllowed`, via `SchemaReadVisibility`), fail-closed. Never a weaker "it's just metadata" check.

5. **Decoding typed values from wire bytes catches the whole parse family** (`FormatException`, `OverflowException`, `ArgumentException`). It also passes an explicit `NumberStyles` (`Integer` / `Float`) AND an explicit culture: the provider picks glyphs, the style picks grammar.

6. **Recursive decoders cap nesting depth before each descent.** Reset the counter only at a top-level frame. Stack overflow cannot be caught. Never pre-allocate from a declared count; grow incrementally. A stream shorter than its declared total is a protocol error, never a shorter message.

7. **Adapter writes go only through `IMutationIntentExecutor`.** The adapter supplies the key and the `UserContext`, never a predicate. It routes the intent the operation means (a column-valued object deletes as update-to-NULL), and the pipeline decides hard vs soft delete. Dangerous write opt-ins default OFF, are the first check in the handler, and log a startup warning. Write-path table resolution uses the full schema, not read visibility; only the did-you-mean prompt is scoped.

8. **Key-addressed writes:**
   (a) With compensation, decouple the deterministic address from the storage key: a fresh key per write, with the row pointer binding them.
   (b) `.Value` is not a count. Use `AffectedRows`, and null is not success.
   (c) Build the predicate from pre-chain client columns via `SelectPredicateColumns`. An inferred target hitting 0 rows throws and rolls back. An empty predicate is an error. Irreversible external effects run after commit. A deferral (approval) is not a failure, so don't compensate it.
   (d) A read that gates a write carries the write's scope: a filter-only probe through the chain, excluding the concurrency token. Events fire only on `AffectedRows > 0`.
   (e) Storage writes persist a locator, never a capability. Signed URLs are minted on read with a server-clamped TTL. Remove the property, don't null it.
   Fixtures span composite PK, single PK, PK value `0`, and pre-existing state at the target.

9. **Sibling op classes on one seam catch the identical exception set.** Pre-pipeline shape checks (lookup, key parse, arity) answer exactly like a pipeline denial: resolve via `SchemaReadVisibility` and use one constant response writer. Naming a field explicitly and reaching it by wildcard must resolve through the same projection. Exception: a denied column used in a filter is refused, never silently dropped. Assert body equality, not just status. Multi-slice epics need an epic-close review of the seam.

10. **One error funnel per request dispatch, plus consistent condition tagging upstream.** The same condition carries the same code on the read and write paths; map by condition, never by op class. Parity facts assert status AND the message string as a literal.

11. **Identity-less surfaces (scrape, webhook, export) never default to cross-tenant aggregates.** Opt-in and default off. They require a credential AND an explicit mode: a fixed service identity through `IBifrostAuthContextFactory`, or tenant-label partitioning. Misconfiguration emits nothing. Cache keys include mode and partition. Self-metric labels are enum-only.

12. **A security mode's name is a claim, and an empty user context is not a refusal.** Pin it with a RED fact on a table without tenant metadata, on every transport the mode covers. When a throw becomes a result type, the failure arm maps to a refusal. A seam with no wire throws `BifrostIdentityRejectedException`. Flipping a default means sweeping every doc snippet. Host security facts use `UseEnvironment("Production")`, and `UseSetting` for switches read before build.

13. **Memoize identity per request, never per session.** Never memoize a failure. Fact: one resolution within a call, two across two calls.

14. **Engine-only privileges are sealed reference tokens with no public constructor or factory** (`MutationRestoreCapability`, `HistoryErasure.Marker`, `ApprovalInterceptMutationHook.MarkApprovedReplay`, `WorkflowTriggerSuppression`). Gate on the token before shaping. The doc comment names the real mint boundary (the `InternalsVisibleTo` list). An operator-grantable privilege (`_hardDelete`) gates on its declaration in both the SDL and a runtime backstop.

15. **A pre-auth deadline is retired only by a credentialed action, never re-armed by a free one.** Free actions: anonymous bind, ping, StartTLS, negotiation, cancel. Only a credentialed→anonymous transition arms a fresh window. Use one owned `CancellationTokenSource(timeout, TimeProvider)` linked to the connection, never a detached `Task.Delay`. Fact: a loop of free actions on an injected clock, with the connection closed during the loop.

16. **When security depends on host wiring order, pin the fail-closed outcome** with a fact parameterized on order (misorder closes the connection; correct order serves). Add a startup diagnostic only if it has no false positive on any valid host.
