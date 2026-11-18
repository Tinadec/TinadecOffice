import test from "node:test";
import assert from "node:assert/strict";
import { join } from "node:path";
import { resolveTargets } from "./run-node-tests.mjs";

// Expectations go through `join` too: `resolveTargets` joins with the platform separator
// (a POSIX-written forward-slash fixture would pass here and fail on Windows).
const tree = {
  "electron": ["b.test.cjs", "a.test.cjs", "main.cjs", "trustedHostRequests.probe.cjs", "sub"],
  "scripts": ["viteCacheGuard.test.mjs", "dev.mjs", "README.md"],
};

const fs = {
  isDirectory: (path) => path in tree,
  read: (path) => tree[path],
};

test("a directory expands to its test files, sorted, and drops everything else", () => {
  assert.deepEqual(resolveTargets(["electron"], fs), [
    join("electron", "a.test.cjs"),
    join("electron", "b.test.cjs"),
  ]);
  assert.deepEqual(resolveTargets(["scripts"], fs), [join("scripts", "viteCacheGuard.test.mjs")]);
});

test("explicit files pass through in the order given, mixing with directories", () => {
  assert.deepEqual(resolveTargets(["scripts", "../../scripts/check-dependencies.test.mjs"], fs), [
    join("scripts", "viteCacheGuard.test.mjs"),
    "../../scripts/check-dependencies.test.mjs",
  ]);
});

test("the helper's own test file is discovered from its directory", () => {
  const only = { isDirectory: (path) => path === ".", read: () => ["run-node-tests.mjs", "run-node-tests.test.mjs", "dev.mjs"] };
  assert.deepEqual(resolveTargets(["."], only), [join(".", "run-node-tests.test.mjs")]);
});
