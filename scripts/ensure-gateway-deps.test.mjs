import test from "node:test";
import assert from "node:assert/strict";
import { gatewayDependenciesCurrent, lockDigest } from "./ensure-gateway-deps.mjs";

function fakeFs(files) {
  return {
    exists: (path) => path in files,
    read: (path) => {
      if (!(path in files)) throw new Error(`missing ${path}`);
      return files[path];
    },
  };
}

const args = (fs) => ({ modulesDir: "nm", stampFile: "stamp", digest: "abc", fs });

test("a missing node_modules or stamp means the dependencies are not current", () => {
  assert.equal(gatewayDependenciesCurrent(args(fakeFs({}))), false);
  assert.equal(gatewayDependenciesCurrent(args(fakeFs({ nm: "" }))), false);
  assert.equal(gatewayDependenciesCurrent(args(fakeFs({ stamp: "abc" }))), false);
  assert.equal(gatewayDependenciesCurrent(args(fakeFs({ nm: "", stamp: "abc" }))), true);
});

test("a stamp left by another lockfile is a mismatch", () => {
  assert.equal(gatewayDependenciesCurrent(args(fakeFs({ nm: "", stamp: "old\n" }))), false);
});

test("an unreadable stamp is treated as a mismatch, never as trust", () => {
  const fs = { exists: () => true, read: () => { throw new Error("EACCES"); } };
  assert.equal(gatewayDependenciesCurrent(args(fs)), false);
});

test("the digest tracks the lockfile bytes exactly", () => {
  assert.equal(lockDigest("a"), lockDigest("a"));
  assert.notEqual(lockDigest("a"), lockDigest("b"));
  assert.match(lockDigest("a"), /^[a-f0-9]{64}$/);
});
