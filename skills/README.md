# Project skills (cross-agent, canonical)

This directory is the **single source of truth** for BifrostQL project skills.
Every AI agent reaches the same content through a per-tool symlink:

- `.claude/skills` → `../skills` (Claude Code)
- `.codex/skills`  → `../skills` (Codex)
- `.agents/skills` → `../skills` (generic AGENTS.md loaders)

Add a skill as `skills/<name>/SKILL.md` (plus any support files). It becomes
visible to all three agents at once — never author a skill inside a single
tool's directory, or it silently stops being cross-agent.

Related repo-level guidance lives at the root: `AGENTS.md` (authority),
`SKILLS.md` (architecture guide), `SKILL.md` (exact-filename loader shim).

> Symlink note: these are git symlinks. A Windows checkout needs
> `core.symlinks=true` (and Developer Mode) or they materialize as plain text
> files. On Linux/macOS/WSL they resolve natively.
