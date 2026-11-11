---
name: tinadec-skill-authoring
description: Author, validate and package a Tinadec skill (SKILL.md, references, scripts, assets). Use when the user asks to create or fix a skill, or when a skill in the workspace is rejected by the loader.
version: 1.0.0
---

# Authoring a Tinadec skill

A skill is a directory whose `SKILL.md` explains a procedure the agent should follow. The loader shows the model only `name` and `description`; the body and every resource stay on disk until the model asks for them.

## Minimal layout

```
my-skill/
  SKILL.md          # required
  references/       # optional: longer documents the body points at
  scripts/          # optional: helper programs (shown, not executed by the loader)
  assets/           # optional: templates and binaries
```

`SKILL.md` must start with YAML frontmatter:

```markdown
---
name: my-skill
description: One sentence that tells the model when to use this skill.
version: 1.0.0
---

# My skill
...
```

Rules:

- `name` must be kebab-case (`[a-z0-9]` joined by single hyphens) and must equal the directory name.
- `description` is required, one line, at most 1024 characters. It is the only routing signal; write it as "Use when ...".
- `SKILL.md` must be at most 256 KiB. The whole package is limited to 256 files and 16 MiB.
- Do not duplicate another skill's name in the same scope. Put the more specific one in `<workspace>/.tinadec/skills`.
- `disabled: true` keeps a valid skill on disk but out of the index.

## Where skills are discovered

| Rank | Root | Notes |
| --- | --- | --- |
| 0 | built into Tinadec | read-only; copy to a project to edit |
| 100 | `<workspace>/.tinadec/skills` | canonical project root |
| 200 | `~/.tinadec/skills` | user library |
| 300 | `<workspace>/.agents/skills` | compatibility, read-only |
| 310 | `~/.agents/skills` | compatibility, read-only |

Lower rank wins a duplicate name. A shadowed skill stays visible in diagnostics, so the user can see why an edit had no effect.

## Procedure

1. Pick a kebab-case name and create the directory in the canonical project root.
2. Write `description` first: one sentence, task-oriented, no marketing.
3. Put the durable procedure in `SKILL.md`; move long tables, schemas and examples to `references/` and link them from the body.
4. Keep scripts small and deterministic. The loader does not execute them; the agent must run them through the approved shell tool.
5. Validate: `GET /api/v1/tools/skills` must show the skill with `valid: true`. If it is rejected, read `reason_code` (`name_invalid`, `name_directory_mismatch`, `description_missing`, `no_frontmatter`, `disabled_in_file`, `duplicate_name`).
6. Ask the user before writing into a shared library or installing from a repository: those actions are approval-gated.

## Do not

- Do not inline secrets, tokens or absolute personal paths.
- Do not rely on the description alone to carry instructions; the model may see only the description until it loads the body.
- Do not create a second skill with the same name to "override" another root; change the canonical one or disable it.
