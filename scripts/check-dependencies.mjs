#!/usr/bin/env node
/**
 * Before `npm run dev`: are the packages in node_modules the ones package-lock.json names?
 *
 * A pull that adds or bumps a dependency leaves node_modules behind until someone runs
 * `npm install`. Vite then stops at the first import of the new package with "Failed to resolve
 * import … Does the file exist?" — monaco-editor arrived this way — which reads like a code bug.
 * This compares the direct dependencies of the root and of every workspace with what is installed
 * and, when one is missing or at another version, says which and how to fix it before anything
 * starts. Read-only: nothing is installed or changed. Set TINADEC_DEV_DEPS_CHECK=off to skip it.
 */
import { existsSync, readFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const readJson = (file) => JSON.parse(readFileSync(file, 'utf8'))

/** `[{ name, expected, installed }]` for every direct dependency that does not match the lockfile. */
export function staleDependencies(root) {
  const packages = readJson(path.join(root, 'package-lock.json')).packages ?? {}
  const problems = []
  const seen = new Set()
  // '' is the root; the other keys without node_modules are the workspaces (apps/desktop, …).
  for (const [workspace, entry] of Object.entries(packages)) {
    if (workspace.includes('node_modules')) continue
    for (const name of Object.keys({ ...entry.dependencies, ...entry.devDependencies })) {
      // npm places a dependency in the workspace's own node_modules or hoists it to the root.
      const location = [path.posix.join(workspace, 'node_modules', name), `node_modules/${name}`].find((key) => packages[key])
      if (!location || seen.has(location)) continue
      seen.add(location)
      const locked = packages[location]
      if (locked.optional) continue
      if (locked.link) {
        // A workspace package is a link to its folder; it is there or the checkout is broken.
        if (!existsSync(path.join(root, location))) problems.push({ name, expected: locked.resolved, installed: null })
        continue
      }
      const manifest = path.join(root, location, 'package.json')
      const installed = existsSync(manifest) ? readJson(manifest).version : null
      if (installed !== locked.version) problems.push({ name, expected: locked.version, installed })
    }
  }
  return problems
}

export function describe(problems) {
  const lines = problems.map(({ name, expected, installed }) =>
    `  - ${name}: ${installed ? `installed ${installed}, lockfile ${expected}` : `missing (lockfile ${expected})`}`)
  return [
    '[check-dependencies] node_modules does not match package-lock.json / 依赖与 package-lock.json 不一致:',
    ...lines,
    'Run `npm install` in the repository root, then start again. / 请在仓库根目录运行 npm install 后再启动。',
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
