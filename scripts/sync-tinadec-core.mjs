#!/usr/bin/env node
/* Sync the standalone TinadecCore mirror repository (default: ../TinadecCore)
   from TinadecOffice's nested TinadecCore/ tree. Copy-only (no deletions):
   runtime artifacts in the mirror (Api/data/*) stay untouched. Applies the
   license transform (GPL-3.0-or-later -> MIT in .csproj files, idempotent —
   the nested tree is already MIT) and rewrites SYNC.md provenance.
   --commit stages exactly the synced top-level paths (never `git add -A`,
   the mirror carries untracked Api/data runtime artifacts) and commits. */

import { execFileSync, execSync } from 'node:child_process'
import { existsSync, mkdirSync, readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const officeRoot = path.resolve(here, '..')
const source = path.join(officeRoot, 'TinadecCore')
const target = process.env.TINADEC_CORE_TARGET
  ? path.resolve(process.env.TINADEC_CORE_TARGET)
  : path.resolve(officeRoot, '../TinadecCore')
const dryRun = process.argv.includes('--dry-run')
const doCommit = process.argv.includes('--commit')

const SKIP_DIRS = new Set(['bin', 'obj', 'data', 'artifacts', '.vs', 'TestResults', '.idea'])
const SKIP_FILES = new Set(['SYNC.md'])

function currentSourceCommit() {
  try {
    return execSync('git rev-parse HEAD', { cwd: officeRoot, encoding: 'utf8' }).trim()
  } catch {
    return 'unknown'
  }
}

const copiedTopLevel = new Set()
const copiedPaths = []

function transformContent(rel, content) {
  if (rel.endsWith('.csproj')) {
    return content.replace(/GPL-3\.0-or-later/g, 'MIT')
  }
  return content
}

function copyRecursive(from, to, top) {
  for (const entry of readdirSync(from)) {
    const srcEntry = path.join(from, entry)
    const dstEntry = path.join(to, entry)
    const relTop = top ?? entry
    const st = statSync(srcEntry)
    if (st.isDirectory()) {
      if (SKIP_DIRS.has(entry)) continue
      if (!dryRun && !existsSync(dstEntry)) mkdirSync(dstEntry, { recursive: true })
      copyRecursive(srcEntry, dstEntry, relTop)
    } else {
      if (SKIP_FILES.has(entry) && top === null) continue
      const relFile = path.relative(source, srcEntry).split(path.sep).join('/')
      if (!dryRun) writeFileSync(dstEntry, transformContent(relTop, readFileSync(srcEntry)))
      copiedPaths.push(relFile)
      copiedTopLevel.add(relTop)
    }
  }
}

function writeSyncMetadata(commit) {
  const shortSha = commit.slice(0, 7)
  const body = [
    '# SYNC PROVENANCE',
    'source_repo: TinadecOffice (github.com/Tinadec/TinadecOffice)',
    `source_commit: ${shortSha}`,
    `synced_at: ${new Date().toISOString()}`,
    'product: core',
    'license_transform: PackageLicenseExpression GPL-3.0-or-later -> MIT (.csproj only)',
    '',
  ].join('\n')
  const syncPath = path.join(target, 'SYNC.md')
  // Idempotent: re-syncing the same source commit must not produce a
  // timestamp-only commit in the mirror.
  if (!dryRun && existsSync(syncPath)
    && readFileSync(syncPath, 'utf8').includes(`source_commit: ${shortSha}`)) return
  if (!dryRun) writeFileSync(syncPath, body)
}

function commitMirror(commit) {
  const gitOpts = { cwd: target, encoding: 'utf8' }
  // Stage the exact file paths the sync wrote — never a top-level directory,
  // which would sweep previously-untracked runtime artifacts (Api/data/*).
  execFileSync('git', ['add', '--', ...copiedPaths, 'SYNC.md'], gitOpts)
  const staged = execSync('git diff --cached --name-only', gitOpts).trim()
  if (!staged) {
    console.log('[sync-tinadec-core] nothing staged; mirror already up to date')
    return null
  }
  const message = `chore(sync): sync from TinadecOffice@${commit.slice(0, 7)}`
  execFileSync('git', ['commit', '-m', message], gitOpts)
  return message
}

function run() {
  if (!existsSync(source)) {
    console.error(`[sync-tinadec-core] source not found: ${source}`)
    process.exit(1)
  }
  if (!existsSync(target)) {
    console.error(`[sync-tinadec-core] target mirror not found: ${target}`)
    process.exit(1)
  }
  if (!dryRun) mkdirSync(target, { recursive: true })

  const commit = currentSourceCommit()
  copyRecursive(source, target, null)
  writeSyncMetadata(commit)

  console.log(`[sync-tinadec-core] ${dryRun ? 'DRY RUN — would copy' : 'copied'} ${copiedPaths.length} files from TinadecCore@${commit.slice(0, 7)} → ${target}`)
  console.log(`[sync-tinadec-core] top-level entries: ${[...copiedTopLevel].sort().join(', ')}`)

  if (doCommit) {
    if (dryRun) {
      console.log('[sync-tinadec-core] --commit ignored under --dry-run')
    } else {
      const message = commitMirror(commit)
      if (message) console.log(`[sync-tinadec-core] committed: ${message}`)
    }
  }
}

run()
