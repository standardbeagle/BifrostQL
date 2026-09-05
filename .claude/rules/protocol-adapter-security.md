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

   **And the decoy must match a real user on EVERY observable, not only on
   timing.** Salt, iteration count and challenge shape are on the wire before
   any proof is sent, so they must be STABLE PER USERNAME across repeated
   connections — RFC 5802 §5.1's deterministic mock salt. `NewDecoy()` with a
   fresh random salt per connection is a user-existence oracle in the opposite
   direction: real users are stable, unknown ones are not. Fixed by
   `PgScramVerifier.Decoy(username)`, salt = HMAC(per-process key, username).
   Note the trigger: swapping a credential store's shape (plaintext → SCRAM
   verifier with a FIXED stored salt) changed what a real user emits, so the
   decoy had to change with it — pre-verifier code was safe only by accident.
   Fixture: open TWO connections for the SAME unknown username and assert the
   server-first messages are byte-equal (salt AND iteration count) and that the
   handshake still reaches the proof step; one connection cannot see it.
   <!-- amended_at: 2026-09-05T00:00:00Z  source_event: task:01M1KPC4P7HGBP8JHRCGK7DW5V, git:859eec42 -->

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

   **The tell is a comment claiming the messages are already wire-safe.**
   Two more surfaces failed this way after the pgwire slice — the chat
   middleware and the MCP tool funnel — and in both the forwarding was
   defended by a comment asserting the text was curated, with no throw-site
   check behind it. In both, the leak was the SAME string:
   `TenantFilterTransformer` / `TenantMutationTransformer` embed the
   qualified table and the context-key name ("Tenant context required but
   not found. Expected 'tenant_id' in user context for table
   'main.orders'.") while tagging `AccessDeniedCode`. Treat "it's already
   user-facing" as unverified until you have read the throw sites; a type
   is adapter-owned only if EVERY throw site builds its text from the
   caller's own arguments or from the policy-projected visible schema —
   never from the raw model, the driver, or a transformer.

   **A lookup miss on a caller-supplied NAME is resolved positively, never
   caught.** `DbModel`'s throwing table lookups embed the queried name
   ("failed table lookup on db name: {tableName}") and surface as a raw
   `ArgumentOutOfRangeException` — a BCL type no adapter funnel owns. H6
   wrapped one call site in try/catch and left the other overloads echoing the
   name; M31 swept the closed set instead. The shape that holds: a `TryGet*`
   overload on the model interface (`IDbModel.TryGetTableFromDbName` /
   `TryGetTableByFullGraphQlName`), used at every client-reachable site, with
   the miss mapped onto the adapter's own error carrying no identifier. Keep
   the throwing overload only where the name is MODEL- or CONFIG-derived (the
   caller already knows it exists), and record which construction sites feed it
   in the commit body so the enumeration can be confirmed rather than
   re-derived. Generalizes to any registry/model lookup whose exception text
   embeds the queried key.
   <!-- written_at: 2026-09-05T08:30:00Z  source_event: task:01M1MN9W82B2J1S2J3TQ3FJ0K0, git:306f428c, git:ff74533c -->

   Sanitizing does not mean unactionable. Where the caller is a program (an
   LLM agent, a driver), keep a stable machine-readable CODE and a category
   distinct enough to choose a next action — retry-with-different-input
   versus do-not-retry — and drop every identifier. A caller does not need
   the table name to know it was denied.

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

   **The same decode owes an explicit `NumberStyles`, not just an explicit
   culture.** `decimal.Parse(s, provider)` defaults to `NumberStyles.Number`
   and `double.Parse(s, provider)` to `Float | AllowThousands`, so a
   group-separated token (`"1,5"`) is ACCEPTED as a different number on every
   host, whatever the provider — a malformed wire value silently becomes a
   valid one instead of the clean parse error invariant 5 exists to produce.
   Name the grammar the wire actually has (`NumberStyles.Integer` for the
   integer family, `NumberStyles.Float` for decimal/float); the culture
   argument alone is only half the fix. Three instances, all found by a
   repo grep after the first: `FilePointerAccess.CoerceToken`, pgwire
   `PgExtendedQueryProcessor.DecodeTextParameter` (raw Bind bytes), and the
   inverse shape in `SqlVisitor.VisitIntValueAsync` — bare
   `int.TryParse(text)` takes the CURRENT culture, so a wire `"-5"` fails
   under a non-hyphen `NegativeSign` and boxes as `BigInteger`. Style AND
   culture, both explicit, on every wire-facing `.Parse`/`.TryParse`.
   <!-- written_at: 2026-09-05T18:10:00Z  source_event: task:01M1RX4ER8SQZW37SZABPGJRZR, git:cfe1fec4, git:5f74dcf7 -->
   <!-- amended_at: 2026-09-05T18:30:00Z  source_event: task:01M1S8E86KMBAJ13XHBDVCHGBT, git:f796021e, git:6aac6431 -->

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
   the elements that actually arrived. The declared total stays a BOUND, never
   an allocation — and because it is only a bound, a stream that stops SHORT of
   it must be refused as a protocol error, never assembled and executed as a
   shorter message (the binary WebSocket `ChunkReceiver` throws on
   `ReceivedBytes != DeclaredBytes`). Accepting a short delivery turns a
   truncated or abandoned transfer into a well-formed request the client never
   sent. Any `IProtocolAdapter` with a
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

   Corollary from the MCP write tools (H10/M21): resolving the caller's table
   name is NOT a place to apply read visibility. A caller may be write-permitted
   on a table it cannot read, so the write path resolves the name against the
   FULL schema and lets the pipeline make the authorization decision — a
   read-visibility pre-gate is the second evaluator invariant 4 forbids, just
   inverted. What the visibility projection scopes is only the PROMPT: an
   unknown name answers with the read tools' own did-you-mean list, restricted
   to names this caller may read. So unknown-vs-denied stays indistinguishable
   on the read surface, while the write surface still reaches the one evaluator
   that may say no.

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

   (c) **The WHERE comes from PRE-chain client columns; zero rows on an
   inferred write is a failure.** Build a delete/update predicate from the
   caller's own columns snapshotted BEFORE the transformer chain (plus the
   primary key) — never from the post-transformer data. A transformer that
   stamps a column (`AuditMutationTransformer` writing `updated_at` under
   `populate: updated-on`) turns that stamp into an extra `AND updated_at=@now`
   WHERE term that matches zero rows, and the caller reports success.
   `TableMutationPipeline.SelectPredicateColumns` is that contract; call it,
   do not re-derive a filter (finding H4 —`TreeSyncExecutor` was the third
   delete path and the only one that had drifted). And where the write target
   was INFERRED rather than caller-supplied — a sync, reconcile, or cascade
   deleting a row it just read — an affected-row count of 0 must throw and roll
   back, not pass quietly: the row is known to exist, so 0 means the statement
   silently did nothing. The single-row pipeline's tolerant return-0 exists
   only because the client supplied the predicate; do not copy it into a seam
   that derives its own targets. An empty predicate is likewise a hard error,
   never a widening to an unscoped DELETE.

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

   **(c) The irreversible external effect goes AFTER the pipeline commits,
   and a DEFERRED write is not a failed one.** The file resolvers deleted the
   blob before running the pipeline, so a pipeline veto destroyed the very
   content the veto existed to protect — unrecoverably, because storage has no
   rollback. Order every external side effect after the gate that may refuse
   it: clear the pointer through the pipeline first, remove the object once the
   transaction commits. A failing removal then leaves reclaimable residue, which
   must be surfaced (an exception naming only the storage key the client already
   holds — invariant 3), never swallowed as a denial. The compensating half has
   its own trap: a before-commit hook that DEFERS (the approval gate enqueues a
   pending change whose payload points at the just-uploaded object) is not a
   rejection, so compensation must filter on the deferral's own error code and
   keep the object. Compensating a deferral makes the approved replay point at
   content that no longer exists. Residue from a later-rejected pending change is
   a known, documented sweep — record it in the feature's guide rather than
   inventing a second gate.
   <!-- written_at: 2026-09-03T23:10:00Z  source_event: task:01M1KPA1T5WSGTZB5B07RFTEFS, git:a9870e61 -->

   **(d) A read that GATES a write carries the write's own scope, and the
   write's event fires only on affected rows.** Part (b) says the adapter
   builds no predicate; the converse is that a pre-write read — a state-machine
   current-row load, a before-image, a concurrency current row — must be
   narrowed by the SAME `AdditionalFilter` the update will carry, not by the
   primary key alone. A PK-only load lets a transformer gate on a row the caller
   cannot write, and the gate's answer is observable: a permitted transition
   returned a silent zero-row success while a forbidden one returned "State
   transition is not permitted." — an oracle reading another tenant's stored
   state. Take the scope from the transformer chain itself (run it as a
   FILTER-ONLY probe with a PK-only payload, keep the filter, discard data and
   errors), never from a second hand-rolled tenant/policy predicate — invariant
   4's rule applies to the write path too. Exclude the optimistic-concurrency
   token from the probe deliberately: it is the one filter contributor that
   depends on the payload, and including it makes a stale token read as "no such
   row", surfacing a lost update as an illegal transition instead of the
   CONFLICT the write itself raises. Not every pre-write read needs this — a
   before-image whose result is DISCARDED on a zero-row write may stay PK-only;
   the test is whether the read's result can change the RESPONSE. Paired half:
   every observer/event emission on a write (transition notifications, webhooks,
   CDC, workflow triggers) is gated on `AffectedRows > 0`, as the in-transaction
   history/outbox hooks already were — a scoped-away write affects no rows, so an
   ungated event ships another tenant's row state to the observer chain. Gate on
   `AffectedRows`, never on the pipeline's scalar return `Value` (invariant 8(b)).
   <!-- written_at: 2026-09-04T02:15:00Z  source_event: task:01M1KPA1W14FJDK7J3JH43DSXT, git:e695d7f0, git:e0b11bc7 -->

   **(e) A storage provider's write path returns a LOCATOR, never a capability.
   Capability URLs are minted on read, with a server-clamped TTL.** M25:
   `S3StorageProvider.UploadAsync` returned a 15-minute presigned GET and
   `FileStorageService` stored it in the column's pointer JSON as
   `FileMetadata.AccessUrl` — a bearer credential written into the database, and
   therefore copied into every history row, CDC message and audit export, where it
   outlives its own expiry and every access check that would have refused to mint
   it again. The rule generalizes past S3 to any signed URL, SAS token, or
   pre-authorized handle: the write path may persist only the opaque key that
   addresses the object; the capability is derived per read, under the reader's own
   identity. Two follow-throughs the review had to force:
   (i) **remove the property, do not merely stop writing it** — a nulled slot left
   "for legacy deserialization" is still on the wire (a permanently-null
   `_fileUpload.accessUrl`) and is exactly where the next write path puts a
   capability back; unknown-member tolerance already reads old rows.
   (ii) **a caller-supplied TTL is a request to NARROW, never to widen** — clamp it
   against a configured server maximum (`maxurlexpiry`, default 60 min), reject
   non-positive, and report the clamped value. Unclamped it reaches the provider's
   own ceiling (SigV4's 7 days) or throws an unmapped
   `ArgumentOutOfRangeException` past the funnel (invariant 5).
   <!-- written_at: 2026-09-04T23:40:00Z  source_event: task:01M1KPC4HZPRSM4ED1WANEY340, git:eee41212, git:1a0f045e -->

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

   **The symmetry obligation covers branches that never reach the catch at
   all.** A front door's PRE-PIPELINE shape checks — table/column lookup,
   key parse, arity — answer before any executor call, so no funnel and no
   catch clause sees them; they are nonetheless the same wire, and every one
   of them must produce the wire shape the PIPELINE's denial produces, with
   no identifiers. `BifrostBlobMiddleware` (M12) resolved table and column
   from the RAW model and answered 400 naming the PK column and its data
   type for a missing/malformed `k.<pk>`, while policy/tenant denial after
   `executor.ExecuteAsync` answered 404 — a read-denied caller enumerated
   tables, blob columns and key names on the 400-vs-404 difference, and the
   comment above the branch claimed the opposite. Two rules: (a) resolve the
   model through the caller-scoped projection (`SchemaReadVisibility`, which
   calls the same evaluator as invariant 4) so a denied table or column is
   null exactly as an unknown one; (b) route unknown, denied, non-blob,
   missing, duplicated and malformed key through ONE constant response
   writer, and assert BODY equality in the test, not just status — a
   status-only assertion passes a 404 that still names the column. The
   `denied-table-WITHOUT-key` row is the fixture that proves the parse never
   runs ahead of the visibility check; without it the test is vacuous.
   Timing is not a second oracle here only because every shape check is
   in-memory — a "defer the checks until after the pipeline read" fix would
   have equalized the bodies while leaving unknown (no DB round trip)
   distinguishable from denied (one round trip). Prefer the projection route.

   **The symmetry obligation also runs WITHIN a single op, across the ways a
   caller can NAME the same object.** M12 was two branches of one middleware;
   LDAP M20 was two spellings of one search: a denied attribute reached by the
   `*` wildcard was omitted from the entry, while the SAME attribute named
   explicitly in the selection list fetched it and surfaced the pipeline's
   `insufficientAccessRights`. The caller's own subschema hides that attribute
   from this identity, so on this wire it does not exist — and success-with-
   omission versus an explicit denial is exactly the hidden-vs-nonexistent
   oracle. Wherever a request can address a field, column, attribute or key by
   name AND by a wildcard/projection/`SELECT *`, both routes must resolve
   through the SAME caller-scoped projection (`SchemaReadVisibility`) and
   produce the same wire shape; a denied name answers as an unknown name does.
   The exception is a name the caller can also FILTER on: filter-referenced
   columns must still be refused rather than silently dropped, because
   silently ignoring a predicate would return rows the caller's filter excluded
   — a correctness fail-open that outranks the oracle. The fixture must pair a
   denied name with an unknown one in the SAME op and assert the responses are
   byte-identical; a test that only exercises the wildcard route passes over
   the explicit one.

   Full write-up:
   `docs/solutions/bifrostql/s3-epic-close-crosscutting-error-mapping-2026-07-17.md`.
   <!-- amended_at: 2026-09-04T23:10:00Z  source_event: task:01M1KPC4MXF2621FXFZZCVF7ZM, git:e0c546b3,019822ef,dfe1d5e2 -->

<!-- invariant 9 amended_at: 2026-09-04T20:00:00Z  source_event: task:01M1KPC4FPVK9M52V2N9NBRCWK, git:9ee6e995,e7e90b5a -->

10. **Route every op class through ONE error-mapping funnel — AND ensure
    upstream throw sites tag the same condition with the same signal.** This
    is the prevention counterpart to invariant 9's detection lesson. The S3
    epic drifted because it had N per-op-class catches kept in sync by review;
    the fix generalized (OData v4 epic) to a single top-level try/catch at the
    request-dispatch boundary so cross-op divergence is impossible *by
    construction* — there is no second catch to drift
    (`ODataMiddleware.InvokeAsync` wraps service-doc/`$metadata`/entity-read/
    `$filter`/`$skiptoken`/`$expand` in one funnel; the S3 differential-oracle
    class is structurally unreachable). BUT a single funnel is NECESSARY, NOT
    SUFFICIENT: the funnel maps by a CONDITION SIGNAL (an exception type or
    code), so parity ALSO requires every op class's upstream throw sites to
    tag the same underlying condition with the same signal. The gRPC epic
    proved this the hard way — reads and writes both routed through one
    `GrpcStatusMapper` funnel, yet a missing-tenant denial mapped to
    `PERMISSION_DENIED` on read but generic `INTERNAL` on write, because the
    read-side `TenantFilterTransformer` tagged its throw with
    `BifrostExecutionError.AccessDeniedCode` while the write-side
    `TenantMutationTransformer` threw a CODELESS `BifrostExecutionError` that
    fell through to the funnel's default. Same funnel, divergent statuses.
    The fix was to tag the write throw with the same code (map by CONDITION,
    never by op class — an "if write -> PERMISSION_DENIED" special-case would
    mask a genuine INTERNAL). Checklist for any adapter mapping internal
    errors onto a client wire: (a) one funnel, not per-op-class catches;
    (b) audit that shared Core/transformer throw sites tag a given condition
    (policy-deny, tenant-deny, not-found) with a consistent, funnel-matchable
    signal across the read AND write paths; (c) a conformance/parity fact that
    asserts the SAME condition -> the SAME wire status across op classes — the
    gRPC divergence was invisible to every per-slice review and surfaced only
    when the conformance kit ran read AND write facts side by side. Full
    write-ups:
    `docs/solutions/bifrostql/odata-epic-close-single-funnel-error-mapping-2026-07-18.md`,
    `docs/solutions/bifrostql/grpc-epic-close-single-funnel-needs-condition-tagging-2026-07-18.md`.

11. **An identity-less front door (a caller with NO per-request identity)
    that serves data aggregated over tenant/row-scoped data must make
    cross-tenant exposure an EXPLICIT DEPLOYMENT DECISION — never an ambient
    default.** Every other adapter carries a per-request caller identity
    (bearer/Basic/session) projected through `IBifrostAuthContextFactory`, so
    the pipeline narrows scope from it automatically. A PULL/PUSH surface with
    no such identity — a Prometheus scrape, and by generalization any webhook
    emitter, scheduled export, OTLP/OpenMetrics endpoint, or data-warehouse
    sync — has no ambient identity to narrow from, so an aggregate would
    silently span all tenants unless the operator explicitly chose the scope.
    The required shape (Prometheus exporter epic): the surface is opt-in and
    default OFF; enabling it requires BOTH a configured scrape credential AND
    an explicit MODE per emitted aggregate — either (a) a configured FIXED
    SERVICE identity projected through the SAME `IBifrostAuthContextFactory`
    (the aggregate runs through `IQueryIntentExecutor` under that identity, so
    tenant/soft-delete/policy apply), or (b) declared TENANT-LABEL
    PARTITIONING (group-by the table's declared tenant column, exposed as a
    label). Any misconfiguration — credential without mode, mode without
    credential, service-identity mode with no configured identity,
    tenant-label mode on a non-partitionable table — FAILS CLOSED to NO output
    for that series, NEVER to an unscoped/global run. Two corollaries that
    bite on these surfaces specifically: (i) **a cached/coalesced aggregate's
    cache key MUST include the security mode + identity partition** — a key
    that omits it serves partition A's cached series to partition B (a
    cross-tenant CACHE leak even when the query path is correct); (ii) **any
    self/operational metric labels must be structurally bounded** — enforce
    finite label domains with an enum-only record API so a tenant id / user id
    / table name / raw SQL / exception string is UNREPRESENTABLE as a label at
    compile time (unbounded labels are both a cardinality-DoS and an
    info-disclosure channel; convention is not enough). Full write-up:
    `docs/solutions/bifrostql/prometheus-epic-identity-less-scrape-exposure-2026-07-18.md`.

