#!/usr/bin/env node
/**
 * Before `bun run dev`: are the packages in node_modules the ones bun.lock names?
 *
 * A pull that adds or bumps a dependency leaves node_modules behind until someone runs
 * `bun install`. Vite then stops at the first import of the new package with "Failed to resolve
 * import … Does the file exist?" — monaco-editor arrived this way — which reads like a code bug.
 * This compares the direct dependencies of the root and of every workspace with what is installed
 * and, when one is missing or at another version, says which and how to fix it before anything
 * starts. Read-only: nothing is installed or changed. Set TINADEC_DEV_DEPS_CHECK=off to skip it.
 */
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

// bun.lock is JSONC (trailing commas allowed); JSON.parse rejects it.
const readJson = (file) => {
  const text = readFileSync(file, 'utf8')
  try { return JSON.parse(text) } catch {
    // bun.lock allows trailing commas; strip only those (not '//', which appears in hashes).
    const cleaned = text.replace(/,(?=\s*[\]\}])/g, '')
    return JSON.parse(cleaned)
  }
}

function electronBinaryInstalled(packageDir, lockedVersion) {
  if (process.env.ELECTRON_OVERRIDE_DIST_PATH) {
    return existsSync(path.join(process.env.ELECTRON_OVERRIDE_DIST_PATH, process.platform === 'win32' ? 'electron.exe' : 'electron'))
  }
  const versionFile = path.join(packageDir, 'dist', 'version')
  if (!existsSync(versionFile)) return false
  const binaryVersion = readFileSync(versionFile, 'utf8').trim().replace(/^v/, '')
  if (lockedVersion && binaryVersion !== lockedVersion) return false
  const pathFile = path.join(packageDir, 'path.txt')
  if (!existsSync(pathFile)) return false
  const executableRel = readFileSync(pathFile, 'utf8').trim()
  if (!executableRel) return false
  return existsSync(path.join(packageDir, 'dist', executableRel))
}

/** `[{ name, expected, installed }]` for every direct dependency that does not match the lockfile. */
export function staleDependencies(root) {
  const lock = readJson(path.join(root, 'bun.lock'))
  const workspaces = lock.workspaces ?? {}
  const packages = lock.packages ?? {}
  const problems = []
  const seen = new Set()

  // '' is the root; the other keys are the workspace paths (apps/desktop, TinadecGateway, …).
  for (const [workspace, entry] of Object.entries(workspaces)) {
    for (const name of Object.keys({ ...entry.dependencies, ...entry.devDependencies, ...entry.optionalDependencies })) {
      // bun hoists to the root node_modules unless a version conflict forces nesting.
      const location = [path.posix.join(workspace, 'node_modules', name), `node_modules/${name}`].find((key) => packages[key])
      if (!location || seen.has(location)) continue
      seen.add(location)
      const locked = packages[location]
      // bun.lock 'packages' entries are arrays: [name@version, resolution-path, meta, integrity].
      // meta.optional flags platform binaries; link entries resolve to a folder, not a version.
      const meta = Array.isArray(locked) ? locked[2] ?? {} : locked
      if (meta.optional) continue
      const link = Array.isArray(locked) ? locked[1] : locked.link
      if (link) {
        if (!existsSync(path.join(root, link))) problems.push({ name, expected: link, installed: null })
        continue
      }
      const lockedVersion = Array.isArray(locked) ? locked[0].split('@').pop() : locked.version
      const packageDir = path.join(root, location)
      const manifest = path.join(packageDir, 'package.json')
      const installed = existsSync(manifest) ? readJson(manifest).version : null
      if (installed !== lockedVersion) {
        problems.push({ name, expected: lockedVersion, installed })
      } else if (name === 'electron' && !electronBinaryInstalled(packageDir, lockedVersion)) {
        problems.push({ name, expected: lockedVersion, installed: `${installed} (missing binary)` })
      }
    }
  }
  return problems
}

export function describe(problems) {
  const hasMissingElectronBinary = problems.some(({ installed }) => String(installed).includes('missing binary'))
  const lines = problems.map(({ name, expected, installed }) =>
    `  - ${name}: ${installed ? `installed ${installed}, lockfile ${expected}` : `missing (lockfile ${expected})`}`)
  return [
    '[check-dependencies] node_modules does not match bun.lock / 依赖与 bun.lock 不一致:',
    ...lines,
    ...(hasMissingElectronBinary
      ? ['Electron postinstall binary is missing. Run `bun install` or `node node_modules/electron/install.js` in the repository root. / Electron 可执行文件缺失，请在仓库根目录运行 bun install 或 node node_modules/electron/install.js。']
      : ['Run `bun install` in the repository root, then start again. / 请在仓库根目录运行 bun install 后再启动。']),
  ].join('\n')
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url) && process.env.TINADEC_DEV_DEPS_CHECK !== 'off') {
  const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
  const problems = staleDependencies(root)
  if (problems.length) {
    console.error(describe(problems))
    process.exit(1)
  }
}
