# Contributing to TinadecOffice

Thanks for your interest in contributing to **TinadecOffice**!

Please read the [Contributor License Agreement (CLA)](CLA.md)
([中文版](CLA.zh-CN.md)) before opening a pull request — **every pull request
that introduces a new contribution is blocked until its authors sign the CLA.**

## Table of contents

- [Code of conduct](#code-of-conduct)
- [License & ownership](#license--ownership)
- [Contributor License Agreement (CLA)](#contributor-license-agreement-cla)
- [How to contribute](#how-to-contribute)
  - [Bugs and feature requests](#bugs-and-feature-requests)
  - [Pull requests](#pull-requests)
  - [Signing the CLA](#signing-the-cla)
- [Development workflow](#development-workflow)
  - [Prerequisites](#prerequisites)
  - [Workspace setup](#workspace-setup)
  - [Build & test](#build--test)
- [Project conventions](#project-conventions)
- [Legal](#legal)

## Code of conduct

Be respectful and constructive. Harassment and personal attacks are not
tolerated in any project space.

## License & ownership

TinadecOffice is a **multi-license repository**. See the
[README](README.md#license) and [`NOTICE`](NOTICE) for the exact scope of each
license:

| Scope | License |
|-------|---------|
| TinadecOffice overall (apps, docs, scripts) | `GPL-3.0-or-later` |
| `TinadecCore` | `MIT` |
| `TinadecGateway` | `AGPL-3.0-or-later` |
| `TinadecTools` (+ Generators) | `AGPL-3.0-or-later` |

**Important:** contributing to this project is *not* a transfer of ownership.
Your contribution remains yours; by signing the CLA you grant the project owner
the perpetual, irrevocable, worldwide right to use, modify, relicense,
sublicense and distribute your contribution under any license the project owner
chooses — including commercial or closed-source terms, and including a
transition of the project (or any part of it) between open-source and
closed-source licensing. In exchange you keep the rights described in the
CLA itself.

## Contributor License Agreement (CLA)

Every contributor must sign the CLA **before their pull request can be
merged**. The CLA gives the project owner:

- the right to **accept, modify, rework and remove** contributions;
- the right to **relicense, sublicense or dual-license** contributions,
  including moving the project between open-source and closed-source;
- **copyright and patent licenses** from each contributor, on a
  perpetual, worldwide, non-exclusive, royalty-free, irrevocable basis;
- confirmation that contributions are **original** and that the contributor
  has the authority to submit them.

Read the full text: [CLA.md](CLA.md) · [CLA.zh-CN.md](CLA.zh-CN.md).

If you are contributing on behalf of an employer, make sure you have the
authority to do so.

## How to contribute

### Bugs and feature requests

Open an [issue](https://github.com/Tinadec/TinadecOffice/issues) with a clear
title and description. Include steps to reproduce, expected behavior, and
observed behavior. For bugs, include the version you are using.

### Pull requests

1. **Fork** the repository and create a branch with a descriptive name
   (`feat/…`, `fix/…`, `docs/…`).
2. **Keep pull requests small and focused.** One logical change per PR.
3. **Match the project's conventions** (see [Project conventions](#project-conventions)).
4. **Run the relevant checks locally** before pushing (see
   [Build & test](#build--test)).
5. **Sign the CLA** by commenting on your pull request:

   > I have read the CLA Document and I hereby sign the CLA

   The [CLA Assistant workflow](.github/workflows/cla.yml) records your
   signature automatically. Skipping this means a PR will fail the required check.

### Signing the CLA

The CLA Assistant bot will post a comment on your pull request asking you to
sign. Reply with the exact sentence:

> I have read the CLA Document and I hereby sign the CLA

Your GitHub username, the date and the pull request number are recorded in
`signatures/version1/cla.json` on the `cla-signatures` branch as evidence.
After you sign, the bot updates the `CLAAssistant` check; that check is required
on `main` before a pull request can be merged.

If you need to re-check after signing, comment `recheck`.

## Development workflow

### Prerequisites

- Node.js ≥ 24 and npm (pnpm is not required)
- Bun ≥ 1.3.x (for `TinadecGateway`)
- .NET SDK 10.x
- ripgrep is staged automatically by `scripts/setup-ripgrep.mjs`

### Workspace setup

```bash
npm ci                # installs workspaces and runs postinstall
npm run ai:workspace:setup   # optional: link native plan/wiki/skill dirs
```

### Build & test

```bash
npm run build         # builds workspaces, gateway and the .NET solution
npm test              # runs workspace tests, gateway tests and .NET tests
npm run check:drift   # regenerates the desktop OpenAPI client and fails on diff
```

A pull request that changes `TinadecCore/`, `TinadecTools/`,
`TinadecGateway/src/` or the generated client should pass the relevant CI
workflows (POSIX core, contract drift, CLA) on the `main` branch.

## Project conventions

- Follow the per-project `AGENTS.md` guidance in each area (`apps/desktop`,
  `apps/TinadecUI`, `TinadecCore`, `TinadecGateway`, …).
- Keep API contracts stable: regenerate the OpenAPI snapshots and
  `schema.d.ts` when you change routes or DTOs (`npm run check:drift`).
- Do not introduce new dependencies without discussing them in the PR.

## Legal

By submitting a contribution you agree to be bound by the
[Contributor License Agreement](CLA.md). If you have questions, open an issue
titled "CLA" in the repository.
