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

<!-- written_at: 2026-09-05T00:00:00Z  source_event: task:01M1KNYNX5NA7CEVRJ4R7BFC7V, git:60fc44b6, git:962cd1e9, git:cf6d48bd; recurrence: M10, M25 -->
