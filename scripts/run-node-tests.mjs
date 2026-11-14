#!/usr/bin/env node
/**
 * Runs `node --test` over a fixed set of locations, expanding each directory to the
 * `*.test.js|.cjs|.mjs` files inside it.
 *
 * Why not `node --test <dir>/*.test.*`: that glob only expands where the shell does it
 * (bash/zsh) or where Node's own test-runner glob support exists (Node >= 21). Windows
 * cmd.exe expands nothing, so the pattern reaches Node literally and an older Node then
 * hunts for a file whose name contains a `*`. Expanding here is shell- and
 * Node-version-independent, and — unlike a hand-written file list — a newly added test
 * file is picked up without anyone remembering to edit a script.
 *
 * Usage: node scripts/run-node-tests.mjs <directory-or-file>...
 */
import { spawnSync } from "node:child_process";
import { readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const TEST_FILE = /\.test\.(?:c|m)?js$/;

/// Directories become the test files directly inside them; explicit files pass through.
export function resolveTargets(entries, { isDirectory = (path) => statSync(path).isDirectory(), read = (path) => readdirSync(path) } = {}) {
  const files = [];
  for (const entry of entries) {
    if (isDirectory(entry)) {
      for (const name of read(entry).sort()) if (TEST_FILE.test(name)) files.push(join(entry, name));
    } else {
      files.push(entry);
    }
  }
  return files;
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const entries = process.argv.slice(2);
  if (entries.length === 0) {
    console.error("usage: node scripts/run-node-tests.mjs <directory-or-file>...");
    process.exit(1);
  }

  const files = resolveTargets(entries);
  if (files.length === 0) {
    console.error(`[run-node-tests] no test files found in: ${entries.join(", ")}`);
    process.exit(1);
  }

  const result = spawnSync(process.execPath, ["--test", ...files], { stdio: "inherit" });
  process.exit(result.status ?? 1);
}
