const assert = require('node:assert/strict');
const test = require('node:test');
const {
  candidateHosts,
  classifyHealth,
  discoverServices,
  isPrivateIpv4,
} = require('./serviceDiscovery.cjs');

test('isPrivateIpv4 accepts only private IPv4 ranges', () => {
  assert.equal(isPrivateIpv4('10.0.0.5'), true);
  assert.equal(isPrivateIpv4('192.168.1.20'), true);
  assert.equal(isPrivateIpv4('172.16.0.1'), true);
  assert.equal(isPrivateIpv4('172.31.255.255'), true);
  assert.equal(isPrivateIpv4('172.15.0.1'), false);
  assert.equal(isPrivateIpv4('172.32.0.1'), false);
  assert.equal(isPrivateIpv4('8.8.8.8'), false);
  assert.equal(isPrivateIpv4('169.254.1.1'), false);
  assert.equal(isPrivateIpv4('not-an-ip'), false);
});

test('candidateHosts keeps loopback and private NICs, drops public and internal', () => {
  const hosts = candidateHosts({
    lo: [{ family: 'IPv4', address: '127.0.0.1', internal: true }],
    eth0: [
      { family: 'IPv4', address: '192.168.1.10', internal: false },
      { family: 'IPv4', address: '203.0.113.7', internal: false },
      { family: 'IPv6', address: 'fe80::1', internal: false },
    ],
  });
  assert.deepEqual(hosts, ['127.0.0.1', '192.168.1.10']);
});

test('classifyHealth fingerprints gateway and core payloads', () => {
  const gatewayReady = classifyHealth(200, {
    name: 'tinadec-core', status: 'ok', version: '0.1.0',
    gateway: 'ok', core_status: 'ready', mode: 'local',
  });
  assert.deepEqual(gatewayReady, { service: 'gateway', core_status: 'ready', mode: 'local', version: '0.1.0' });

  const gatewayDegraded = classifyHealth(503, { gateway: 'ok', core_status: 'unreachable', mode: 'local' });
  assert.deepEqual(gatewayDegraded, { service: 'gateway', core_status: 'unreachable', mode: 'local', version: undefined });

  const core = classifyHealth(200, { name: 'tinadec-core', status: 'ok', version: '0.1.0' });
  assert.deepEqual(core, { service: 'core', core_status: 'ready', version: '0.1.0' });

  assert.equal(classifyHealth(200, { hello: 'world' }), null);
  assert.equal(classifyHealth(200, null), null);
  assert.equal(classifyHealth(502, { code: 'CORE_UNREACHABLE' }), null);
});

test('discoverServices lists reachable Tinadec services and skips unrelated ports', async () => {
  const openPorts = new Set(['127.0.0.1:48730', '127.0.0.1:48731', '127.0.0.1:48735']);
  const healthByUrl = {
    'http://127.0.0.1:48730': { status: 200, payload: { gateway: 'ok', core_status: 'ready', mode: 'local', name: 'tinadec-core', status: 'ok', version: '0.1.0' } },
    'http://127.0.0.1:48731': { status: 200, payload: { name: 'tinadec-core', status: 'ok', version: '0.1.0' } },
    'http://127.0.0.1:48735': { status: 200, payload: { server: 'unrelated' } },
  };

  const services = await discoverServices({
    hosts: ['127.0.0.1'],
    currentGatewayUrl: 'http://127.0.0.1:48730/',
    tcpProbe: async (host, port) => openPorts.has(`${host}:${port}`),
    fetchHealth: async (url) => healthByUrl[url] ?? { status: 404, payload: null },
  });

  assert.deepEqual(services.map((entry) => entry.url), ['http://127.0.0.1:48730', 'http://127.0.0.1:48731']);
  assert.equal(services[0].service, 'gateway');
  assert.equal(services[0].current, true);
  assert.equal(services[0].core_status, 'ready');
  assert.equal(services[1].service, 'core');
  assert.equal(services[1].current, false);
});

test('discoverServices tolerates probe and fetch failures', async () => {
  const services = await discoverServices({
    hosts: ['127.0.0.1'],
    ports: [48730, 48731],
    tcpProbe: async (host, port) => {
      if (port === 48730) throw new Error('boom');
      return true;
    },
    fetchHealth: async () => {
      throw new Error('fetch failed');
    },
  });
  assert.deepEqual(services, []);
});