12. **A security mode's NAME is a load-bearing claim, and an EMPTY user context
    is not a refusal.** The MCP adapter's `McpAuthMode.FailClosed` projected an
    empty user context for a caller who presented no identity, and served the
    request. An empty context only gates tables that DECLARE tenant metadata, so
    every other table stayed readable by an unidentified caller — fail-closed in
    the name, the enum's XML doc, and the guide; fail-open in fact.
    `FailClosed`, `Bearer` and `AnonymousDev` all behaved identically for an
    unidentified caller. Two checks:
    (a) any enum member, option, or mode whose NAME asserts a security property
    needs a test that goes RED when the property is absent — and the fixture
    must use a table WITHOUT tenant metadata, or an empty-context bug cannot
    manifest (a tenant-scoped fixture makes the test vacuous per
    `regression-test-non-vacuous.md`);
    (b) the refusal belongs on EVERY transport seam the mode covers — the stdio
    per-call provider and the HTTP per-request guard both, or one name means two
    things. Reuse the adapter's already-funnelled exception type (invariants 1
    and 3) so the refusal reaches the wire sanitized with no new mapping.

    **A refactor from THROW to a result type must map the failure arm to a
    REFUSAL, never to the type's empty/default value.** M13 funnelled every HTTP
    mount's identity projection through `BifrostIdentityGate.Project`, which
    returns a `BifrostIdentityOutcome`; the wire-less helper
    `HttpContextWorkflowExtensions.GetBifrostUserContext` mapped `Unprojectable`
    to an EMPTY context. Pre-refactor it THREW, so an authenticated-but-
    unidentifiable principal (unmapped OIDC issuer, subject-less principal) got
    an accidental 500; after the refactor the workflow executor served it as
    anonymous for reads AND writes — a fail-open introduced BY the fix, on the
    one seam that owns no response. The empty value of an identity type is
    always the fail-open value, so the refactor's default arm is exactly where
    it lands. Rules: a projection seam with NO wire of its own throws a typed,
    constant-message exception (`BifrostIdentityRejectedException` — invariant 3:
    no issuer or claim detail) and its caller maps that to the status every other
    seam answers for the same condition (invariant 9); only a seam that owns a
    response may map the outcome itself. Every consumer of the outcome branches
    on the failure arm EXPLICITLY — a consumer that ignores it and reads the
    context gets empty, which the executor serves. The pinning fact asserts the
    CALLER's behaviour (the sidecar answers 403), not the projector's return
    value; a fact over the projector alone is green either way.
    <!-- written_at: 2026-09-05T22:00:00Z  source_event: task:01M1KPC4GQQ1SYYTZDYQTVTJ9D, git:06ab8a6c, git:41140a3b -->

    **Flipping a shipped default is a docs sweep, not a docs sentence.** Every
    code SNIPPET that shows the old default is load-bearing, not just prose
    asserting the control: after this flip a bare
    `AddProtocolAdapter<BifrostMcpAdapter>()` in the guide and in an article
    refused every tool call, and a reader following it would have shipped a
    server that serves nothing. Review caught the same shape twice on this drain
    (the HTTP-mount-order flip, then this one). Grep every `docs/` snippet that
    constructs the affected surface and make it declare the mode explicitly.

    Host corollary: a fact about a SHIPPED security default must pin
    `UseEnvironment("Production")` — `WebApplicationFactory` defaults to
    Development, where `DisableAuth` legitimately lives, so the test proves
    nothing. Auth-on in a Host test also constructs the interactive OIDC
    handler, which needs `JwtSettings:Authority` + `ClientId`. And
    `Program.cs` top-level statements read `builder.Configuration` BEFORE the
    host builds, so `ConfigureAppConfiguration` is invisible to those reads —
    set feature switches with `UseSetting`.

