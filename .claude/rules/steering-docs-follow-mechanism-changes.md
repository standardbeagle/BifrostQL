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

<!-- written_at: 2026-09-04T02:00:00Z  source_event: task:01M1KP68KCJPYTWCAY3A7A5TZ0, git:534cadcc; recurrence: H8, H9, H13 -->
