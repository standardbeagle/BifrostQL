# Retiring a mechanism includes the sentences that describe it

A fix that replaces or removes a mechanism must, **in the same commit**, update
every `AGENTS.md` / `docs/` sentence and snippet that describes the retired one.

Review caught this three times on the `review-2026-09` drain, each on a
different surface, none of them security work:

- H8 — the HTTP mount-order fix left AGENTS.md describing the old order.
- H9 — the MCP default flip left a `docs/` snippet constructing the old default
  (see `protocol-adapter-security.md` invariant 12, the security-scoped
  instance of this same shape).
- H13 — AGENTS.md still said `App.tsx` built the transport in a `useMemo` and
  remounted the editor by bumping `editorKey`; both were gone after 322d103,
  and the fix had removed `editorKey` precisely because it caused the race the
  old sentence would tell the next implementer to reintroduce
  (corrected in 534cadcc).

## The rule

- **Implementer:** before submitting, grep the steering corpus for the names the
  change retires — the removed identifier, the old option name, the old call
  shape — and fix or delete every hit. Stage those edits with the fix, or as an
  adjacent `docs(...)` commit in the same attempt. Report the grep in the
  commit body so review confirms rather than re-derives.
- **Reviewer:** for any diff that removes or renames a mechanism, run the same
  grep. A stale steering sentence is not a cosmetic defect: it is the
  instruction the next implementer follows, so it reintroduces the bug the
  commit just fixed.
- Prose is not the only carrier. A **code snippet** showing the old
  construction is load-bearing in exactly the same way, and a reader copies it
  verbatim.
- **A replacement named in a breaking entry must be REACHABLE by the audience
  the entry addresses.** A CHANGELOG breaking note tells out-of-tree callers
  what to migrate to, so naming an `internal` member is a false claim, not a
  terse one: the caller cannot compile it. The `TableFilter.ToSqlParameterized`
  removal pointed at `RenderParts`, which is internal — the honest entry states
  the real end state (no public read-side render remains by design; route
  through the engine seams, `RenderForMutation` is the only public render).
  Check the accessibility of every symbol a migration note names; where nothing
  public replaces it, say the removal is deliberate and name the seam.
  <!-- written_at: 2026-09-05T14:30:00Z  source_event: task:01M1RX501DZFH39JDV61V0G231, git:1598160f -->
- **A UNIFICATION writes its end state into the docs before every consumer is on
  it.** The retirement case above is prose left describing the old mechanism;
  this is prose describing the NEW one too early. Folding three pre-auth
  deadlines into one session host shipped an AGENTS.md sentence saying the read
  timer runs on `TimeProvider` "or the injected clock is decorative" — true of
  RESP, false of LDAP, whose handler no longer held a `TimeProvider` and still
  armed `idle.CancelAfter(deadline)` on the wall clock. Every existing fact
  stayed green, because they all exercised the arm/retire ARITHMETIC, never the
  await. Before writing a coverage sentence about a unified control, grep the
  path for the retired mechanism (`CancelAfter`, `Task.Delay`,
  `CancellationTokenSource(`) and pair each hit with the new seam. **And when
  review finds such a sentence false, check the repair direction first**: both
  the sentence and the annotation described what the code SHOULD have done, so
  fixing the code made them true and no prose changed — cheaper and safer than
  weakening a security claim to match a defect.
  <!-- written_at: 2026-09-07T02:30:00Z  source_event: task:01M1KP3SDS29TYSRR6PCR6J31A, git:3b549156, git:dfaa0bb4, git:19c911a6 -->

<!-- written_at: 2026-09-04T02:00:00Z  source_event: task:01M1KP68KCJPYTWCAY3A7A5TZ0, git:534cadcc; recurrence: H8, H9, H13 -->

## Retiring a VALIDATED key breaks shipped config, not just prose

Where the retired name is a metadata key or config option that a validator
rejects when unknown, a stale carrier is not a misleading sentence — it is a
**startup failure in the shipped product**. `ModelConfigValidator` throws on an
unrecognized `:root` key, so M10's removal of `dynamic-joins` from
`KnownDatabaseKeys` meant `src/BifrostQL.Host/appsettings.json` and
`templates/bifrost/appsettings.json` — both still declaring the key — would have
failed `dotnet run` and every project scaffolded from the template. The
implementer's own report did not mention it; review found it (`60fc44b6`).

So the grep is over the REPO, not the steering corpus. For any retired
identifier, key, or option name, `git grep` the whole tree and clear every hit
in these carriers before submitting:

- `**/*.json` / `**/*.kdl` — shipped `appsettings`, sample and fixture config.
  A validated key here is the startup-failure carrier.
- `templates/` — scaffolded output inherits the stale key, and nothing in the
  repo's own test run exercises it.
- `README.md`, `SKILLS.md`, `CHANGELOG.md` — reader-facing carriers outside
  `docs/`. A removal is a breaking change and needs its CHANGELOG entry.
- `tests/**` — an assertion on the OLD shape is a carrier too: it either fails
  (noise) or, worse, still passes and pins the retired behaviour. M10 left a
  UI.Tests assertion inverted (`962cd1e9`).
- `examples/` — copied verbatim by readers, same as a docs snippet.

Corollary for planning: a removal task's `fileScope` must be derived from
`git grep <identifier>` across the whole repo, **not** from the files that
implement or emit the mechanism. M10 was scoped to the emitter and missed four
carriers; M25 was scoped to the SDL and missed the reader. The emitter is where
the change starts, never where it ends.

A MOVE or rename is a removal of the old name, and the grep has one more
reader: a characterization or pinning test that binds the symbol. CHAR-3's
flow step ordered `TableMutationPipeline.SelectPredicateColumns` into
`MutationArgumentBinder` while its DONE-CONDITION required zero diff to the
CHAR-1/CHAR-2 files, and `BulkBatchPlanCharacterizationTests.cs` binds that
symbol at two sites; the move was un-performable by construction and cost an
implement attempt (CS0117 x2) before review dropped it. A plan that moves a
symbol names who binds it, and a pinned test binding it means the move waits
for the slice that owns re-baselining that test, never a "follow the symbol"
edit to the pinning file.

<!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1KNYNX5NA7CEVRJ4R7BFC7V, git:60fc44b6, git:962cd1e9, git:cf6d48bd; recurrence: M10, M25 -->
<!-- amended_at: 2026-09-07T23:30:00Z  source_event: task:01M1W0HB3ZB4BC85BAP20R37DK, git:6f57646d, git:7bc1f56d; recurrence: M10, M25, CHAR-3 -->
