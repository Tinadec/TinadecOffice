# SHARED DEVELOPMENT FILES

**Generated:** 2026-10-05
**Last Updated:** 2026-11-11
**Last Updated By:** Denv 文档迁入 `Tinadec/withdocs` 私有仓库，补充共享输出例外
**Last Verified Commit:** 27af3ab0 + 本轮 AGENTS.md 准则改动（未提交）
**Branch:** main

Follow the root `AGENTS.md` and [OUTPUT-POLICY.md](OUTPUT-POLICY.md). This directory is tracked; native tool state is ignored.

- Keep the original source/provenance for imported plans, reports and wiki pages. Preserve history without treating it as current product truth.
- Existing module architecture/status/tasks remain authoritative in `../docs/development-program/`; new process artifacts live here and link to the relevant module or Task ID.
- **Denv, writing-module and collaboration-module discussion docs do not live here.** They are in the private `Tinadec/withdocs` repository under `tinadec-denv/` (Tinadec organization members only), indexed by `tinadec-denv/README.md`. When a Denv, writing-module or collaboration-module discussion starts, locate that repo, revise the existing numbered document, tell the user where the docs live, and never recreate them under `.tinadec_dev/specs/` or copy them to a public location. See the 2026-11-11 entry in the root `AGENTS.md`.
- Skills are maintained only in `skills/`. Native `.agents/skills`, `.claude/skills` and `.opencode/skills` are local links to that directory.
- `MIGRATION-MANIFEST.json` records this import's source and original hashes, including duplicate shadcn resources. Do not silently overwrite history when two files have the same name.
- `scripts/setup-ai-workspace.mjs` preserves old local directories under ignored `tmp/ai-workspace-backups/` and creates only document/skill-directory links. Do not link an entire private tool configuration/cache folder into this tracked directory.
