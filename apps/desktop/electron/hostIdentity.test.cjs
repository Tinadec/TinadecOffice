const assert = require('node:assert/strict');
const { createHmac } = require('node:crypto');
const test = require('node:test');
const { verifyHostIdentity, verifyManagedHost } = require('./hostIdentity.cjs');
const token = 'a'.repeat(43);
const nonce = 'b'.repeat(43);
const proof = (role, key = token, value = nonce) => createHmac('sha256', key).update(`tinadec-host-v1\0${role}\0${value}`).digest('hex');

test('endpoint verification sends only a nonce and checks its role-bound HMAC', async () => {
  const calls = [];
  const fetchImpl = async (url, init) => { calls.push({ url, init }); return Response.json({ role: 'core', nonce, proof: proof('core') }); };
  assert.equal(await verifyHostIdentity('http://127.0.0.1:48731', 'core', token, { fetchImpl, nonceFactory: () => nonce }), true);
  assert.equal(new URL(calls[0].url).searchParams.get('nonce'), nonce);
  assert.equal(new Headers(calls[0].init.headers).has('x-tinadec-host-control'), false);
  assert.equal(JSON.stringify(calls[0]).includes(token), false);
  assert.equal(calls[0].init.redirect, 'error');
});

test('public fingerprints cannot substitute for a matching nonce, role, and private-key proof', async () => {
  for (const body of [{ name: 'tinadec-core', status: 'ok' }, { role: 'gateway', nonce, proof: proof('gateway') },
    { role: 'core', nonce: 'c'.repeat(43), proof: proof('core') }, { role: 'core', nonce, proof: proof('core', 'foreign-key') },
    { role: 'core', nonce, proof: proof('core').toUpperCase() }, { role: 'core', nonce, proof: 'short' }])
    await assert.rejects(verifyHostIdentity('http://127.0.0.1:48731', 'core', token,
      { fetchImpl: async () => Response.json(body), nonceFactory: () => nonce }), /invalid trusted host identity proof/);
});

test('missing startup keys, invalid URLs, invalid nonce and unavailable endpoints fail before any private request', async () => {
  let sent = false;
  const fetchImpl = async () => { sent = true; return new Response(null, { status: 503 }); };
  for (const url of ['https://127.0.0.1:48731', 'http://127.0.0.1:48730', 'http://127.0.0.2:48731', 'http://127.0.0.1:48731/api'])
    await assert.rejects(verifyHostIdentity(url, 'core', token, { fetchImpl }), /managed local/);
  await assert.rejects(verifyHostIdentity('http://127.0.0.1:48731', 'core', undefined, { fetchImpl }), /startup credential/);
  await assert.rejects(verifyHostIdentity('http://127.0.0.1:48731', 'core', token, { fetchImpl, nonceFactory: () => 'short' }), /nonce/);
  assert.equal(sent, false);
  await assert.rejects(verifyHostIdentity('http://127.0.0.1:48731', 'core', token, { fetchImpl }), /cannot prove/);
});

test('both managed endpoints must prove their own role before the host becomes trusted', async () => {
  const fetchImpl = async url => {
    const request = new URL(url); const role = request.port === '48731' ? 'core' : 'gateway';
    const value = request.searchParams.get('nonce');
    return Response.json({ role, nonce: value, proof: proof(role, token, value) });
  };
  assert.equal(await verifyManagedHost(token, { fetchImpl }), true);
  await assert.rejects(verifyManagedHost(token, { fetchImpl: async url => {
    if (new URL(url).port === '48730') return new Response(null, { status: 503 });
    return fetchImpl(url);
  } }), /gateway cannot prove/);
});

test('an isolated host accepts only its declared Core/Gateway ports', async () => {
  const fetchImpl = async url => {
    const request = new URL(url);
    const role = request.port === '48741' ? 'core' : 'gateway';
    const value = request.searchParams.get('nonce');
    return Response.json({ role, nonce: value, proof: proof(role, token, value) });
  };
  const environment = {
    TINADEC_CORE_URL: 'http://127.0.0.1:48741',
    TINADEC_GATEWAY_URL: 'http://127.0.0.1:48740',
    TINADEC_MANAGED_API_PORTS: '48740,48741',
  };

  assert.equal(await verifyManagedHost(token, { fetchImpl, environment }), true);
  await assert.rejects(
    verifyHostIdentity('http://127.0.0.1:48742', 'core', token, { fetchImpl, environment }),
    /managed local/,
  );
});