13. **Memoize a fail-closed identity seam per REQUEST, never per SESSION.** One
    MCP tool call asked the user-context provider twice (role gate, then the
    tool), and on stdio each ask re-ran the configured credential exchange —
    real IdP I/O, bridged sync-over-async. The fix is a per-call memo at the
    funnel boundary; a failed resolution is never memoized. The per-session
    cache the finding originally proposed would keep serving a revoked or
    expired credential until the peer disconnects — it converts a latency win
    into the exact revocation fail-open that per-request revalidation exists to
    prevent. Any adapter whose single request resolves identity at several seams
    (gate, tool, projection) gets the per-call memo; the pinning fact asserts
    invocations == 1 within one call AND == 2 across two calls, so a
    session-scoped cache goes RED.

14. **A privileged pipeline behaviour is opened by an unforgeable reference
    token, never by a public bool or enum an external caller can set.** Restore
    (M2) shipped as `MutationIntent.RestoreSoftDeleted`, a public bool that
    lifted the soft-delete guard in `MutationTransformerBase` and took the
    captured-image re-insert past `TenantMutationTransformer`'s insert pinning —
    so every `IMutationIntentExecutor` caller (MCP write tools, protocol
    adapters, host code) could un-delete rows or re-create hard-deleted ones by
    setting a flag. The shape that holds is a sealed type with no public
    constructor, factory, or settable static (`MutationRestoreCapability`,
    `HistoryErasure.Marker`, `ApprovalInterceptMutationHook.MarkApprovedReplay`
    — three instances, so treat it as the default for any new
    engine-only privilege): the caller can name the property but can never
    produce a value, making forgery a compile-time impossibility rather than a
    runtime check. Gate on the token BEFORE argument shaping and before any
    transformer, so a token-less call builds nothing and cannot be probed; a
    token attached to the wrong action is refused, not ignored.

    **The mint boundary is the `InternalsVisibleTo` list, and the type's doc
    comment must name it.** "Can only be minted inside BifrostQL.Core" was
    false — Core grants internals to Server, the dialect packages, Benchmarks
    and Core.Test, all of which can mint one. An XML doc comment on a security
    type is a load-bearing claim in exactly the way a `docs/` sentence is (see
    `steering-docs-follow-mechanism-changes.md`): coverage words are the tell,
    and the repair is to state the real boundary and the guarantee that still
    holds (here: BifrostQL.Mcp and out-of-tree callers cannot mint; reflection
    is not a wire and is out of the threat model), never to delete the sentence.

    **Fourth instance: `WorkflowTriggerSuppression`** (`17b06d8e`, task
    `01M1KPA21MXSV6WS059S21BB81`). The workflow-trigger suppression flag was
    `UserContext["_bifrostSuppressWorkflowTriggers"] is bool` — a typed claim from
    any identity provider, or a wire-context entry, silenced every post-commit
    observer for that request. Now `WorkflowTriggerHost` stamps the sealed marker
    and `MutationNotifier.IsWorkflowTriggerSuppressed` checks reference equality
    against `WorkflowTriggerSuppression.Instance`. The strip in
    `IdentityContextMapper`/`WireContextMerger` is defense-in-depth only: the
    Server's legacy per-claim-type projection (`BifrostContext`) still copies an
    unknown claim type under the key as a `string[]`, and the reference gate is
    what refuses it. When both halves exist, the gate is the load-bearing one;
    a test that only proves the strip has not proved the privilege is closed.

    **`_hardDelete` (M4) resolved differently, and the difference is the rule.**
    A token is for a privilege no external caller may ever hold. `_hardDelete`
    is a privilege the OPERATOR grants per table, so it shipped as a
    declaration gate on one predicate — the table carrying
    `soft-delete-hard-role` — enforced twice: the SDL omits the argument
    (`MutationArgumentsSdl`, both the mutation field and `_batch`), and
    `GetHardDeleteDenial` refuses with `AccessDeniedCode` on the same
    predicate, which closes the `IMutationIntentExecutor` route that has no
    SDL to omit. **Both halves are required**: an SDL-only gate leaves the
    programmatic route open, and a backstop-only gate advertises a privilege
    that is always denied. When a privileged flag is operator-declarable,
    gate it on the declaration in both places; reach for a token only when
    nothing outside the mint boundary may hold the privilege at all.

    Flipping such a default is breaking, and its blast radius reaches engines
    that request the privilege on the operator's behalf: M4 made the retention
    purge of a `retain` table without the role start failing, which falsified
    a `retention.md` sentence no test covered. Grep the docs for the old
    default before the docs commit (`steering-docs-follow-mechanism-changes.md`).

