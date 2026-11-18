const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const test = require('node:test');
const { createLogSink } = require('./logSink.cjs');

/** Every file under a directory, as `dir/file` -> size, for budget readings across streams. */
function filesUnder(root) {
  const found = new Map();
  for (const item of fs.readdirSync(root, { withFileTypes: true })) {
    const file = path.join(root, item.name);
    if (item.isDirectory()) for (const [name, size] of filesUnder(file)) found.set(path.join(item.name, name), size);
    else if (item.isFile()) found.set(item.name, fs.statSync(file).size);
  }
  return found;
}
const totalUnder = root => [...filesUnder(root).values()].reduce((sum, size) => sum + size, 0);

test('host rotation shares the scope budget with Core and keeps active files', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-host-log-'));
  try {
    const logs = path.join(root, 'logs'); const host = path.join(logs, 'host'); const runtime = path.join(logs, 'runtime');
    fs.mkdirSync(runtime, { recursive: true });
    fs.writeFileSync(path.join(runtime, 'core.log'), 'active');
    const rotated = path.join(runtime, `core.log.639008400000000000-${'a'.repeat(32)}`);
    fs.writeFileSync(rotated, '01234567890123456789');
    const sink = createLogSink(host, { maxFileBytes: 10, maxTotalBytes: 40, budgetCheckBytes: 0 });
    sink.write('gateway', '0123456789012345678901234567890123456789');

    // The 40-byte write rotated the host stream instead of appending past maxFileBytes: the
    // canonical file kept the ceiling, so the rest went to rotations and/or the budget.
    const hostFiles = filesUnder(host);
    assert.ok(hostFiles.has('gateway.log'));

    // Canonical files are never the victim, on either stream.
    assert.ok(fs.existsSync(path.join(runtime, 'core.log')));
    assert.ok(fs.existsSync(path.join(host, 'gateway.log')));

    // The budget is shared across logs/, not held per directory: the whole tree fits and every
    // file the sink wrote is within maxFileBytes. Which rotated file is evicted to get there is
    // decided by mtimeMs, and filesystems resolve that coarsely enough for the pre-existing Core
    // file and the host's own rotations to tie — measured on the win-x64 CI leg, where the tie
    // sent the host files first and the 20-byte Core file survived at 36/40 bytes, while the
    // posix legs evicted the Core file first and left two host rotations behind. Both outcomes
    // respect the budget, so neither the victim nor the surviving rotations are pinned here; the
    // next test pins the victim by constructing a budget only the Core file can satisfy.
    const hostSizes = [...hostFiles.values()];
    assert.ok(hostSizes.every(size => size <= 10), `over maxFileBytes: ${JSON.stringify([...hostFiles])}`);
    assert.ok(totalUnder(logs) <= 40, `over maxTotalBytes: ${totalUnder(logs)}`);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
test('a host write evicts a rotated Core file when the shared budget leaves no other candidate', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-host-log-shared-'));
  try {
    const logs = path.join(root, 'logs'); const host = path.join(logs, 'host'); const runtime = path.join(logs, 'runtime');
    fs.mkdirSync(runtime, { recursive: true });
    fs.writeFileSync(path.join(runtime, 'core.log'), 'active');
    const rotated = path.join(runtime, `core.log.639008400000000000-${'b'.repeat(32)}`);
    fs.writeFileSync(rotated, '01234567890123456789');
    // 6 bytes of active Core log + 20 bytes of rotated Core log + this 10-byte host write is 36
    // against a 30-byte budget, so six bytes must go and the rotated Core file is the only rotated
    // candidate in the tree. Nothing here depends on eviction order.
    const sink = createLogSink(host, { maxFileBytes: 10, maxTotalBytes: 30, budgetCheckBytes: 0 });
    sink.write('gateway', '0123456789');
    assert.equal(fs.existsSync(rotated), false, 'the shared budget had to evict the rotated Core file');
    assert.ok(fs.existsSync(path.join(runtime, 'core.log')), 'the active Core file must survive');
    assert.ok(fs.existsSync(path.join(host, 'gateway.log')), 'the active host file must survive');
    assert.ok(totalUnder(logs) <= 30, `over maxTotalBytes: ${totalUnder(logs)}`);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
test('host logging reads canonical TOML capacity and refuses an invalid document', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-log-config-'));
  try {
    fs.mkdirSync(path.join(root, 'config'));
    const configuration = path.join(root, 'config', 'logging.toml');
    fs.writeFileSync(configuration, 'version = 1\n[logging]\nrotation_bytes = 4096\ntotal_bytes = 8192\n');
    const host = path.join(root, 'logs', 'host'); const sink = createLogSink(host, { budgetCheckBytes: 0 });
    sink.write('electron', Buffer.alloc(12 * 1024));
    const files = fs.readdirSync(host).map(file => fs.statSync(path.join(host, file)).size);
    assert.ok(files.every(size => size <= 4096)); assert.ok(files.reduce((a, b) => a + b, 0) <= 8192);
    fs.writeFileSync(configuration, '[logging]\nrotation_bytes = 4096\ntotal_bytes = 1\n');
    assert.throws(() => createLogSink(host), /Invalid logging configuration/);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('the total-budget tree walk is throttled by bytes, not run on every small write', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-log-throttle-'));
  try {
    const logs = path.join(root, 'logs'); const host = path.join(logs, 'host'); const runtime = path.join(logs, 'runtime');
    fs.mkdirSync(runtime, { recursive: true });
    fs.writeFileSync(path.join(runtime, 'core.log'), 'active');
    const rotated = path.join(runtime, `core.log.639008400000000000-${'c'.repeat(32)}`);
    fs.writeFileSync(rotated, '01234567890123456789');
    // 6 active + 20 rotated + this 10-byte write is 36 against a 30-byte budget, but the write
    // is far under the default 1 MiB check threshold and within 5 s of creation: the tree walk
    // must NOT run yet (running it per chunk is exactly the main-process block being fixed).
    const sink = createLogSink(host, { maxFileBytes: 10, maxTotalBytes: 30 });
    sink.write('gateway', '0123456789');
    assert.ok(fs.existsSync(rotated), 'a small write must not walk the whole log tree immediately');
    // An explicit check (or crossing the byte threshold) still enforces the budget.
    sink.enforceBudget();
    assert.equal(fs.existsSync(rotated), false, 'an explicit check evicts the rotated candidate');
    assert.ok(totalUnder(logs) <= 30, `over maxTotalBytes: ${totalUnder(logs)}`);

    // With a tiny byte threshold the write itself triggers the walk.
    const rotated2 = path.join(runtime, `core.log.639008400000000001-${'d'.repeat(32)}`);
    fs.writeFileSync(rotated2, '01234567890123456789');
    const eager = createLogSink(host, { maxFileBytes: 10, maxTotalBytes: 30, budgetCheckBytes: 1 });
    eager.write('gateway', '0123456789');
    assert.ok(totalUnder(logs) <= 30, `crossing the byte threshold must enforce: ${totalUnder(logs)}`);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});
