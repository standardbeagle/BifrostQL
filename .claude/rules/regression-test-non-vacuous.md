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
- **Discovery: a finding INFERRED from a sibling's bug is a hypothesis until it
  goes RED at the site it names.** M8's systemic observation named
  `HistoryTableResolver` as carrying the same `SubFields` response-key trap, and
  the slice ran as a bug fix against a defect that does not exist there —
  GraphQL.NET merges same-response-key selections before the resolver, so loss
  needs >=2 ALIASES *and* a reader folding by schema name (the pre-M8 aggregate's
  `nodes[sub.Field.Name] = …`); the history resolver iterates `SubFields.Values`.
  The tell is a task whose evidence section proves the trap at the SIBLING and
  asserts "same trap here". Write that RED first; if it is green, refile as a
  refactor (share the walk) and correct the over-broad sentence that produced the
  inference, rather than shipping a refactor titled as a bug.
  <!-- written_at: 2026-09-06T06:45:00Z  source_event: task:01M1QT7MGM129G7X6NNNMBFJNC, git:1e7b924f -->
  **Mirror image: a REFUSAL to do the work is also a hypothesis until a fact
  goes RED.** The ProtocolSessionHost slice deferred the deadline half of its
  criteria because unifying "would change wire behaviour on two adapters" —
  argued, never tested. The rework wrote the extraction and ran the existing
  H11/M15/M17 deadline facts: all green, so the claim was simply false, and the
  one real difference survived as a named parameter (`clampToIdleWhileArmed`).
  On a REFACTOR-tagged task behaviour preservation is a SUITE RESULT, never an
  inference; "unifying would change behaviour" must name the existing fact that
  goes RED. The tell is a self-report that narrows the acceptance criteria and
  defends the narrowing with an assertion — acceptance criteria are not a menu.
  <!-- written_at: 2026-09-07T02:30:00Z  source_event: task:01M1KP3SDS29TYSRR6PCR6J31A, git:3a4c63e1 -->

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
  Same shape where one walker STAMPS state that another walker READS back:
  M11's `ConnectLinks` stamps the resolved `IDbTable` onto each link node, and
  the link kinds are a closed set (single, multi, many-to-many, and a nested
  link on a child) — a fixture covering one kind leaves a missing stamp on the
  others GREEN, because nothing else re-derives the identity. One fact per
  kind, asserting the stamped value by reference AND the SQL it produces.
  <!-- written_at: 2026-09-06T05:45:00Z  source_event: task:01M1KNYP1FA26W4H3R9GT4DKCB, git:e690aaa0 -->
- **A shared conformance-kit fact must be revert-proven in EVERY opt-in
  derivation, not in one.** The kit's test body is shared, but each derived
  suite supplies its own hook/fixture, and the vacuity lives in the hook. The
  frame-cap fact was proven RED on the RESP derivation and shipped vacuous on
  the LDAP one. Run the mutant against each suite that sets the opt-in flag.
  <!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1N05460G65T9XXDAPMHGKKS, git:31990a6d -->
  Two things that rule depends on. **Enumerate the derivations by `git grep` of
  the flag, not from memory** — review attempt 1 asked for proofs across "8
  mutation-capable derivations" and there are 5. And **one mutant need not take
  every derivation RED.** The cross-op-class parity fact needs two: code-mapped
  wires (gRPC, RESP, MCP) diverge when a throw loses its `ErrorCode`,
  message-forwarding wires (Echo, BinaryTransport) only when the text itself
  changes. Record which facts each mutant is EXPECTED to leave green, and why,
  or the next reviewer reads a partial RED set as vacuity. Same for a positive
  CONTROL in a deadline suite: it stays green under the clock mutant by
  construction, so validate it with a second mutant on the decision it actually
  guards (skipping `RetireOnCredentialedAction` took it RED alone).
  <!-- written_at: 2026-09-07T02:30:00Z  source_event: task:01M1KP3SDS29TYSRR6PCR6J31A, git:b2377b75, git:2e40df44 -->
  Same shape without a kit: **a wire-parity fact (`protocol-adapter-security.md`
  invariant 9 — same condition, byte-identical response) pinned on ONE of N
  sibling seams says nothing about the other N-1.** M11-w pinned the
  unknown-vs-ambiguous byte-identical miss on `IMutationIntentExecutor` only,
  while the same resolution rule ran in three `File*Resolver`s that review had
  to fact separately. Enumerate the seams that share the contract and write one
  fact per seam.
  <!-- written_at: 2026-09-06T08:05:00Z  source_event: task:01M1THZTA5S39N7JKYFXJQ5YTW, git:d4cd685b -->
