---
written_at: 2026-07-24T00:30:00Z
source_event: task:01KXHR3JW5W0NGTM4E66SSVCN3
module: bifrostql
category: best-practices
confidence: high
form: procedure
sources:
  - task:01KXHR3JW5W0NGTM4E66SSVCN3#comment-01KY8PG8382SCZYD25CTT6Z7JS   # worktrack_completion_v1 revertProofs
  - task:01KXHR3JW5W0NGTM4E66SSVCN3#comment-01KY8QGEDR51SXQ8J44NEPTXGC   # review_annotation_v1 passPatterns + systemicObservations (independent reproduction)
  - git:c791be6   # refactor: extract single-definition ComputeSearchToken
  - git:df5c0e2   # test RED: pin equality routing
  - git:a668b30   # feat GREEN: route encrypted-column equality onto _bidx
related:
  - .claude/rules/regression-test-non-vacuous.md   # stale-dll + backstop fall-through now promoted into the rule
  - docs/solutions/bifrostql/crypto-key-rotation-versioned-envelope-2026-07-19.md
tags: [crypto, blind-index, read-write-symmetry, shared-derivation, revert-proof, backstop-guard, stale-dll, non-vacuous, module-slice]
status: steering
recurrence: 1
---

# Crypto blind-index read routing: read/write derivation symmetry + non-vacuous fail-closed proofs

**Task**: Crypto blind-index query routing — equality-predicate rewrite onto
`_bidx` (01KXHR3JW5W0NGTM4E66SSVCN3). Security review PASS attempt 1, 0
blockers, clean run (no rewinds). Rich signal despite the clean run.

## Lessons

1. **When a new read path must reproduce a value a write path already stored,
   extract the derivation into ONE definition as its own preparatory refactor
   commit FIRST — then verify byte-identity against the pre-refactor write
   source.** The round-trip guarantee (a value encrypted on write is findable
   by the same plaintext on read) becomes *structural* rather than coincidental:
   both sides call `BlindIndexComputer.ComputeSearchToken` (invariant-culture
   `Convert.ToString` → per-column `GetBlindIndexKey` → keyed `Compute`), so
   they cannot drift and existing stored `_bidx` values stay findable. Reviewer
   independently checked byte-identity against the pre-refactor path — that
   check is the acceptance gate, not the passing round-trip test.

2. **A revert-proof of a fail-closed branch that sits in front of an
   independent backstop is vacuous unless it targets the branch's specific
   unsafe fall-through.** The rewrite's fail-closed rejection (unresolvable key
   manager) shares the `EncryptedColumnReadGuard` backstop: disabling the
   fail-closed branch changes nothing because the guard still rejects. The
   correct revert-proof mutates the branch into the *raw-value fall-through it
   prevents* — routing the unhashed value onto `_bidx` (a column the guard does
   NOT cover) — which the guard cannot catch, so the test genuinely goes RED.

3. **`dotnet test` incremental builds can mask a revert-proof RED with a stale
   `BifrostQL.Core.dll`.** A source-only mutation showed GREEN on the first run;
   a forced `dotnet build src/BifrostQL.Core` was required to observe the 6× RED.
   Promoted into `.claude/rules/regression-test-non-vacuous.md` ("Forced rebuild
   before the RED run") — it is a correctness hole in that already-promoted
   rule's procedure, so it belongs at project-rule tier, not just here.

## What didn't work / traps avoided

- Revert-proving fail-closed by "return the leaf unchanged" — vacuous, the guard
  still rejects. Had to mutate to the raw-predicate fall-through.
- Trusting the first `dotnet test` GREEN on a mutant — stale Core.dll.

## Why it recurs

Any feature that carves an equality/search read path out of encrypt-on-write
storage (aggregate link filters, grouped aggregates, binary DSL) faces the same
derivation-symmetry requirement AND the same backstop-guard revert-proof
subtlety. `ComputeSearchToken` serializes null → `""`, so any future caller
routing null values conflates null with empty string (the `_eq` path guards
this; a naive `_in`/aggregate path would not).

## Apply when

- Building a read path that must find values a sibling write path stored.
- Writing a revert-proof for a fail-closed branch backed by an independent guard.
- Extending blind-index equality routing to aggregate/grouped/DSL surfaces
  (route through the same `RewriteBlindIndexEquality` walk, not a second copy;
  decide null-element handling explicitly).

## Prevention

- Extract shared write/read derivation as a standalone refactor commit; reviewer
  verifies byte-identity against pre-refactor source.
- Never edit a fail-closed security guard to add transparent routing — insert
  the narrow rewrite upstream and reuse the guard's exact
  `FilterDeniedMessage`/`AccessDeniedCode` so there is no differential oracle
  across rejection sites.
- Force-rebuild the mutated project before every mutant run (see the rule).
