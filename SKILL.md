# BifrostQL Project Skill

This file exists for agent skill loaders that discover project guidance by the
exact filename `SKILL.md`.

Use these repo guides together:

- `AGENTS.md` for maintainer constraints, generated-output boundaries, package
  manager rules, metadata-key rules, and query-builder safety requirements.
- `SKILLS.md` for the longer BifrostQL architecture and implementation guide.
- `CLAUDE.md` for concise AI-tool reference notes.
- `skills/` for packaged, per-skill project skills. This directory is the
  single cross-agent source of truth; each tool reaches it through a symlink
  (`.claude/skills`, `.codex/skills`, `.agents/skills` → `../skills`). Author
  new skills as `skills/<name>/SKILL.md`, never inside one tool's directory.

When changing code, follow `AGENTS.md` first if guidance overlaps.
