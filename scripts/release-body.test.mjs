import assert from 'node:assert/strict'
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import path from 'node:path'
import { test } from 'node:test'
import { fileURLToPath } from 'node:url'
import { composeReleaseBody, fillTemplate, parseArgs } from './release-body.mjs'

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

function createFixtureRoot(notesByTag = {}) {
  const root = mkdtempSync(path.join(tmpdir(), 'tinadec-release-body-test-'))
  mkdirSync(path.join(root, '.github'), { recursive: true })
  copyFileSync(
    path.join(repoRoot, '.github', 'release-header.md'),
    path.join(root, '.github', 'release-header.md'),
  )
  copyFileSync(
    path.join(repoRoot, '.github', 'release-footer.md'),
    path.join(root, '.github', 'release-footer.md'),
  )
  if (Object.keys(notesByTag).length > 0) {
    mkdirSync(path.join(root, 'docs', 'releases'), { recursive: true })
    for (const [tag, content] of Object.entries(notesByTag)) {
      writeFileSync(path.join(root, 'docs', 'releases', `${tag}.md`), content, 'utf8')
    }
  }
  return root
}

test('fillTemplate substitutes repository and strips leading v by default', () => {
  const rendered = fillTemplate('https://github.com/__GH_REPO__/releases/TinadecOffice-__VERSION__-linux-x64.deb', {
    repo: 'Tinadec/TinadecOffice',
    tag: 'v0.5.0',
  })
  assert.equal(rendered, 'https://github.com/Tinadec/TinadecOffice/releases/TinadecOffice-0.5.0-linux-x64.deb')
})

test('fillTemplate prefers explicit displayVersion over tag', () => {
  const rendered = fillTemplate('TinadecOffice-__VERSION__-win-x64-setup.exe', {
    repo: 'Tinadec/TinadecOffice',
    tag: 'nightly-723c7d5',
    displayVersion: 'nightly-723c7d5',
  })
  assert.equal(rendered, 'TinadecOffice-nightly-723c7d5-win-x64-setup.exe')
})