- **Narrowing a broad path needs facts for the shapes the BROAD path already
  served.** The bullet above spans the population that must FAIL; this is its
  complement — the population that must keep WORKING. When a fix replaces
  "do it for everything" with "do it for what was selected/requested/matched",
  the RED fact only proves the narrowing happened; every input the old code
  served correctly is now un-covered, and the suite stays green while valid
  requests break. M8 narrowed aggregate value columns from every numeric column
  to the selected sub-fields: it went GREEN with a one-shape fixture, and review
  found THREE valid queries the pre-fix resolver served and the fix broke —
  `_sum { __typename amount }` (threw), `_sum { total: amount amount }` (generic
  DB error), `s1: _sum { amount } s2: _sum { id }` (dropped columns, served
  null). Before submitting a narrowing fix, enumerate the selector's own
  input grammar — aliases, duplicates, siblings under different aliases,
  fragments (named and inline), introspection fields, empty selections — and
  pin one fact per shape. Assert the OUTPUT ARTEFACT (the generated SQL text,
  the built predicate), not only the downstream outcome: M8's policy-outcome
  assertion was true for all three broken shapes.
  <!-- written_at: 2026-09-04T00:00:00Z  source_event: task:01M1KNYNEFYP8M0RC70387X4SM, git:85f2131e, git:87ea68c1 -->
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
  Same shape one level down: **a test FAKE inherits the interface's DEFAULT
  bodies, so a fact written over the fake is not a fact about the production
  override.** Three of M31's eight new facts ran against `DbModelTestFixture`
  and exercised `IDbModel`'s default `TryGet*` implementations while
  `DbModel`'s own overrides (built on its lazy indexes) stayed uncovered —
  green whatever they did. Any fact about a member the interface supplies a
  default for must construct the real implementation (as
  `EnumTableAmbiguityTests` does), not the fake.
  <!-- written_at: 2026-09-05T08:30:00Z  source_event: task:01M1MN9W82B2J1S2J3TQ3FJ0K0, git:68c2f8e1 -->
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
  (`ProtocolAdapterConformanceTests.cs`, `documents { policy-read-deny: body }`)
  carried the same defect into 8 derived suites: its
  `Read_SelectingPolicyDeniedColumn_IsRejected` /
  `Read_FilteringOnPolicyDeniedColumn_IsRejected` facts were table-level
  denial tests. Fixed in task `01M1QAJZ3ET9XHQJGWNTZ47GSQ`: the fixture now
  carries `policy-actions: read`, the select fact
  (`Read_SelectingPolicyDeniedColumn_IsStopped`) is per-adapter Reject/Omit
  via `DeniedColumnSelection`, and forcing `IsColumnAllowed` to Allow on the
  read path takes both facts RED in all 8 derived suites (16 RED, replayed
  in review).
  <!-- written_at: 2026-09-04T23:10:00Z  source_event: task:01M1KPC4MXF2621FXFZZCVF7ZM, git:0c2b6d37 -->
  <!-- amended_at: 2026-09-05T09:30:00Z  source_event: task:01M1QAJZ3ET9XHQJGWNTZ47GSQ, git:ea632a0e -->
- **A substring assertion over composed SQL is not a fact about the fragment
  under test.** The M9 nested-collection RED asserted the restricted join-id
  sub-query contained `LIMIT 100`, and it passed against provably unpaged code:
  the composed flat-collection statement embeds that sub-query AND appends its
  own `LIMIT 10000`, whose text contains `LIMIT 100` as a prefix. Any SQL-text
  fact about a fragment that a larger statement later embeds must assert on the
  BUILDER's output for that fragment (here `GetRestrictedSqlParameterized`), not
  on the composed statement set — and where the keyword recurs with different
  operands, match a token boundary or parse with ScriptDom rather than
  `Contains`. The implementer self-caught this one; the generalization is that a
  numeric literal in a SQL-text assertion is a prefix of every longer literal.
  <!-- written_at: 2026-09-05T04:30:00Z  source_event: task:01M1KNYNQAKE5FHC06NKF607N4, git:7ce42c98, git:063f6acb -->