15. **A pre-auth deadline may only be RETIRED by an action that costs the peer
    credentials — never RE-ARMED by a free one.** AGENTS.md already requires
    every adapter to arm a deadline at accept. The LDAP M17 fix armed one and
    then re-armed it on every successful bind, including an ANONYMOUS bind —
    and anonymous binds carry no secret, so they are (correctly) exempt from
    `MaxBindAttempts*`. A credential-less peer re-binding anonymously inside
    each window held its admission slot indefinitely: the same slot-exhaustion
    vector the deadline exists to close, one message type over, and a deadline
    that any free action renews is equivalent to no deadline at all.

    The rule generalizes past LDAP bind: any unauthenticated message an adapter
    accepts before the credentialed handshake completes — an anonymous or
    unauthenticated bind, a ping/NOOP/keepalive, a StartTLS or renegotiation, a
    protocol-version negotiation, a cancel request — is FREE, because nothing
    rate-limits it and nothing proves who sent it. None of them may move the
    deadline. Concretely (`LdapConnectionHandler`): fix the deadline at accept;
    only a credentialed bind clears it; every other outcome leaves an armed
    deadline untouched (`??=`), and a fresh bounded window is armed only on the
    transition BACK to anonymous (a failed re-bind of a previously credentialed
    session — invariant 12's reset), so that state is bounded too rather than
    unbounded-because-previously-authenticated.

    Third occurrence on this contract: RESP H11 (no-auth mode killed the 30 s
    deadline outright), pgwire M15, LDAP M17 — so treat "what retires this
    deadline, and can the peer cause that for free?" as a review question on
    every adapter handshake, not a re-check of LDAP. The pinning fact is a
    LOOP: N free actions spaced inside the window, asserting the connection is
    closed DURING the loop. A single free action cannot distinguish a renewed
    deadline from a still-armed one, so a one-shot fixture is vacuous
    (`regression-test-non-vacuous.md`). The loop must advance an INJECTED
    clock, not sleep real time — a wall-clock margin under a few hundred ms
    flakes under the parallel epic gate (LDAP M17's fact, twice), and every
    adapter handler already takes a `Func<DateTimeOffset>` clock (RESP, now
    LDAP), so a new deadline fact has no excuse to sleep.
    <!-- amended_at: 2026-09-05T00:00:00Z  source_event: task:01M1QX52E7ZCY5QRZ8JMZ2BJHY, git:baba20a5 -->


<!-- invariant 14 written_at: 2026-09-04T03:00:00Z  source_event: task:01M1KPA1WXEYM3W99A5V1RRV77, git:f91dfeee,7a00fc2a -->
<!-- invariant 14 amended_at: 2026-09-04T19:30:00Z  source_event: task:01M1KPA1ZWG8TQGCXCXWDNN7RB, git:9b43f138,13c8fa8c -->
<!-- invariant 15 written_at: 2026-09-04T23:10:00Z  source_event: task:01M1KPC4MXF2621FXFZZCVF7ZM, git:a2a97269,9402082a,adf6fc5c -->

