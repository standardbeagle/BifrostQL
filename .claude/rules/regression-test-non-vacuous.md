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
single-column PK, `id=1`, a single data source, no pre-existing state at the
target. A bug that only manifests with multiple elements (>=2 placeholders,
composite PK, PK value `0`, multi-source table, pre-existing target content)
cannot be exercised by such a fixture. A test targeting a multi-element failure
mode needs a **dedicated fixture variant** that makes the fixed and pre-fix
implementations produce provably different output.

- Construct the test fixture from the bug's *minimal reproduction*, not the
  default fixture.
- Parameterize the shared builder with a default preserving prior behavior
  rather than mutating the shared fixture (keeps blast radius to one test).
- **A fixture value must be storable in the column type it exercises.** The
  edit-db BigInt test used a value above int64; it stayed green only until a
  real bound arrived. Pick extremes just inside the real limit.

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
