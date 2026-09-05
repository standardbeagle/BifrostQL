# Every regression test added with a fix must be revert-proven RED

Applies to any commit that closes a bug by adding or changing a test alongside a
code fix — every project surface, not just protocol adapters. Generalizes the
revert-the-fix technique from `protocol-adapter-security.md` invariant 8, which
recurred across three unrelated slices: S3 slice-1 key-addressed writes, CDC
slice-4b single-source fixtures, and RSS feeds slice-2 template re-expansion. In
every case a fully green suite passed over a regression test whose fixture was
structurally incapable of manifesting the bug it claimed to guard.

## The rule

A regression test is **vacuous** if it passes against the pre-fix (buggy)
implementation. A vacuous test reads as coverage in review and guards nothing —
a future regression to the old code stays green.

- **Implementer:** before submitting a "fix + pinning test" commit, temporarily
  restore the pre-fix implementation in the working tree and run the new test.
  It MUST fail with the exact bug signature. Then restore the fix. Report the
  revert-proof (the divergent output strings / the RED assertion) in the commit
  message so review is a fast confirmation, not a re-derivation.
- **Reviewer:** for any rework whose blocker was "add a regression test", repeat
  the revert experiment. Do not trust a green suite or a plausible-looking
  assertion. If a CHANGED assertion is claimed as a correction, revert-prove it
  is a genuine correction and not a test weakened to fit the fix.

## Why fixtures go vacuous

Shared fixtures are tuned for the common case — a single placeholder, a
single-column PK, `id=1`, a single data source, a single relationship hop, a
single-verb batch, no pre-existing state at the target. A bug that only
manifests with multiple elements (>=2 placeholders, composite PK, PK value `0`,
multi-source table, >=2 traversal hops, >=2 heterogeneous actions in one
batch/tree, pre-existing target content) cannot be exercised by such a
fixture. A test targeting a multi-element failure
mode needs a **dedicated fixture variant** that makes the fixed and pre-fix
implementations produce provably different output.

- Construct the test fixture from the bug's *minimal reproduction*, not the
  default fixture.
- **Pick the fixture's ENTRY POINT by reachability, not by familiarity — a
  schema-validated front door masks pipeline bugs.** The GraphQL front door
  types its update/delete inputs with the key columns required, so it cannot
  submit the malformed shape at all: the M3 partial-composite-key task's first
  RED went six-facts GREEN against provably buggy code, because the front door
  rejected the input before the pipeline ever saw it. The reachable surface for
  a pipeline/seam defect is the seam a caller can actually reach with a free
  dictionary — here `IMutationIntentExecutor` (the protocol-adapter write seam,
  whose `Data` is built from the wire). Before writing the test, ask which
  caller can actually construct the bad input; drive the intent seam, not the
  schema-validated front.
- Parameterize the shared builder with a default preserving prior behavior
  rather than mutating the shared fixture (keeps blast radius to one test).
- **Where the affected population is a CLOSED ENUMERATION, span every kind, not
  a representative.** ">=2 elements" is about count; this is about coverage of a
  known-finite set. The H6 mixed-root-field test enumerated all six non-table
  root-field kinds (`__typename`, `<t>Aggregate`, `<t>Pivot`, `<t>History`,
  `_rawQuery`, `_dbSchema`) as separate facts, which is what proved the
  `__typename` case real rather than a no-op the visitor never collects. One
  representative kind leaves the rest free to regress independently.
- **A one-element fixture cannot observe WHICH element a walker read.** Where
  state is attached to one node of a chain and read back by another walker
  (relationship-filter scope on the node naming the link — finding C1), a
  single-hop fixture has one candidate node, so an off-by-one read is
  indistinguishable from a correct one and the test passes either way. Span
  >=2 hops and scope only the LAST one, so the wrong node is provably null.
- **A single-verb batch cannot observe state leaking between actions.** Where
  per-action state lives in a bag a multi-action path reuses (H3: the approval
  hook's logical verb surviving from a soft-delete action into the next update
  of the same batch), a same-verb fixture makes the leaked value equal the
  correct one. Use >=2 actions with DIFFERENT verbs in one batch/tree and
  assert the downstream destructive consequence (approving the pending row
  renames it, does not delete it), not just the recorded op.
- **A name-space boundary needs a fixture where the two names DIFFER, plus a
  negative assertion on the old name.** Shared fixtures name a column the same
  on both sides (`GraphQlName == ColumnName`), so a test over them passes
  whether or not the rekey happened. Give every column under test a DB name
  that sanitizes to a different GraphQL name (a space or dash: `sale-price` ->
  `sale_price`), and assert the output does NOT contain the old key — a
  positive-only assertion misses a double-bind where both names reach SQL.
