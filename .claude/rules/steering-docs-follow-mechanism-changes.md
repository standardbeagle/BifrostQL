# Retiring a mechanism includes every carrier that names it

A change that removes, renames or moves a mechanism (identifier, option, metadata key, call shape) updates every carrier of the old name in the same commit, or in an adjacent `docs(...)` commit. Report the grep in the commit body. Reviewers rerun it.

- `git grep <old name>` across the whole repo, not just the steering docs. Carriers: `AGENTS.md`, `.claude/rules/`, `docs/`, `README.md`, `SKILLS.md`, `CHANGELOG.md`, `examples/`, `templates/`, shipped `**/*.json` / `**/*.kdl` config, and tests that assert the old shape.
- A validated key left in shipped config (`appsettings.json`, templates) is a startup failure, not a stale sentence.
- Code snippets carry as much weight as prose: readers copy them verbatim.
- A breaking-change entry names only replacements its audience can reach (public symbols). If nothing public replaces the old one, say so and name the seam.
- Don't write a coverage claim about a unified mechanism until every consumer is on it. Grep for the retired mechanism first (`CancelAfter`, `Task.Delay`, …). If review finds such a claim false, check whether fixing the code is the right repair.
- Removal tasks take their file scope from the repo-wide grep, not from the emitter. A move must name the pinning tests that bind the symbol.
