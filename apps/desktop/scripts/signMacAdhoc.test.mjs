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
    // Without the non-negative check a dropped Helper would still satisfy "-1 < outer index".
    const helperIndex = plan.indexOf(join(app, "Contents", "Frameworks", "TinadecOffice Helper.app"));
    assert.ok(helperIndex >= 0, "the helper app is part of the plan");
    assert.ok(helperIndex < plan.indexOf(app), "the helper app is signed before the bundle that seals it");
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

test("every planned target is signed with the ad-hoc arguments, outer bundle last", () => {
  const { root, app } = fixtureApp();
  try {
    const calls = [];
    const signed = signMacAdhoc(app, { run: (command, args) => calls.push([command, args]), platform: "darwin" });
    // The failing-path test above never reaches the callback, so this is the only assertion that
    // would notice signMacAdhoc passing wrong arguments or signing in a different order.
    assert.ok(calls.length > 0, "the fixture app has nested code to sign");
    assert.deepEqual(calls.map(([, args]) => args.at(-1)), signed, "callbacks see exactly the reported targets");
    assert.ok(calls.every(([command]) => command === "codesign"));
    assert.equal(signed.at(-1), app, "the outer bundle is the last target signed");
    for (const [, args] of calls) {
      assert.deepEqual(args, codesignArguments(args.at(-1)), "each target gets the seal-refresh arguments");
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("a staged runtime file without its bundled copy fails instead of being skipped", () => {
  const { root, app, staged } = fixtureApp();
  try {
    // A file missing from staging is a --dir build; a file missing from the bundle is a broken copy.
    rmSync(join(staged, RUNTIME_EXECUTABLES[0]));
    rmSync(join(app, "Contents", "Resources", "runtime", RUNTIME_EXECUTABLES[1]));
    assert.throws(() => assertBundledRuntimeMatches(app, staged), /Bundled runtime is missing/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
