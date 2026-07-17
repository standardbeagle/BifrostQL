---
title: "Acceptance criteria describing an unshipped feature: docs follow the code, not the criterion"
written_at: "2026-07-17T21:06:03Z"
source_event: "task:01KX7ZNT9XAYEWRCBBG709C37G"
source_commit: "5ce68c9b10e8f98e67d1e766e7a806d235e276ac"
workspace: "bifrostql"
tags: ["docs", "mcp-slice", "acceptance-criteria", "review-process"]
---

## Problem

Task 01KX7ZNT9XAYEWRCBBG709C37G ("MCP slice 6: MCP server docs") had an
acceptance criterion (#3) requiring the docs to state the mutation surface
"defaults OFF with a per-table allow-list." No per-table allow-list exists
in the shipped `BifrostQL.Mcp` code — the write gate is a single global
`EnableWrites` flag (`BifrostMcpAdapter.cs:69`); per-row/per-tenant scoping
is enforced structurally by the mutation pipeline, not by a table allow-list.

The criterion was written earlier in the epic, before slice ordering
settled the final write-gating design, and was never updated to match what
actually shipped.

## Resolution

The docs commit (5ce68c9) deliberately did NOT document the per-table
allow-list, and explicitly called this out in the commit message: "none
exists in the shipped code." The correctness-review gate caught the
divergence, verified it against source (`grep` of `src/BifrostQL.Mcp`
confirmed no per-table allow-list), and passed the task anyway — reasoning
that criterion #3's own "as implemented" framing makes documenting the
real, shipped behavior the correct outcome, not a criterion violation.

## Durable lesson

When a written acceptance criterion describes a feature that diverges from
what the code actually does (especially in a docs-only or docs-adjacent
task), the correct move is:

1. Verify against source first — do not assume the criterion is accurate.
2. If the criterion's own wording is anchored to "as implemented" (or
   equivalent), the implementation is authoritative — document what
   exists, and explicitly flag the divergence in the commit message so the
   discrepancy is traceable, not silently absorbed.
3. Never fabricate the described-but-unshipped feature to satisfy the
   letter of the criterion. Docs describing a nonexistent allow-list would
   have been a worse outcome than a "criterion mismatch" — it would ship a
   false security claim.
4. A reviewer gate that catches this must verify against source (not just
   re-read the diff) before passing — this review did, citing exact file
   and line evidence for the divergence.

Generalizes beyond this task: any task whose acceptance criteria were
drafted early in a multi-slice epic (before later slices settled the final
design) is a candidate for this drift. Docs/spec tasks closing out such an
epic should independently verify each criterion against HEAD, not assume
epic-time criteria stayed accurate.
