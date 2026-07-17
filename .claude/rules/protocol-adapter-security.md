---
title: Protocol adapter security invariants (exception routing + timing safety + error sanitization)
created: 2026-07-13T22:04:51Z
updated: 2026-07-17T13:40:00Z
source:
  - task: 01KXEB9NN448K3KTR0JK6M131C
  - commit: 4a568ce44ded5b5f871fc9f5adcefa3ca8ec002f
  - review: correctness-reviewer verdict on pgwire slice-1 handshake
  - task: 01KXEBB2RHJN89G1KP4PXJADCH
  - commit: 8fa4f847879678e938d1d4db5c016e897c7585ba
  - review: correctness-reviewer verdict on pgwire slice-2 simple query
  - task: 01KXEBB73JDTEZQ4QKT6S9T4CB
  - commit: 9dd3c9b
  - commit: 0439525
  - commit: abca8d7
  - task: 01KXEBB9EVJ6M621VDBY368NW4
  - commit: 2a31036
  - review: correctness-reviewer verdict on pgwire slice-5 extended query (rewind)
  - task: 01KXEBBPR2MBVK2N1R6WYK517M
  - commit: d32d7c5562f545732a6756343ce491bc91420a0d
  - review: correctness-reviewer verdict on resp slice-1 codec/auth (rewind)
  - task: 01KXEBC2FVZ1K7AK38SMPRZKNK
  - commit: 148bc46
  - commit: 9636c60
  - review: correctness-reviewer verdict on resp slice-5 writes (clean, PASS)
scope: project (all IProtocolAdapter implementations, not just pgwire)
---

Two recurring review findings from the pgwire slice-1 handshake+auth review,
generalized to every non-GraphQL front door built on `IProtocolAdapter`
(see AGENTS.md "Request Flow" / protocol adapters section):

1. **Custom protocol exceptions must derive from the type the connection
   handler's catch clause already filters on.** A new exception type that
   only extends `Exception` (not the handler's caught base, e.g.
   `PgProtocolException`) escapes unhandled to the host (Kestrel) on
   adversary-controlled malformed input — ungraceful, and noisy at
   error-level, even when it doesn't reach a fail-open state. When adding a
   new protocol-violation exception type to any adapter, either derive it
   from the handler's existing caught base or add it to the catch filter
   before merging.

2. **Constant-time/anti-enumeration comparisons must run unconditionally.**
   `return login is not null && CryptographicOperations.FixedTimeEquals(...)`
   short-circuits the compare on the null-check, silently defeating the
   documented anti-enumeration property for unknown users. Any auth path
   built around a decoy-secret/fixed-time-compare pattern must execute the
   compare first (or unconditionally), then AND the null/existence check —
   never gate the compare behind it.

Both were caught by review, not by tests, on the pgwire slice; treat them as
a checklist item for review of any new protocol adapter's handshake/auth
code, not just re-checks of pgwire.

3. **Never forward `BifrostExecutionError.Message` (or any Bifrost-internal
   exception message) verbatim onto a client-facing protocol wire.** The
   pgwire slice-2 error mapper initially forwarded it on an unverified
   "sanitized at source" assumption. Verifying the throw sites disproved
   this: `FromDatabaseException` can wrap raw driver/DB text (schema names,
   identifiers), and `ConnectionFailed`/`SchemaError` embed caller-supplied
   detail — there is no type-level signal distinguishing a curated
   user-facing instance from a DB-wrapping one. Treat every
   `BifrostExecutionError` (and any other Bifrost-internal exception) as
   untrusted on any client-facing wire: only a deliberately user-facing type
   owned by the adapter itself (e.g. its query-translation/validation
   exception) may be forwarded verbatim. Everything else maps to a generic
   sanitized message + a generic error code; full detail is logged
   server-side only. This applies to any `IProtocolAdapter` that maps
   Bifrost errors onto a client wire, not just pgwire.

