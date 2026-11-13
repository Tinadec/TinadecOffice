# CLA Signatures

This directory stores the contributor signatures collected by the CLA Assistant
workflow (`.github/workflows/cla.yml`).

- `.github/workflows/cla-discussion.yml` writes `version1/cla.json` to both the
  `cla-signatures` audit branch and `main`. Keeping the same content on `main`
  also lets older, re-run CLA workflow attempts read it for
  **CLA.md / CLA.zh-CN.md (version 1)**.

- `.github/workflows/cla-discussion.yml` records fixed statements posted to
  [Discussion #67](https://github.com/Tinadec/TinadecOffice/discussions/67).
  `.github/workflows/cla.yml` still accepts the legacy per-PR signature comment.

Do **not** hand-edit `cla.json`; the workflows manage it. Keep the
`cla-signatures` branch and this directory tracked in Git so signatures are
durable and auditable.
