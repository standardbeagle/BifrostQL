---
paths:
  - "tests/**"
  - "**/*.test.ts"
  - "**/*.test.tsx"
  - "**/*.spec.ts"
  - "**/e2e/**"
---

# Regression tests must be revert-proven RED

A test that passes against the buggy code guards nothing. Full pre-trim text with incidents: `git show 58d13dfb:.claude/rules/regression-test-non-vacuous.md`.

## The proof

- Before submitting fix + test: restore the pre-fix code, force a rebuild, and confirm the test fails with the bug's own signature. Record the RED in the commit body. Reviewers replay it.
- A stale binary fakes GREEN. Run `dotnet build <mutated project>` before every mutant run, `touch` files restored with `mv`/`cp`, and rebuild workspace `dist` deps (e.g. `pnpm --dir packages/@bifrostql/react build`). Commit the fix first so `git checkout` can't eat it.
- The mutant must reach the assertion under test, not trip an earlier one. A corrected assertion needs its own revert-proof. For characterization baselines, mutate the mechanism, not the expectation.
- Findings inferred from a sibling's bug, refusals ("unifying would change behaviour"), and flake root causes are hypotheses until a fact goes RED or the mechanism is disproved.
- DONE-condition checks are assertions too: name the baseline sha, and run numeric checks against the intended end state before writing them.
- "Value is unrepresentable" is proven by the mutant failing to compile, not by a green suite.
- Unforgeability: a public-surface reflection assert plus a runtime negative. Test assemblies see internals.

## Fixtures

- Build the fixture from the bug's minimal repro, not the shared default. Multi-element bugs need multi-element fixtures: ≥2 placeholders, composite PK, PK `0`, multi-source, ≥2 hops (scope the last), ≥2 different verbs in one batch, pre-existing target state.
- Pick the entry point the bad input can actually reach. The GraphQL front door rejects malformed keys, so drive `IMutationIntentExecutor` instead.
- Closed enumerations: one fact per kind. Conformance-kit facts: revert-prove in every derivation (`git grep` the opt-in flag) and record which facts a mutant should leave green.
- To prove N seams agree, drive both in one fact and compare artefacts.
- Narrowing fixes: pin every shape the broad path served (aliases, duplicates, fragments, `__typename`, empty). Provenance comes from a producer-stamped flag, never a verb.
- Name-space boundaries: names that differ (`sale-price` → `sale_price`) plus a negative assertion on the old name. Re-routing: assert the old target was not hit.
- Use the production registrar (`AddBifrostQL`), the real implementation (not a fake's interface defaults), and the production key derivation (≥2 identities equal on the kept dimension, different on the dropped one).
- Column-level policy fixtures need `policy-actions: read`, or they test table denial.
- SQL text: assert the fragment builder's output at token boundaries, not `Contains` on composed SQL. Response bodies: assert only the region that can carry the value. When splitting an artefact into parts, add negative facts on the parts you don't assert.
- Absence assertions need a paired control proving the absent thing is otherwise produced.
- Changed defaults: pin the consumer's behaviour, and assert payloads negatively (a timeout RED is not a payload RED).
- Injected clocks: set the real timeout far beyond the wait budget, prove with a `TimeProvider.System` mutant, advance per poll.
- Values must fit the column type. Culture fixtures also need a default-culture fact that non-invariant grammar is refused. Swapped symbols must actually change the parse (`"NEG"`, not U+2212).
- Size caps: send a chunked body with no declared length. A declared-size probe must send the bytes it declares.
- Two bounds or N axes: give each its own cap so each binds alone; mutate each forwarding subtype.
- Guards comparing client values to typed columns run on non-string columns.
- Backstop-guarded branches: prove against the unsafe fall-through, not mere disabling.

## Source-scan tests

- Assert the allowlisted positive-hit count (> 0) and give it its own mutant. Strip comments by walking string/char literals, and replay both literal mutants: `//` in a literal on the offender line, and on the anchor line.
- Scan the joined file across newlines. An unclosed argument list is an offender. Prove with a line-wrapped mutant.
- Anchor on the data or API the copy must touch, never on renameable identifiers. Prove with a mutant that renames every local.
- A false positive means the pattern is wrong (e.g. case-insensitive SQL keywords hitting prose). Fix the pattern, don't grow the allowlist.

## De-flaking

Absorb at most one error code on one named call. Leave the strict half strict. Replay the security mutant in every derivation afterwards. Retries, longer timeouts and either-result assertions only re-roll the race. Fix the observing side, not production behaviour.