4. **Any introspection/metadata surface an adapter exposes must be filtered
   by the same authorization as its data path, fail-closed.** The pgwire
   slice-4 catalog emulation (`pg_catalog`/`information_schema` synthesized
   from `DbModel`) reuses the authoritative gate —
   `PolicyEvaluator.CanAct(PolicyConfigCollector.FromTable(t), PolicyAction.Read,
   identity)` for tables and `IsColumnAllowed(..., PolicyDirection.Read, ...)`
   for columns, via a shared `PolicyIdentity.FromUserContext` extracted from
   `PolicyFilterTransformer` (priority 1) — the SAME check enforced on the
   query path. Unparseable/unevaluable policy excludes the table/column even
   for admin (`FromTable` throws before the evaluator runs). A separate,
   weaker check for "it's just metadata" turns introspection into an
   information-disclosure side channel; never reimplement authorization for
   a metadata endpoint — call the same evaluator the data path calls. This
   applies to any `IProtocolAdapter` exposing schema/catalog/introspection
   data, not just pgwire.

5. **Decoding a typed value out of untrusted wire bytes must catch the full
   parse-exception family, not just `FormatException`.** The pgwire slice-5
   extended-protocol Bind handler decoded declared-OID text parameters
   (int2/4/8, numeric, float, uuid, bool, date/timestamp) guarding only
   `catch (FormatException)`. A well-formed but out-of-range numeric text
   value (e.g. 29 nines bound to an `int8` parameter) throws
   `OverflowException` from `long.Parse`/`decimal.Parse`/`double.Parse`, not
   `FormatException` — it escaped the decode catch, the query-loop catch,
   and the connection handler's filtered catch, reaching Kestrel unhandled
   and dropping the connection with no `ErrorResponse` (fail-open,
   driver/attacker-triggerable with one Bind message). Fixed by widening to
   `catch (Exception ex) when (ex is FormatException or OverflowException or
   ArgumentException)`. Any wire/untrusted-input decode built around a
   `.Parse`-family call (numeric, GUID, date/time, or any BCL parser) must
   catch this full family — a catch scoped to only the "obviously malformed"
   exception type is a fail-open connection-teardown bug waiting on a
   boundary value. This is the third error-handling-on-the-wire finding on
   this drain (slice-2 sanitize `BifrostExecutionError`, slice-3
   overflow→clean syntax error, slice-5 overflow→clean bind error) —
   treat "decode untrusted wire input" as needing this checklist item on
   every new protocol adapter, not just re-checks of pgwire.

6. **A recursive wire/untrusted-input decoder must bound nesting depth
   BEFORE recursing — width/size caps are not sufficient.** The RESP
   slice-1 aggregate decoder (`RespReader.ReadAggregateAsync` /
   `ReadMapAsync` / `ReadValueAsync`) had element-count and bulk-length caps
   but no depth cap. An unauthenticated peer sending a few KB of nested
   array headers (`*1\r\n` repeated) recurses one physical stack frame per
   level; once the socket has data buffered, the inner awaits complete
   synchronously, so the physical call stack — not just an async
   continuation chain — grows unbounded, producing a `StackOverflowException`.
   That exception is **uncatchable in .NET**: it escapes every catch clause
   (including the adapter's own protocol-exception filter and invariant 5's
   parse-exception family) and crashes the entire host process — every
   front door sharing that process (HTTP, pgwire, RESP) goes down together,
   not just the offending connection. This is categorically different from
   invariant 5: a parse exception can be caught after the fact; a stack
   overflow can only be *prevented*, never handled. Fix: add a
   `MaxNestingDepth` (RESP slice-1 used 32) and thread a depth counter as a
   plain parameter through the recursive read path, incrementing and
   checking it **before** each descent — reset to 0 only at each top-level
   frame, never silently reset by an intermediate helper (that would defeat
   the guard). Exceeding the cap throws the adapter's own protocol
   exception (already in the caught-base family per invariant 1), not an
   uncaught `Exception`. Fold in the adjacent allocation-amplification
   pattern found alongside it: don't pre-allocate `new T[declaredCount]`
   from an attacker-controlled length prefix before any element is read (a
   ~13-byte `*1000000\r\n` prefix forcing a multi-MB array) — grow a `List`
   incrementally instead, so a truncated/lying stream only ever materializes
   the elements that actually arrived. Any `IProtocolAdapter` with a
   recursive frame/aggregate decoder on the unauthenticated path must add a
   depth cap before merging, not just width/size caps.

