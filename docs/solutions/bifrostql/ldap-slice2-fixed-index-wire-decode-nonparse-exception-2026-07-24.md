---
written_at: 2026-07-24T03:05:00Z
source_event: task:01KXM41EP1B0RDCXEYWWJBJXQY
module: bifrostql
category: security
confidence: high
form: constraint
sources:
  - task:01KXM41EP1B0RDCXEYWWJBJXQY#comment-01KY8Z85A8RXHCEYV43B656ZAW   # review_annotation_v1 attempt-1 FAIL: failPattern + systemicObservation (Content[0] on empty Boolean escapes parse-family catch)
  - task:01KXM41EP1B0RDCXEYWWJBJXQY#comment-01KY900EEQ28TWSCBPJ2AXKFGH   # review_annotation_v1 attempt-2 PASS: passPatterns (fix at accessor + non-vacuous revert-proof)
  - git:1c0ddb4   # RED pin: zero-length criticality Boolean -> IndexOutOfRangeException at LdapMessageReader.cs:219 against pre-fix code
  - git:c11694a   # GREEN fix: length-checked BerCursor.Boolean accessor mirrors Integer's guard; DecodeControls routed through it
related:
  - .claude/rules/protocol-adapter-security.md          # invariant 5 (catch the full parse-exception family) — this is its wire-decode SIBLING (a non-parse throw the family-catch cannot cover)
  - .claude/rules/regression-test-non-vacuous.md        # dedicated zero-length fixture distinct from the 1-byte Control helper; revert-proven RED
  - docs/solutions/bifrostql/ldap-slice1-second-egress-path-credential-column-2026-07-24.md   # consecutive LDAP slice, same "reviewer sweep found the unguarded sibling path" shape
  - docs/solutions/bifrostql/resp-slice1-nesting-depth-2026-07-14.md                          # the check-before-descend depth cap this slice reused 1:1; adjacent unauth-wire-decode hardening
  - docs/solutions/bifrostql/pgwire-extended-query-bind-decode-2026-07-13.md                  # invariant 5's origin (OverflowException outside FormatException catch)
tags: [ldap, protocol-adapter, security, ber-codec, wire-decode, invariant-5, fixed-index-read, non-parse-exception, validate-at-decode, accessor-guard, revert-proof, non-vacuous, module-slice, rewind]
status: steering
recurrence: 1
---

# LDAP slice 2: a fixed-index read of decoded wire content throws a NON-parse exception the family-catch cannot cover (1 rewind)

**Task**: LDAP slice 2 — BER/LDAPv3 codec + bounded Kestrel connection loop
(01KXM41EP1B0RDCXEYWWJBJXQY). Correctness review FAIL attempt 1 (1 major
blocker) → rework → PASS attempt 2. Codec + connection lifecycle only; no bind
auth / no search execution (non-goals).

## Lessons

