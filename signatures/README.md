# CLA Signatures

This directory stores the contributor signatures collected by the CLA Assistant
workflow (`.github/workflows/cla.yml`).

- `.github/workflows/cla.yml` reads `version1/cla.json` on `main` when it checks
  pull requests. `.github/workflows/cla-discussion.yml` mirrors the same file to
  the `cla-signatures` branch as an audit trail for **CLA.md / CLA.zh-CN.md
  (version 1)**.

- `.github/workflows/cla-discussion.yml` records fixed statements posted to
  [Discussion #67](https://github.com/Tinadec/TinadecOffice/discussions/67).
  `.github/workflows/cla.yml` still accepts the legacy per-PR signature comment.

Do **not** hand-edit `cla.json`; the workflows manage it. Keep both `main` and
the `cla-signatures` branch tracked in Git so signatures are durable and
auditable.