7. **An adapter write path must route exclusively through
   `IMutationIntentExecutor` and must never build its own predicate.** The
   RESP slice-5 write surface (SET/HSET/DEL — the epic's first write path)
   establishes the pattern every future adapter write feature must copy:
   (a) writes go through `IMutationIntentExecutor.ExecuteAsync`
   (→ the full `TableMutationPipeline`: tenant scoping, audit actor
   resolution, soft-delete, field-encryption-on-write, CDC/history hooks) —
   never direct SQL, never `SqlExecutionManager`, never a pipeline bypass.
   (b) The adapter supplies ONLY the positional primary key plus the
   session's `UserContext` — it builds NO WHERE/predicate of its own. The
   pipeline narrows scope from the identity, so an out-of-scope PK matches
   zero rows: "caller A cannot write caller B's row" holds structurally,
   not because the adapter remembered to filter. (c) A delete command routes
   a Delete INTENT and lets the pipeline decide hard-vs-soft; the adapter
   never special-cases soft-delete itself (doing so would bypass the
   soft-delete/audit contract the table's metadata establishes). This is the
   write-side counterpart to the existing read invariant ("reads via
   `IQueryIntentExecutor`, transformers unskippable" — see AGENTS.md Request
   Flow / protocol adapters).

   Paired with this: **a dangerous opt-in write capability must default OFF
   and the gate must be the first check in the handler** — before arity
   parsing, model lookup, or intent construction — so a disabled surface
   builds zero intent and can't even be probed for behavior (fail-closed by
   construction), and enabling it must log a startup warning (a posture
   change worth surfacing). Any new `IProtocolAdapter` write command must be
   reviewed against both halves of this invariant before merging.

   Scope note from S3 slice 1: part (c) is about *routing the intent the
   operation actually means*, not about the literal `Delete` enum. Where the
   adapter's "object" is a COLUMN VALUE rather than a row (an S3 object
   stored in a file column), a Delete intent would destroy the whole row —
   the correct routing is an Update-to-NULL intent. The invariant's spirit
   (exclusively through `IMutationIntentExecutor`, adapter builds no
   predicate, pipeline decides semantics) is what binds; re-derive the right
   intent for the adapter's object model rather than copying the verb.