1. **`protocol-adapter-security` invariant 5 ("catch the full parse-exception
   family") has a SIBLING that widening the catch cannot fix: a fixed-index /
   `[N]` / first-byte read of decoded primitive content throws
   `IndexOutOfRangeException`, which is NOT a parse exception.** `DecodeControls`
   read `Content(ReadElement(Boolean))[0]` on the control-criticality field
   without a length check. A legal-BER zero-length Boolean (tag `0x01`, length
   `0`) — sendable by an unauthenticated peer on the FIRST message before any
   dispatch — makes `Content(...)` return an empty array and `[0]` throw
   `IndexOutOfRangeException`. That type is outside the connection loop's decode
   catch filter (`LdapProtocolException | FormatException | OverflowException |
   ArgumentException`) AND outside the outer catch (`IOException |
   OperationCanceledException`), so it escaped unhandled to Kestrel with no
   Notice of Disconnection — an error-level, attacker-triggerable fail-open
   connection teardown. Invariant 5's own remedy (widen the parse family) does
   NOT catch this: `IndexOutOfRange`/`IndexOutOfBounds` from indexing a
   short/empty primitive is a different exception class. The 1442-test Server
   suite was green over it because the only controls test used
   `criticality:true` (a non-empty Boolean) — structurally incapable of
   manifesting the zero-length crash.

2. **The durable fix is validate-at-decode ON THE SHARED ACCESSOR, not
   catch-widening and not a call-site guard.** The fix added a length-checked
   `BerCursor.Boolean` accessor that raises `LdapProtocolException` (inside the
   loop's caught base) when `ContentLength == 0`, mirroring the existing
   `BerCursor.Integer` guard (`==0` / `>8`), and routed `DecodeControls` through
   it. Centralizing on the reusable accessor makes every future Boolean decode
   (typesOnly, other controls) safe by construction; an inline `[0]` guard at
   one call site would leave the next primitive-decode call as the next escape.
   This is exactly how `Integer`/`String` already contained their own boundary
   cases — the codec had the pattern; one accessor (`Boolean`) was missing it.

3. **After fixing the named site, GREP for the concrete second-egress vector
   and RECORD the per-accessor conclusion.** The reviewer named one site; the
   rework swept every fixed-index / first-byte read of untrusted wire content in
   `BerReader.cs` + `LdapMessageReader.cs` and recorded why each is safe (the
   recorded sweep is the cheap-to-re-verify deliverable):
   - `DecodeControls` criticality `Content(...)[0]` — WAS unguarded → now via
     length-checked `BerCursor.Boolean` (the blocker).
   - `Integer` — already guards `ContentLength == 0` and `> 8` before its
     bounds-checked loop; `Int32` delegates to it.
   - `String` — `Encoding.UTF8.GetString(buffer, ContentStart, ContentLength)`;
     0-length → empty string, no index.
   - `Content(...)` — `Array.Copy` of `ContentLength` bytes; 0-length → empty
     array (the crash was the CALLER's `[0]`, not `Content`). The two other
     `Content(...)` callers (filter comparison value, control value) do not
     index the result.
   - `ReadElement`/`ReadLength`/`PeekTag` — bounds-check `_position < _end`;
     `ReadByteAsync` indexes `one[0]` only after `read == 1`.
   Line 219 was the ONLY unchecked fixed-index wire read. The reviewer
   independently repeated the grep and confirmed the sweep claim.

4. **The pinning fixture must be the bug's minimal reproduction, not the tuned
   default.** A dedicated `ControlWithEmptyCriticalityBoolean` fixture
   (`BerWriter.Tlv(Boolean, empty)`) — distinct from the 1-byte `Control` helper
   — makes the fixed and pre-fix code produce provably different output
   (`LdapProtocolException` vs `IndexOutOfRangeException`). RED-commit-first
   (`1c0ddb4` pins the leak test against pre-fix code; `c11694a` adds the
   accessor + fix). The reviewer restored the pre-fix `Content(...)[0]`,
   force-rebuilt Server (`--no-incremental`), and reproduced the exact
   `IndexOutOfRangeException` at line 219 — proving the test genuinely guards,
   per `.claude/rules/regression-test-non-vacuous.md`.

5. **(Advisory / fact) A zero-length guard is a LOWER bound only; strict
   validation needs both bounds.** `BerCursor.Boolean` now guards
   `ContentLength == 0` but not `!= 1`, so a multi-byte Boolean is leniently
   accepted (first byte taken as the value). Non-blocking — `ReadElement`
   consumes the whole element (no cursor desync) and the index is in-bounds for
   any `len >= 1`, so no crash/escape. But it is a strict-LDAPv3 leniency;
   mirroring `Integer`'s upper-bound guard (throw when `ContentLength != 1`)
   closes it. Any primitive accessor should validate BOTH length bounds, not
   just the crashing one.

## Why it recurs

- Invariant 5 is framed around `.Parse`-family throws, so hardening focuses on
  numeric/GUID/date decode and misses direct indexing of decoded bytes. The
  connection-loop family-catch reads as complete coverage but only covers the
  exception types someone remembered to enumerate — a fixed-index read
  introduces a class outside that set.
- Shared fixtures are tuned for the common case (a non-empty Boolean), so the
  empty/short-primitive failure mode is invisible to a green suite.
- This is the SECOND consecutive LDAP slice where the reviewer's adversarial
  sweep found a pre-auth blocker the implementer + green suite missed, both of
  the same shape — a guard/catch covering the obvious path while a sibling path
  is unguarded (slice 1 = DN-naming-column credential egress, config side;
  slice 2 = fixed-index BER accessor, wire-decode side). See the slice-1 doc.

## Apply when

- Any protocol adapter with typed-value decoding on an unauthenticated wire.
  After confirming the parse-exception family-catch, AUDIT every site that
  INDEXES decoded content (`content[N]`, `buffer[start + i]`, `.First()`,
  `[0]`), not just `.Parse`-family sites — each is a non-parse-exception escape
  the family-catch cannot cover.
- Fixing one such site: put the guard on the shared cursor/accessor (raising the
  adapter's caught protocol exception), never inline at one call site; then grep
  the codec for the whole `[N]`/first-byte-read family and record each accessor's
  conclusion.
- Validate BOTH length bounds of each primitive (empty AND over-wide), mirroring
  the sibling accessors that already do.

## Prevention (checklist to add to future adapter slices)

- [ ] For every BER/wire primitive accessor, guard zero/short AND over-wide
      content length at the accessor and raise the loop's caught protocol
      exception — never index decoded content without a length check.
- [ ] After fixing one decode-crash site, grep the codec for `[0]` / `[N]` /
      `buffer[start` / `.First()` on decoded content; record the per-accessor
      safe/unsafe conclusion in the completion (like this slice's
      `accessorSweep`).
- [ ] Pin with a dedicated minimal-repro fixture (the empty primitive), distinct
      from the tuned default; RED-commit-first and narrate the revert-proof.
- [ ] Add a codec-level boundary/fuzz test feeding every BER primitive at 0-byte,
      multi-byte, over-wide, and truncated lengths, asserting a clean
      `LdapProtocolException` for each — so no single decode site can regress the
      invariant-5 family and the Boolean upper-bound leniency closes with
      coverage.

## Promotion candidates (flagged for operator decision — steering only for now)

1. **Fold a wire-decode sub-clause into `protocol-adapter-security` invariant
   5**: "catch the full parse family" is necessary but not sufficient — the
   durable rule is *validate-at-decode on the shared accessor*, and the audit
   must cover every fixed-index / `[N]` / first-byte read of decoded content,
   not just `.Parse`-family sites (those throw non-parse exceptions the
   family-catch cannot cover). This is the Nth invariant-5 manifestation in-repo
   (pgwire slice-5 overflow, and now this non-parse egress) plus the consecutive
   LDAP sibling-sweep shape — the recurrence gate for a rule-tier promotion is
   effectively clear.

2. **Module-slice workflow-template test-target gap (3rd+ occurrence).** The
   module-slice template builds/tests `tests/BifrostQL.Core.Test`, which gives
   ZERO coverage of `src/BifrostQL.Server/Ldap` adapter code; its build/test
   steps passed trivially at 5321 Core tests while the actual slice code was
   only exercised by an explicit `Server.Test` run (self-run by implementer AND
   reviewer, 1443/0) — and the revert experiment required a forced Server
   rebuild the template never performs. Already recorded twice on the pgwire
   epic playbook (INVERSE direction there: template targeted Server.Test, Core
   needed running) — same root: a template with a FIXED test target does not
   follow the adapter's actual code location. Operator action: per-epic
   workflow template for Server-hosted adapter slices (Ldap/Resp/Pgwire/gRPC/
   OData/Prometheus) must build+test `tests/BifrostQL.Server.Test`, else review
   must self-run it before any pass verdict.
