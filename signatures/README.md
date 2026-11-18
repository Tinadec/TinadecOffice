# CLA Signatures

This directory stores the contributor signatures collected by the CLA Assistant
workflow (`.github/workflows/cla.yml`).

- The workflows read and write `version1/cla.json` on the dedicated
  `cla-signatures` branch. The `main` copy is a bootstrap seed for
  **CLA.md / CLA.zh-CN.md (version 1)**.

- `.github/workflows/cla-discussion.yml` records fixed statements posted to
  [Discussion #67](https://github.com/Tinadec/TinadecOffice/discussions/67).
  `.github/workflows/cla.yml` still accepts the legacy per-PR signature comment.

Do **not** hand-edit `cla.json`; the workflows manage it. Keep the
`cla-signatures` branch and this directory tracked in Git so signatures are
durable and auditable.