8. **A write-with-compensation path must never write to a caller-derived
   deterministic address; and never read a pipeline result's `.Value` as an
   affected-row count.** Two halves, both from the S3 slice-1 review — each
   was a real fail-open that a 4900-test green suite passed over.

   (a) **Address ≠ storage key.** If a seam/adapter (i) derives its write
   target deterministically from caller input, AND (ii) has a compensating
   delete/rollback on failure, then the deterministic *address* (used to
   look the object up) must be decoupled from the actual *storage key* (used
   for the write) — e.g. a fresh random key per write, with the row's
   pointer binding address → storage key. Otherwise the write is an in-place
   overwrite that lands BEFORE the pipeline's write gate, and the
   compensating delete then removes the victim's content: a caller with read
   visibility but a DENIED write destroys data it was never authorized to
   touch, and orphans the row pointer. Read-visibility is not a write gate.
   With the keys decoupled, compensation can only ever remove what this call
   itself created — structurally, not by ordering luck. `FileUploadResolver`
   already had this protection (random key + pipeline pre-check before
   upload) and the new seam dropped both: **consult in-repo prior art before
   building a second seam over the same resource.**

   (b) **`.Value` is not a count.** `TableMutationPipeline.UpdateAsync`
   returns `keyData.Count == 1 ? keyData.Values.First() : result` — for a
   single-column-PK table the value is THE KEY, not an affected-row count.
   Any code detecting a scoped-away write (out-of-tenant PK matching zero
   rows) must use `MutationIntentResult.AffectedRows`, never `.Value`. Read
   as a count, `.Value` is INERT for every nonzero single-key row (the
   scoped-away write reports success — the exact outcome the guard exists to
   prevent) and MISFIRES on PK value `0`. A guard built on an assumed return
   contract is worse than no guard: it reads as protection in review and
   does nothing at runtime. `AffectedRows` is nullable — a null must never
   read as success, or the fail-open has merely moved. Check the contract at
   the definition; do not infer it from the happy path.

   **Fixture rule (why both hid):** the veto test's row had no pre-existing
   object, and every seam test used `id=1` on a single-key table — so
   neither bug could manifest. Any test covering a key-addressed write path
   must span composite PK, single-column PK, PK value `0`, and
   pre-existing-state-at-the-target. A fixture too simple to let the bug
   manifest is a vacuous test that reads as coverage. (Same shape as the CDC
   slice-4b lesson: a single-source test over a shared multi-source table is
   vacuous — see `docs/solutions/bifrostql/cdc-delivery-guarantees-2026-07-15.md`.)

   **Review technique worth reusing:** to verify a rework's claimed fix,
   REVERT the fix in the working tree and confirm the new tests actually
   fail. Attempt 2's reviewer did this and thereby proved that two CHANGED
   test assertions were genuine corrections of tests that had encoded the
   bug — not tests weakened to fit the fix. A test bent to fit would have
   passed against the reverted code.

   Full write-up: `docs/solutions/bifrostql/s3-slice1-address-vs-storage-key-2026-07-16.md`.

9. **A wire-facing exception catch clause must be complete AND symmetric
   across every op class that shares the same seam's error contract.** The
   S3 epic's own epic-CLOSE gate (not any per-slice review — all 8 slices
   passed individually) found that the read op classes
   (GetObject/HeadObject, CopyObject source-resolve) caught only
   `InvalidOperationException`, while the write op classes
   (PutObject/DeleteObject/CopyObject-destination) correctly caught
   `when (ex is InvalidOperationException or BifrostExecutionError)`. A
   corrupt stored pointer or a table-level policy read-deny threw
   `BifrostExecutionError`, which escaped the narrower read-path catch to
   the generic handler: 500 InternalError instead of the seam's documented
   `BifrostExecutionError` -> `NoSuchKey` (404) contract. A read-denied
   caller got GetObject=500 while ListObjects=404 and every write path=404 —
   an existence/authorization oracle across op classes, defeating the
   epic's own non-enumeration invariant even though nothing leaked. Fixed in
   82376b7 by widening the read-path catches to match the write paths.

   This is the third distinct "wire catch clause exactness" defect on this
   epic's error-mapping seam — invariant 1 (catch must include the derived
   type), invariant 5 (catch must include the full parse-exception family),
   and now catch-SET symmetry across sibling op classes on the same seam.
   Any protocol adapter where multiple op classes (read vs write, list vs
   get, source vs destination) route through one seam with a documented
   exception -> wire-status contract must catch the IDENTICAL exception set
   in every op class; a narrower catch on any one of them creates a
   differential wire signal for the same underlying condition — exactly
   what a non-enumeration/anti-oracle contract exists to prevent.

   **Process corollary:** per-slice review is structurally blind to this
   class of bug — each slice's own diff and fixtures looked correct in
   isolation; only a gate that diffs sibling op classes against each other
   (not against their own acceptance criteria) can catch it. Any multi-slice
   epic implementing one cross-cutting contract across N slices needs an
   epic-close gate that reviews the seam, not the slice.

   Full write-up:
   `docs/solutions/bifrostql/s3-epic-close-crosscutting-error-mapping-2026-07-17.md`.