- **Splitting a composed artefact into PARTS un-covers every part an assertion
  stops reading.** The inverse of the bullet above: where the old fact asserted
  on the combined text, a migration to the parts (`TableFilter.RenderParts`'s
  `.Where` / `.Joins`) narrows what each fact can see — the FTS predicate tests
  surfaced only `.Where`, so a search predicate that began emitting a join went
  unobserved, while the retired single-fragment render would have shown it.
  When migrating assertions from a composed artefact to its parts: every site
  gains a NEGATIVE fact on the parts it does not assert
  (`parts.Joins.Should().BeEmpty()`), and a test helper must not return one part
  while discarding the others — return the whole shape and let each fact narrow.
  <!-- written_at: 2026-09-05T14:30:00Z  source_event: task:01M1RX501DZFH39JDV61V0G231, git:860347aa, git:481b9aba -->
- **A source-scan / hygiene test must assert the ALLOWLISTED hit, not just zero
  offenders.** "No offender matched" and "the pattern set matched nothing at
  all" are the same GREEN, so a scan whose regexes drift away from real code
  guards nothing while reading as coverage. Count the hits inside the
  allowlisted home and fail when that count is 0 (`KeyParserHygieneTests`:
  removing the split from `ToolJson.cs` goes RED). The pattern set must also
  span the language's current spellings of the construct — the original four
  `.Split` shapes missed the C# 12 collection expression `.Split(['|'])`, which
  a net10 assembly can legally use, so a real offender would have passed.
  <!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1MWB3HVPFH6W3VQDH20Q58N, git:a4b8fb4e -->
  **A source scan reads the FILE, not the LINE, and an unparseable call is an
  offender.** C# call arguments wrap freely, so a per-line scan sees a
  truncated argument list: M11's read-path scan counted
  `GetTableFromDbName(\n    TableName)` as schema-qualified because the line
  had an unclosed paren, so the restored bare lookup passed AND inflated the
  positive-hit count that proves the scan non-vacuous — the two halves of the
  guard failed together. Join the file (blank comment lines rather than
  removing them, so reported line numbers stay real), match at token boundaries
  across newlines, and treat an argument list that never closes as a hit, never
  as a trusted one. Prove it with a LINE-WRAPPED mutant, not only a one-line
  one; a mutant formatted the way the fixed code is formatted tests nothing.
  <!-- written_at: 2026-09-06T05:45:00Z  source_event: task:01M1KNYP1FA26W4H3R9GT4DKCB, git:e690aaa0 -->
  **Anchor a duplicate-detection scan on the DATA the copy must read, never on
  identifiers a copier is free to rename.** The mount-auth scan's anchor was the
  literal `multiDb.Endpoints.Count == 1 ? multiDb.Endpoints[0]` plus a
  `*AuthRequirement` method-name filter, so a re-added second derivation that
  named its local anything else passed GREEN — review reproduced it with a stub
  `MutantCopy.Gate(opts, p)` carrying the full derivation body. A local name, a
  method name and a variable spelling are all free for the copier to change; the
  PROPERTY or API the derivation cannot avoid touching (`.DisableAuth`) is not.
  Anchor on that read, allowlist the one helper and the options file that declare
  the flag, and prove the scan with a mutant copy that renames every local — a
  scan proven only against a verbatim copy tests the copier's laziness.
  <!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1MJATQMGDT1JGAYCBEVMMA8, git:879b4cae -->
  **The positive-hit half needs its OWN mutant, and PROSE in the home can
  satisfy it.** The offender half is proven by a copy; nothing proves the count,
  so it is where a scan rots silently. The pre-auth deadline scan anchored on
  the configured-option READ and counted matches in raw file text —
  `ProtocolSessionHost.cs` names `AuthenticationTimeout` in its own XML doc
  comments, so the count was met by prose and the anchor-drift mutant (the home
  hardcodes a 30 s literal instead of reading each adapter's options) stayed
  GREEN. Run that mutant, and strip comments before matching whenever the anchor
  is a name a doc comment would naturally write — the two prior scans in this
  repo are accidentally safe only because prose rarely writes
  `Interlocked.CompareExchange(ref `. Strip by walking string and character
  literals, not skipping them: a `"//"` inside a literal otherwise opens a
  comment that swallows every read after it. Corollary from the same slice: an
  anchor on an OPERAND shape (`CompareExchange(ref x, y + 1,`) is defeated by
  hoisting the expression into a local — anchor on the CALL.
  <!-- written_at: 2026-09-07T02:30:00Z  source_event: task:01M1KP3SDS29TYSRR6PCR6J31A, git:39b419b0, git:130f70e0 -->
- **Changing a DEFAULT value is a change to every branch that CONSUMES it.**
  The edit-db connection form's Postgres default moved from `postgres` to
  `Environment.UserName`, and the tests asserted only that the new value rode
  the payload — but `PsqlDatabaseLister` spawns `sudo -u <user> psql` for any
  non-null user, so a fresh host still failed (`sudo -u <self>` refused, no
  sudo in a container). Pin the CONSUMER's behaviour under the new default —
  extract a pure seam that returns the artefact (here `BuildProcessStartInfo`,
  asserting the argv actually spawned), not just that the default is sent.
  Same slice, second half: a RED that fails by TIMEOUT on a missing label is
  not a RED on the payload — it goes red against the fixed code too. Assert the
  payload NEGATIVELY (`!== 'postgres'`) so only the defect can produce the
  failure.
  <!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1QNKK8C17R76EWBFW81VRJJ, git:2e8b5a67, git:c6c281b3 -->
- **An injected clock proves nothing while the code under test can still
  reach the real deadline.** `protocol-adapter-security.md` invariant 15
  requires a deadline fact to advance an injected clock rather than sleep;
  the vacuity is that advancing it is not the same as DRIVING it. pgwire's
  fact injected a clock, advanced it, and stayed GREEN against a handler
  that ignored it — the handler still awaited `Task.Delay(remaining)` on the
  wall clock, and the fixture's real timeout (400 ms) was inside the
  harness's own wait, so the wall clock satisfied the assertion either way.
  Two requirements: set the REAL timeout far beyond the test's wait budget
  (10 min vs 5 s) so only the fake provider can fire it, and run the mutant
  "handler substitutes `TimeProvider.System`" — it must go RED. If any await
  on the path still measures real time, the seam is decorative. (Advance the
  clock per poll, not once: the admission slot is taken at accept, BEFORE the
  timer is armed, so a single early advance can land ahead of the timer and
  never fire it.)
  <!-- written_at: 2026-09-06T03:30:00Z  source_event: task:01M1N2VV1T7KASK90QR6JKGKAT, git:43c11a51 -->
- **A fixture value must be storable in the column type it exercises.** The
  edit-db BigInt test used a value above int64; it stayed green only until a
  real bound arrived. Pick extremes just inside the real limit.
- **A culture fixture proves the PROVIDER argument, not the number GRAMMAR.**
  The `concurrencyToken` fix passed `CultureInfo.InvariantCulture` to
  `long.Parse` / `decimal.Parse` in `FilePointerAccess.CoerceToken` and pinned it
  with a de-DE fact, which was green against the defect still there:
  `decimal.Parse(s, provider)` defaults to `NumberStyles.Number` and
  `double.Parse(s, provider)` to `Float | AllowThousands`, so `"1,5"` reads as
  15 on EVERY host — a malformed token matched the stored `15` and the guarded
  write passed the lost-update guard. A culture-swap fixture can only vary the
  provider, so it cannot see this; pair it with a fact in the DEFAULT culture
  that a token outside the invariant wire form (group separator, surrounding
  space, exponent where none is legal) is REFUSED as invalid, not reinterpreted.
  Fix by naming the STYLE (`NumberStyles.Integer` / `NumberStyles.Float`), not
  only the provider: the provider chooses the glyphs, the style chooses the
  grammar.
  <!-- written_at: 2026-09-05T18:10:00Z  source_event: task:01M1RX4ER8SQZW37SZABPGJRZR, git:c7837344, git:cfe1fec4, git:5f74dcf7 -->
- **A culture-swap fixture must pick a symbol the runtime does NOT normalise.**
  The negative-literal RED was first written with `NegativeSign = "−"`
  (the typographic minus), and went GREEN against provably buggy code: .NET
  number parsing accepts U+002D and U+2212 as minus under EVERY culture, so the
  swapped culture parsed the wire's `"-5"` exactly as the invariant one did. The
  fixture only became RED with `NegativeSign = "NEG"`. Before trusting a RED
  built on a swapped `NumberFormatInfo` symbol (negative sign, decimal or group
  separator, digit shapes), verify the swap actually changes the parse outcome —
  parse the literal under both cultures and assert they differ, or pick a
  multi-character sentinel the BCL cannot fold back to the ASCII form.
  <!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1S8E86KMBAJ13XHBDVCHGBT, git:0cbb405d -->
- **A size cap on a wire has TWO inputs — declared and received — and the
  fixture must make the RECEIVED one bind.** A body/frame cap is normally two
  branches: an early refusal on the client-declared size (`Content-Length`, a
  length prefix) and an accumulator over the bytes that actually arrive. A
  fixture built from a buffered body (`StringContent`, a byte array) always
  declares its length, so it exercises only the first branch, and a mutant that
  deletes the accumulator stays GREEN. Send a CHUNKED / streamed body with no
  declared length (or a lying one) so only the received-byte check can reject
  it. Second instance of the same shape: the binary WebSocket reassembly cap
  already counts received bytes rather than the client's declared total
  (AGENTS.md, `/bifrost-ws` row), and M14's chat POST cap repeated it on HTTP.
  The mirror half: a probe of the DECLARED branch must actually SEND the bytes
  it declares. The LDAP frame-cap probe declared 64 KiB and wrote 64 bytes; on
  unguarded code the truncated stream's EOF throws the SAME
  `LdapProtocolException` the cap throws, so the mutant stayed GREEN — a
  short-write fixture proves nothing about a declared-size guard.
  <!-- written_at: 2026-09-05T03:10:00Z  source_event: task:01M1KP3S56ACGSETB02AMK4Z00, git:6f95328f -->
  <!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1N05460G65T9XXDAPMHGKKS, git:31990a6d -->
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
- **Where a guard counts on N INDEPENDENT AXES, the fixture must make each axis
  bind ALONE, or an axis can be dropped silently.** Sibling of the bullet above:
  that one is two bounds on one window, this is one bound per axis. Every RESP
  auth-limiter fact set the per-SOURCE and per-ACCOUNT caps to the same value and
  drove a single source, so the two axes were indistinguishable — a subtype
  forwarding `TryAdmit(source, null)` (dropping the account axis entirely) stayed
  GREEN, and the shared base's own mutant proof said nothing about which subtype
  forwards which argument. Give each axis a DIFFERENT cap and >1 identity on the
  other axis, so only the axis under test can trip. Applies to every fold of N
  copies into base + forwarding subtypes (`ProtocolAuthAttemptLimiter`,
  `ProtocolConnectionLimiter`): mutate each SUBTYPE's forwarding, not only the
  base — a per-adapter brute-force bound is lost silently otherwise.
  <!-- written_at: 2026-09-05T23:30:00Z  source_event: task:01M1N0546XVQTX1Q619JJ2ABFT, git:1b79cee3 -->
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
  `git checkout`) before the rebuild. **But `git checkout -- <file>` restores
  the COMMITTED content, so it also throws away an uncommitted fix living in
  the mutated file** — M11-w's scan mutant and the fix were both in
  `MutationIntentExecutor.cs`, and the restore silently reverted the fix,
  recovered only from a scratch backup. Commit the fix BEFORE the mutant run
  (or restore from the backup and `touch` it), so `git checkout` is safe by
  construction.
  <!-- written_at: 2026-09-06T08:05:00Z  source_event: task:01M1THZTA5S39N7JKYFXJQ5YTW, git:d4cd685b -->

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
