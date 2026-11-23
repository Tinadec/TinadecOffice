import assert from "node:assert/strict";
import { mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import {
  assertBundledRuntimeMatches,
  codesignArguments,
  RUNTIME_EXECUTABLES,
  signMacAdhoc,
  signingPlan,
  signStagedRuntime,
} from "./sign-mac-adhoc.mjs";

function fixtureApp() {
  const root = join(tmpdir(), "tinadec-adhoc-" + process.pid + "-" + Math.random().toString(16).slice(2));
  const app = join(root, "TinadecOffice.app");
  const frameworks = join(app, "Contents", "Frameworks");
  const electron = join(frameworks, "Electron Framework.framework");
  const crashpad = join(electron, "Versions", "A", "Helpers", "chrome_crashpad_handler");
  mkdirSync(join(frameworks, "TinadecOffice Helper.app"), { recursive: true });
  mkdirSync(join(electron, "Versions", "A", "Helpers"), { recursive: true });
  // The fixture must create the helper, otherwise an ordering assertion compares -1 < n and passes
  // even when signingPlan drops it entirely.
  writeFileSync(crashpad, "");
  const runtime = join(app, "Contents", "Resources", "runtime");
  const staged = join(root, "runtime");
  for (const relative of RUNTIME_EXECUTABLES) {
    writeFileSyncWithParents(join(runtime, relative), "signed:" + relative);
    writeFileSyncWithParents(join(staged, relative), "signed:" + relative);
  }
  return { root, app, electron, crashpad, runtime, staged };
}

function writeFileSyncWithParents(path, content) {
  mkdirSync(join(path, ".."), { recursive: true });
  writeFileSync(path, content);
}

test("nested code is signed before the bundle that seals it", () => {
  const { root, app, electron, crashpad } = fixtureApp();
  try {
    const plan = signingPlan(app);
    assert.equal(plan.at(-1), app, "the outer bundle is always last");
    const crashpadIndex = plan.indexOf(crashpad);
    const frameworkIndex = plan.indexOf(electron);
    assert.ok(crashpadIndex >= 0, "the crashpad helper is part of the plan");
    assert.ok(crashpadIndex < frameworkIndex, "helpers are signed before their framework");
    assert.ok(plan.indexOf(join(app, "Contents", "Frameworks", "TinadecOffice Helper.app")) < plan.indexOf(app));
    // Runtime binaries are sealed at staging time, not here: re-signing the bundle copies would break
    // the byte-for-byte comparison against the Office channel archives.
    for (const relative of RUNTIME_EXECUTABLES) {
      assert.ok(!plan.includes(join(app, "Contents", "Resources", "runtime", relative)), relative);
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the staged runtime is signed in place and missing binaries are skipped", () => {
  const root = join(tmpdir(), "tinadec-adhoc-staged-" + process.pid + "-" + Math.random().toString(16).slice(2));
  const calls = [];
  try {
    for (const relative of RUNTIME_EXECUTABLES.slice(0, 2)) writeFileSyncWithParents(join(root, relative), "elf");
    const signed = signStagedRuntime(root, { run: (command, args) => calls.push([command, args]), platform: "darwin" });
    assert.equal(signed.length, 2, "only the two files that exist are signed");
    assert.ok(calls.every(([command]) => command === "codesign"));
    assert.deepEqual(calls[0][1], codesignArguments(join(root, RUNTIME_EXECUTABLES[0])));
    assert.deepEqual(signStagedRuntime(root, { run: () => { throw new Error("must not run"); }, platform: "win32" }), []);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("bundled runtime must stay byte-identical to the staged runtime", () => {
  const { root, app, staged } = fixtureApp();
  try {
    assert.equal(assertBundledRuntimeMatches(app, staged).length, RUNTIME_EXECUTABLES.length);
    writeFileSync(join(staged, RUNTIME_EXECUTABLES[0]), "resigned-later");
    assert.throws(() => assertBundledRuntimeMatches(app, staged), /differs from the staged runtime/);
    // No staged runtime (for example a --dir build without staging) is not an error.
    assert.deepEqual(assertBundledRuntimeMatches(app, join(root, "missing")), []);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("signing arguments are ad-hoc with an explicit seal refresh", () => {
  assert.deepEqual(codesignArguments("/tmp/x.app"), ["--force", "--sign", "-", "--timestamp=none", "/tmp/x.app"]);
  assert.throws(() => signMacAdhoc(join(tmpdir(), "missing-" + process.pid + ".app"), { run: () => {}, platform: "darwin" }), /existing .app/);
});
