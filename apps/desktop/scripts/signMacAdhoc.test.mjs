import assert from "node:assert/strict";
import { mkdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { codesignArguments, RUNTIME_EXECUTABLES, signMacAdhoc, signingPlan } from "./sign-mac-adhoc.mjs";

function fixtureApp() {
  const root = join(tmpdir(), "tinadec-adhoc-" + process.pid + "-" + Math.random().toString(16).slice(2));
  const app = join(root, "TinadecOffice.app");
  const frameworks = join(app, "Contents", "Frameworks");
  mkdirSync(join(frameworks, "TinadecOffice Helper.app"), { recursive: true });
  mkdirSync(join(frameworks, "Electron Framework.framework", "Versions", "A", "Helpers"), { recursive: true });
  const electron = join(frameworks, "Electron Framework.framework");
  mkdirSync(join(app, "Contents", "Resources", "runtime", "core"), { recursive: true });
  return { root, app, electron };
}

test("nested code is signed before the bundle that seals it", () => {
  const { root, app, electron } = fixtureApp();
  try {
    const plan = signingPlan(app);
    assert.equal(plan[plan.length - 1], app, "the outer bundle is always last");
    assert.ok(plan.indexOf(join(electron, "Versions", "A", "Helpers", "chrome_crashpad_handler")) < plan.indexOf(electron));
    assert.ok(plan.indexOf(join(app, "Contents", "Frameworks", "TinadecOffice Helper.app")) < plan.indexOf(app));
    // Every runtime binary the packaged app spawns carries its own signature and must be sealed too.
    for (const relative of RUNTIME_EXECUTABLES) assert.ok(plan.includes(join(app, relative)), relative);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("only existing targets are signed, and nothing is signed off macOS", () => {
  const { root, app } = fixtureApp();
  const calls = [];
  try {
    const signed = signMacAdhoc(app, { run: (command, args) => calls.push([command, args]), platform: "darwin" });
    assert.ok(signed.length > 0);
    assert.ok(calls.every(([command]) => command === "codesign"));
    assert.deepEqual(calls[0][1], codesignArguments(calls[0][1].at(-1)));
    assert.equal(signMacAdhoc(app, { run: () => { throw new Error("must not run"); }, platform: "win32" }).length, 0);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("signing arguments are ad-hoc with an explicit seal refresh", () => {
  assert.deepEqual(codesignArguments("/tmp/x.app"), ["--force", "--sign", "-", "--timestamp=none", "/tmp/x.app"]);
  assert.throws(() => signMacAdhoc(join(tmpdir(), "missing-" + process.pid + ".app"), { run: () => {}, platform: "darwin" }), /existing .app/);
});