- **A re-routing / state-transition test must assert the OLD target was not
  hit.** Where a change moves work from one destination to another (H13: an
  editor remount that had to issue its once-per-mount schema query over the
  newly selected profile's transport), "the new destination is eventually
  reached" is true on the buggy code too — it reaches the old one FIRST and
  then the new one. Only the negative half is RED: no request to the old
  endpoint after the switch, and the old endpoint is not the FIRST request at
  startup. This generalizes the name-space bullet above from names to any
  before/after target — endpoints, queues, tables, files. The fixture must also
  make the two targets distinguishable (two profiles with different endpoints;
  a single-profile fixture cannot tell them apart).
  <!-- written_at: 2026-09-04T02:00:00Z  source_event: task:01M1KP68KCJPYTWCAY3A7A5TZ0, git:862fc8f8, git:322d1035 -->
- **A test container is not the production container.** A fixture that builds a bare
  `ServiceCollection` and registers only what the test needs cannot see a defect whose
  cause is what the PRODUCTION registrar adds. The `updateWhere` / bulk-fast-path gates
  asked "is any mutation hook registered?"; `AddBifrostQL` registers four hooks in every
  host, so both features were refused in every shipping deployment while every
  bare-container test stayed green. Any test covering a gate, guard, or fast path whose
  condition reads the DI container must build it through the production registrar
  (`AddBifrostQL` / `BifrostServiceRegistrar`), not by hand.
  <!-- written_at: 2026-09-03T22:00:00Z  source_event: task:01M1KP14CKVVE0FEKMXFVWMSGF, git:292976a2, git:66e2dac0 -->
- **A test that supplies a guard's KEY by hand cannot see how production derives
  it.** Where a limiter, cache, or bucket is addressed by a key the production
  entry point computes (a per-source rate limit, a cache partition, a dedupe
  bucket), a fixture that calls the guard with a fixed literal key proves only
  that the guard counts. The pgwire SCRAM throttle's shipped test passed one
  constant source string and stayed green while `OnConnectedAsync` handed the
  limiter `RemoteEndPoint.ToString()` — "ip:port", a fresh ephemeral port per
  reconnect, so the cap was per-connection and never tripped against the
  reconnect loop it exists to bound. Drive the PRODUCTION entry point
  (`ConnectionContext` / Kestrel seam), and give the fixture >=2 identities that
  are EQUAL on the dimension the key must collapse to and DIFFERENT on the
  dimension it must drop (two `IPEndPoint`s, one address, two ports). LDAPS
  carried the same `RemoteEndPoint.ToString()` keying at the time of writing —
  the key-derivation shape recurs per adapter, so the address-only helper
  (`ProtocolSourceKey.Of`) is the fix and this fixture is what pins it.
  <!-- written_at: 2026-09-04T16:10:00Z  source_event: task:01M1KPC4M29E79XY29MJS0MRYQ, git:a728a6fe, git:dca3f6b5 -->
- **A COLUMN-level policy fixture needs `policy-actions` too, or it is a
  TABLE-level denial wearing a column-level name.** `PolicyConfigCollector`
  sets `HasPolicy` if ANY policy key is present, and `PolicyEvaluator.CanAct`
  then requires the action to be in `AllowedActions` — which
  `policy-read-deny: body` alone leaves EMPTY. So a table declaring only a
  read-deny column is denied WHOLESALE to every non-admin, and a test asserting
  "selecting the denied column is rejected" passes without the column guard
  ever running. Write `policy-actions: read` alongside the deny list whenever
  the subject under test is the COLUMN. This cost the LDAP M20 slice a rewrite
  of its first RED, and the shared conformance fixture
  (`ProtocolAdapterConformanceTests.cs:210`, `documents { policy-read-deny: body }`)
  carries the same defect into 8 derived suites — its
  `Read_SelectingPolicyDeniedColumn_IsRejected` /
  `Read_FilteringOnPolicyDeniedColumn_IsRejected` facts are currently
  table-level denial tests (tracked follow-up).
  <!-- written_at: 2026-09-04T23:10:00Z  source_event: task:01M1KPC4MXF2621FXFZZCVF7ZM, git:0c2b6d37 -->
- **A fixture value must be storable in the column type it exercises.** The
  edit-db BigInt test used a value above int64; it stayed green only until a
  real bound arrived. Pick extremes just inside the real limit.
- **Where TWO bounds narrow the same window, the fixture must make the OTHER
  one bind.** A surface-level cap tested with the server ceiling above it
  exercises only its own arithmetic, so a sentinel or flag that skips the
  server clamp stays invisible. M23's declarative-include test used a 200-item
  cap over a 500-row fixture with `max-query-rows` unset, and went green
  against code whose cap+1 sentinel bypassed `GqlObjectQuery.ClampRowLimit`:
  a `max-query-rows` below 200 clamped the SQL window while the truncation
  flag still waited for row 201, so a full window AT the ceiling reported
  `Truncated=false` — silent partial data, the outcome the flag exists to
  prevent (second occurrence; H7 is the first, and `AggregateTools` was
  already the prior art). Add the case where the server ceiling is the
  binding bound, and assert truncation is reported there — "the window is
  full" is the condition, not "the surface cap was reached".
  <!-- written_at: 2026-09-04T19:30:00Z  source_event: task:01M1KP68F2P0W005FV72CYPAXH, git:78981baa -->
- **A guard that compares a client-supplied value to a typed column must be
  fixtured on a NON-string column.** The wire type is part of the guard's
  correctness: the file-mutation `concurrencyToken: String` argument reached
  `ConcurrencyMutationTransformer` uncoerced, and a TEXT literal compared to a
  SQLite INTEGER column matches no row — every CORRECT token would have read as
  `CONFLICT`, a guard that always fires. A fixture whose token column is TEXT
  cannot manifest that; the positive case must run on the column type the
  coercion exists for (numeric AND temporal where both are supported).
  <!-- written_at: 2026-09-03T23:10:00Z  source_event: task:01M1KPA1T5WSGTZB5B07RFTEFS, git:b353dad4 -->
- **A CORRECTED assertion needs its own revert-proof.** The same slice's history
  cases went RED on the first run for the wrong reason — the assertion matched
  `entity_id = '1'` while `HistoryMutationHook` writes
  `JsonSerializer.Serialize(keyData)`, so a 0-row equality read as the bug. After
  fixing the assertion the pre-fix source must be restored and rebuilt again; the
  earlier proof does not carry over.

## Forced rebuild before the RED run

The revert experiment is only valid if the test runs against a binary built
from the mutated source. In this repo `dotnet test` (and any `--no-build` /
incremental invocation) can leave a **stale `BifrostQL.Core.dll`** after a
source-only edit: the test host loads the old assembly, the mutation never
takes effect, and the revert-proof falsely shows GREEN — a revert-proof
executed against a stale binary is itself vacuous, the exact failure this rule
exists to prevent. Observed on the blind-index read-routing slice: a weakened
operator-gate mutation showed GREEN on the first `dotnet test`; only a forced
`dotnet build src/BifrostQL.Core` (or `touch` + rebuild of the mutated project)
surfaced the expected 6× RED. Both implementer and reviewer hit it.

- **Before EVERY mutant/revert run** (implementer proving, reviewer replaying),
  force a clean build of the mutated project — `dotnet build src/BifrostQL.Core`
  — do not trust an incremental `dotnet test`. A GREEN mutant run is only
  evidence of a vacuous test if you have first confirmed the binary under test
  contains the mutation.
- **Restoring sources with `mv`/`cp` of a backup preserves the backup's OLD
  mtime**, so even an explicit `dotnet build` afterwards can no-op against the
  still-mutated outputs — the suite then "fails" with the mutant's signature
  after you believe you restored. `touch` the restored file (or restore via
  `git checkout`) before the rebuild.
- **Workspace `dist` dependencies stale the same way.** A JS package consumed
  through the workspace by its BUILT output (e.g. the HostedSpa sample
  resolving `@bifrostql/react` via its `dist`) runs the last-built bundle, not
  the edited source: on the paged-envelope slice the spa suite showed 180/180
  GREEN against a pre-change dist and went 64-RED only after
  `pnpm --dir packages/@bifrostql/react build`. Rebuild every built workspace
  dep of the suite under test before trusting a mutant/revert run — vitest
  source aliases (as in the react package's own vitest config) are the
  exception, not the rule.
- **Backstop-guarded fail-closed branches:** when the branch being proven sits
  in front of an independent backstop that ALSO rejects (e.g. a security guard
  downstream of the rewrite), disabling the branch changes nothing — both paths
  reject, so the proof is vacuous a second way. Revert-prove such a branch
  against the specific UNSAFE FALL-THROUGH it prevents (e.g. a raw predicate on
  a column the backstop does not cover), not against merely disabling it.

## An unforgeability claim cannot be proven from inside the trust boundary

A test asserting that some type or capability is unforgeable by external callers
runs in a test assembly that Core grants `InternalsVisibleTo` — so the test CAN
mint the thing it claims nobody can mint, and any runtime negative it writes is
about its own restraint, not about the boundary. Prove such a claim with a
**public-surface reflection assert** (no public constructor, no public factory,
no public settable static / no forgeable scalar flag on the carrier type) PLUS a
runtime negative for the refusal path. The reflection half is the one that goes
RED when someone re-adds a public way in; the runtime half only pins the error.
Source: `MutationRestoreCapability` (M2) —
`protocol-adapter-security.md` invariant 14.

## Related

- `docs/solutions/bifrostql/crypto-blind-index-read-routing-2026-07-24.md`
  — the harvest that added the forced-rebuild + backstop-fall-through subsection.
- `protocol-adapter-security.md` invariant 8 — the fixture-span requirement
  (composite PK, single PK, PK value `0`, pre-existing state) and the original
  revert technique for key-addressed writes.
- `composite-pk-compliance.md` — composite-key fixture coverage.
- `docs/solutions/bifrostql/rss-slice2-vacuous-guard-and-rfc-conformance-2026-07-22.md`
  — the harvest that promoted this rule.
</content>
