const test = require('node:test');
const assert = require('node:assert/strict');
const { createHostConnection } = require('./hostConnection.cjs');
const flush = () => new Promise(resolve => setImmediate(resolve));
function fixture(verify, options = {}) {
  const timers = new Map(); const events = []; let id = 0;
  const host = createHostConnection({ verify, retryDelaysMs: [10, 20], watchIntervalMs: 50,
    now: () => '2026-10-10T00:00:00Z', onStatusChange: value => events.push(value),
    setTimer: (fn, delay) => { const key = ++id; timers.set(key, { fn, delay }); return key; },
    clearTimer: key => timers.delete(key), ...options });
  async function next() {
    const entry = [...timers.entries()].sort((a, b) => a[1].delay - b[1].delay)[0];
    assert.ok(entry, 'a retry/watch timer exists'); timers.delete(entry[0]); entry[1].fn(); await flush();
  }
  return { host, events, timers, next };
}
test('first startup failure retries and restores business authorization', async () => {
  let calls = 0;
  const f = fixture(async () => { if (++calls === 1) throw new Error('fetch failed'); return true; });
  assert.equal((await f.host.start()).state, 'unavailable');
  assert.equal(f.host.isReady(), false);
  await f.next();
  assert.equal(f.host.snapshot().state, 'ready'); assert.equal(f.host.isReady(), true);
  assert.equal(calls, 2); f.host.stop();
});
test('periodic verification revokes on failure and can authenticate again', async () => {
  let calls = 0;
  const f = fixture(async () => { if (++calls === 2) throw new Error('timeout'); return true; });
  await f.host.start(); await f.next();
  assert.equal(f.host.snapshot().state, 'unavailable'); assert.equal(f.host.isReady(), false);
  await f.next(); assert.equal(f.host.snapshot().state, 'ready'); f.host.stop();
});
test('concurrent manual retries share one verification', async () => {
  let release; let calls = 0;
  const f = fixture(() => { calls++; return new Promise(resolve => { release = resolve; }); });
  const first = f.host.start(); const second = f.host.retry();
  assert.equal(first, second); await flush(); assert.equal(calls, 1);
  release(true); assert.equal((await first).state, 'ready'); f.host.stop();
});
test('automatic retry budget is finite and explicit retry starts a new budget', async () => {
  let calls = 0;
  const f = fixture(async () => { calls++; throw new Error('network failure'); });
  await f.host.start(); await f.next(); await f.next();
  assert.equal(calls, 3); assert.equal(f.timers.size, 0);
  await f.host.retry(); assert.equal(calls, 4); assert.equal(f.timers.size, 1); f.host.stop();
});
test('identity mismatch is blocked without automatic retry or leaking error contents', async () => {
  const secret = 'private-host-value';
  const f = fixture(async () => { throw Object.assign(new Error(secret), { code: 'host_identity_rejected' }); });
  const result = await f.host.start();
  assert.equal(result.state, 'rejected'); assert.equal(f.timers.size, 0);
  assert.equal(JSON.stringify(result).includes(secret), false);
  assert.equal(f.host.isReady(), false); f.host.stop();
});
test('a bootstrap configuration failure names the document instead of "temporarily unavailable"', async () => {
  const startupFailure = { code: 'configuration_missing', document: 'storage.toml', message: 'ignored by the host snapshot' };
  const f = fixture(async () => { throw Object.assign(new Error('service_start_failed'), { startupFailure }); });
  const status = await f.host.start();
  assert.equal(status.state, 'unavailable');
  assert.equal(status.error.code, 'host_startup_configuration_invalid');
  assert.equal(status.error.document, 'storage.toml');
  assert.equal(status.error.reason, 'configuration_missing');
  // The child's own message never becomes user-visible text; the window words it from the code.
  assert.doesNotMatch(status.error.message, /ignored by the host snapshot/);
  f.host.stop();
});

test('closing host aborts pending verification and ignores its late success', async () => {
  let release; let signal;
  const f = fixture(options => { signal = options.signal; return new Promise(resolve => { release = resolve; }); });
  const first = f.host.start(); await flush(); f.host.stop(); release(true);
  await first; assert.equal(signal.aborted, true);
  assert.equal(f.host.isReady(), false); assert.equal(f.host.snapshot().error.code, 'host_connection_stopped');
  assert.equal(f.events.some(x => x.state === 'ready'), false); assert.equal(f.timers.size, 0);
});
test('verification timeout is recoverable even when a dependency never settles', async () => {
  const f = fixture(async () => new Promise(() => {}), { verificationTimeoutMs: 5 });
  const attempt = f.host.start(); await f.next();
  assert.equal((await attempt).state, 'unavailable'); assert.equal(f.host.isReady(), false); f.host.stop();
});
test('a false proof cannot authorize the host', async () => {
  const f = fixture(async () => false); assert.equal((await f.host.start()).state, 'rejected'); f.host.stop();
});
