# Shared development output policy

Use `.tinadec_dev` as the repository's shared, Git-versioned home for useful agent-generated development artifacts. The spelling is **`.tinadec_dev`**, never `.tindec_dev`.

- New plans and implementation notes: `.tinadec_dev/plans/`.
- Research and comparisons: `.tinadec_dev/research/`.
- Reviews, investigations, verification reports and durable evidence: `.tinadec_dev/reports/` or `.tinadec_dev/evidence/`.
- Specs and standalone task checklists: `.tinadec_dev/specs/`.
- **Exception — Denv / writing / collaboration module:** these product and architecture discussion docs are not stored in this repository. They live in the private `Tinadec/withdocs` repository under `tinadec-denv/` (Tinadec organization members only). Read `tinadec-denv/README.md` first, revise the existing numbered document instead of starting a parallel one, and never create a local or public duplicate. See the 2026-11-11 entry in the root `AGENTS.md`.
- Project knowledge and memory: `.tinadec_dev/memory/` or `.tinadec_dev/wiki/`.
- Shared skills and reusable tool rules: `.tinadec_dev/skills/` and `.tinadec_dev/tooling/`.

Use descriptive filenames, normally `YYYY-MM-DD-topic.md`, or a stable module/Task ID when updating an existing work item. Preserve the source tool and provenance when importing historical material; old completion marks are historical evidence, not current acceptance.

Update existing product documentation and the module's authoritative `STATUS.md`/`TODO.md` in place. The active module development program remains at `docs/development-program/`; link new analysis/evidence back to it rather than creating a second TODO database.

Do not create new project plans/reports in the repository root or in private tool folders. Native fixed output directories are linked locally to this shared tree by `npm run ai:workspace:setup`. Tool sessions, logs, caches, machine-specific settings and credentials stay in their ignored local locations or `tmp/`/`logs/`.

When changing skills or tool output routing, edit the tracked canonical files here and run setup again. `.claude`, `.agents`, `.qoder`, `.opencode`, `.workbuddy` and other tool folders are local adapters, not independent sources of project documentation.