test('composeReleaseBody renders Stable mode with hand-written notes and folded commit log', () => {
  const root = createFixtureRoot({
    'v0.5.0': '## Highlights\n\n- Improved workflow orchestration.',
  })
  try {
    const result = composeReleaseBody({
      root,
      tag: 'v0.5.0',
      repo: 'Tinadec/TinadecOffice',
      autoNotes: "## What's Changed\n* feat: add modular CI (#52)\n",
    })

    assert.equal(result.hasNotes, true)
    assert.ok(result.body.includes('https://raw.githubusercontent.com/Tinadec/TinadecOffice/main/docs/images/poster.webp'))
    assert.ok(result.body.includes('## Highlights\n\n- Improved workflow orchestration.'))
    assert.ok(!result.body.includes('<!-- release-notes:'))
    assert.ok(result.body.includes('<details>\n<summary><b>Full commit log</b>'))
    assert.ok(result.body.includes("### What's Changed\n* feat: add modular CI (#52)"))
    assert.doesNotMatch(result.body, /^## What's Changed$/m)
    assert.ok(result.body.includes('`TinadecOffice-0.5.0-win-x64-setup.exe`'))
    assert.ok(result.body.includes('`TinadecOffice-0.5.0-linux-x64.deb`'))
    assert.ok(result.body.includes('`TinadecOffice-0.5.0-osx-arm64.dmg`'))
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('composeReleaseBody marks missing Stable notes with HTML comment and hasNotes: false', () => {
  const root = createFixtureRoot()
  try {
    const result = composeReleaseBody({
      root,
      tag: 'v0.9.9',
      repo: 'Tinadec/TinadecOffice',
      autoNotes: '',
    })

    assert.equal(result.hasNotes, false)
    assert.ok(result.body.includes('<!-- release-notes: docs/releases/v0.9.9.md is missing -->'))
    assert.ok(!result.body.includes('<summary><b>Full commit log</b>'))
    assert.ok(result.body.includes('`TinadecOffice-0.9.9-win-x64-setup.exe`'))
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('composeReleaseBody renders Nightly mode with commit provenance, exact asset names, and folded commit log', () => {
  const commit = '723c7d5c28fec0fbe41ebdeed56b29b6edcfbe39'
  const root = createFixtureRoot({
    // Even if a file with this name existed, Nightly mode must not read docs/releases/<tag>.md.
    'nightly-723c7d5': 'SHOULD NOT BE INCLUDED',
  })
  try {
    const result = composeReleaseBody({
      root,
      tag: 'nightly-723c7d5',
      repo: 'Tinadec/TinadecOffice',
      autoNotes: "## What's Changed\n* ci: split workflows (#52)\n",
      nightly: true,
      commit,
      displayVersion: 'nightly-723c7d5',
    })

    assert.equal(result.hasNotes, true)
    assert.equal(result.isNightly, true)
    assert.ok(!result.body.includes('<!-- release-notes:'))
    assert.ok(!result.body.includes('SHOULD NOT BE INCLUDED'))
    assert.ok(result.body.includes(`[\`723c7d5\`](https://github.com/Tinadec/TinadecOffice/commit/${commit})`))
    assert.ok(result.body.includes('`main`'))
    assert.ok(result.body.includes('<details>\n<summary><b>Full commit log</b>'))
    assert.ok(result.body.includes("### What's Changed\n* ci: split workflows (#52)"))
    assert.ok(result.body.includes('`TinadecOffice-nightly-723c7d5-win-x64-setup.exe`'))
    assert.ok(result.body.includes('`TinadecOffice-nightly-723c7d5-win-x64-portable.exe`'))
    assert.ok(result.body.includes('`TinadecOffice-nightly-723c7d5-linux-x64.deb`'))
    assert.ok(result.body.includes('`TinadecOffice-nightly-723c7d5-osx-arm64.dmg`'))
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('composeReleaseBody Nightly mode falls back to short tag sha when commit is omitted', () => {
  const root = createFixtureRoot()
  try {
    const result = composeReleaseBody({
      root,
      tag: 'nightly-723c7d5',
      repo: 'Tinadec/TinadecOffice',
      autoNotes: '',
      nightly: true,
      displayVersion: 'nightly-723c7d5',
    })

    assert.equal(result.hasNotes, true)
    assert.equal(result.isNightly, true)
    assert.ok(result.body.includes('`723c7d5`'))
    assert.ok(!result.body.includes('/commit/'))
  } finally {
    rmSync(root, { recursive: true, force: true })
  }
})

test('parseArgs parses Stable and Nightly CLI options cleanly', () => {
  assert.deepEqual(
    parseArgs(['v0.5.0', '--out', 'body.md', '--auto-notes', 'notes.md', '--repo', 'Tinadec/TinadecOffice', '--update']),
    {
      tag: 'v0.5.0',
      out: 'body.md',
      autoNotes: 'notes.md',
      repo: 'Tinadec/TinadecOffice',
      update: true,
      nightly: false,
      commit: null,
      displayVersion: null,
      previousTag: null,
    },
  )

  assert.deepEqual(
    parseArgs([
      'nightly-723c7d5',
      '--nightly',
      '--commit',
      '723c7d5c28fec0fbe41ebdeed56b29b6edcfbe39',
      '--display-version',
      'nightly-723c7d5',
      '--previous-tag',
      'nightly-1111111',
      '--out',
      'nightly.md',
    ]),
    {
      tag: 'nightly-723c7d5',
      out: 'nightly.md',
      autoNotes: null,
      repo: null,
      update: false,
      nightly: true,
      commit: '723c7d5c28fec0fbe41ebdeed56b29b6edcfbe39',
      displayVersion: 'nightly-723c7d5',
      previousTag: 'nightly-1111111',
    },
  )

  assert.throws(() => parseArgs([]), /usage:/)
  assert.throws(() => parseArgs(['v0.5.0', '--unknown']), /unexpected argument/)
  assert.throws(() => parseArgs(['v0.5.0', '--commit']), /missing value for --commit/)
})
