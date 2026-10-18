const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const { existsSync, mkdirSync, mkdtempSync, rmSync, writeFileSync } = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const {
  CORE_URL,
  DEFAULT_GATEWAY_URL,
  bundledRuntimePaths,
  canonicalLocalGatewayUrl,
  createServiceManager: createActualServiceManager,
  officeRootFor,
  shouldManageLocalServices,
} = require('./serviceManager.cjs');
const { createHmac } = require('node:crypto');
const testHostToken = 'a'.repeat(43);
// Existing process-lifecycle fixtures isolate identity verification; the following dedicated tests exercise real proofs.
function createServiceManager(options) {
  const manager = createActualServiceManager({ verifyHostIdentityImpl: async () => true, ...options });
  const ensure = manager.ensureLocalServices;
  manager.ensureLocalServices = args => ensure({ hostControlToken: testHostToken, ...args });
  return manager;
}

const coreHealth = {
  name: 'tinadec-core',
  status: 'ok',
  version: '0.1.0',
  time: '2026-01-01T00:00:00Z',
};
const gatewayHealth = {
  ...coreHealth,
  gateway: 'ok',
  core_status: 'ready',
  mode: 'local',
  core_url: CORE_URL,
};

class FakeChild extends EventEmitter {
  constructor(pid) {
    super();
    this.pid = pid;
    this.exitCode = null;
    this.signalCode = null;
    this.killCalls = [];
  }

  kill(signal) {
    this.killCalls.push(signal);
  }
}

function createRuntime(root, platform = 'win32') {
  const resourcesPath = path.join(root, 'resources');
  const localAppDataPath = path.join(root, 'local-app-data');
  const paths = bundledRuntimePaths(resourcesPath, platform);
  for (const file of [paths.core, paths.gateway, paths.tools]) {
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, '');
  }
  for (const directory of [paths.gitCmdDir, paths.gitBinDir]) {
    mkdirSync(directory, { recursive: true });
  }
  return { resourcesPath, localAppDataPath, paths };
}

function healthResponse(health) {
  return { ok: true, json: async () => health };
}

test('only packaged exact local gateways are managed and localhost canonicalizes to 127.0.0.1', () => {
  assert.equal(shouldManageLocalServices(true, DEFAULT_GATEWAY_URL), true);
  assert.equal(shouldManageLocalServices(true, 'http://localhost:48730'), true);
  assert.equal(canonicalLocalGatewayUrl('http://localhost:48730/'), DEFAULT_GATEWAY_URL);
  assert.equal(shouldManageLocalServices(true, 'http://127.0.0.1:48731'), false);
  assert.equal(shouldManageLocalServices(true, 'http://127.0.0.2:48730'), false);
  assert.equal(shouldManageLocalServices(true, 'http://localhost:48730/api'), false);
  assert.equal(shouldManageLocalServices(true, 'http://localhost:48730/?source=packaged'), false);
  assert.equal(shouldManageLocalServices(true, 'https://localhost:48730'), false);
  assert.equal(shouldManageLocalServices(false, DEFAULT_GATEWAY_URL), false);
  assert.equal(shouldManageLocalServices(true, 'https://gateway.example.com'), false);
});

test('an isolated packaged host may manage its declared gateway port', () => {
  assert.equal(canonicalLocalGatewayUrl(
    'http://localhost:48740',
    { TINADEC_MANAGED_API_PORTS: '48740,48741' },
  ), 'http://127.0.0.1:48740');
  assert.equal(canonicalLocalGatewayUrl(
    'http://localhost:48741',
    { TINADEC_MANAGED_API_PORTS: '48740,48741' },
  ), 'http://127.0.0.1:48741');
});

test('remote, development, and non-default local gateways do not start services', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-skip-'));
  try {
    for (const options of [
      { isPackaged: false, gatewayUrl: DEFAULT_GATEWAY_URL },
      { isPackaged: true, gatewayUrl: 'https://gateway.example.com' },
      { isPackaged: true, gatewayUrl: 'http://127.0.0.1:48731' },
      { isPackaged: true, gatewayUrl: 'http://localhost:48730/remote' },
    ]) {
      const manager = createServiceManager({
        platform: 'win32',
        fetchImpl: async () => {
          throw new Error('health probes must not run');
        },
        spawnImpl: () => {
          throw new Error('services must not start');
        },
      });
      assert.deepEqual(await manager.ensureLocalServices(options), {
        started: false,
        ownsCore: false,
        ownsGateway: false,
      });
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('packaged localhost starts Core then Gateway with explicit runtime paths and environment', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-start-'));
  const runtime = createRuntime(root);
  const ready = new Set();
  const probes = [];
  const launches = [];
  let nextPid = 100;
  const manager = createServiceManager({
    platform: 'win32',
    environment: { TINADEC_HOME: path.join(root, '.tinadec'), Path: 'C:\\Windows' },
    homedirImpl: () => root,
    startupTimeoutMs: 100,
    pollIntervalMs: 1,
    fetchImpl: async (url) => {
      probes.push(url);
      if (!ready.has(url)) throw new Error('not listening');
      return healthResponse(url.startsWith(CORE_URL) ? coreHealth : gatewayHealth);
    },
    spawnImpl: (command, args, options) => {
      const child = new FakeChild(nextPid++);
      launches.push({ command, args, options, child });
      ready.add(command === runtime.paths.core
        ? `${CORE_URL}/api/v1/health`
        : `${DEFAULT_GATEWAY_URL}/api/v1/health`);
      return child;
    },
    spawnSyncImpl: (command, args) => {
      assert.equal(command, 'taskkill.exe');
      return { status: 0 };
    },
  });

  try {
    assert.deepEqual(
      await manager.ensureLocalServices({
        isPackaged: true,
        gatewayUrl: 'http://localhost:48730',
        resourcesPath: runtime.resourcesPath,
        localAppDataPath: runtime.localAppDataPath,
      }),
      { started: true, ownsCore: true, ownsGateway: true },
    );
    assert.deepEqual(launches.map((launch) => launch.command), [runtime.paths.core, runtime.paths.gateway]);
    assert.deepEqual(launches[0].args, ['--urls', CORE_URL]);
    assert.deepEqual(launches[1].args, []);
    assert.equal(launches[0].options.cwd, runtime.paths.coreDir);
    assert.equal(launches[1].options.cwd, runtime.paths.gatewayDir);
    assert.equal(launches[0].options.env.ASPNETCORE_URLS, CORE_URL);
    assert.equal(launches[0].options.env.TINADEC_HOME, path.join(root, '.tinadec'));
    assert.equal(launches[0].options.env.TinadecPersistence__Provider, undefined);
    assert.equal(launches[0].options.env.TinadecPersistence__Sqlite__DatabasePath, undefined);
    assert.equal(launches[0].options.env.TinadecTools__ExecutablePath, runtime.paths.tools);
    assert.equal(launches[0].options.env.TinadecTools__DefaultWorkspaceRoot, path.join(root, 'TinadecProjects'));
    assert.equal(launches[1].options.env.TINADEC_GATEWAY_MODE, 'local');
    assert.equal(launches[1].options.env.TINADEC_GATEWAY_PORT, '48730');
    assert.equal(launches[1].options.env.TINADEC_CORE_URL, CORE_URL);
    assert.equal(launches[0].options.env.Path, `${runtime.paths.toolsDir};${runtime.paths.gitCmdDir};${runtime.paths.gitBinDir};C:\\Windows`);
    assert.ok(probes.every((url) => url === `${CORE_URL}/api/v1/health` || url === `${DEFAULT_GATEWAY_URL}/api/v1/health`));
    assert.ok(existsSync(path.join(root, '.tinadec', 'logs', 'host', 'core.log')));
    assert.ok(existsSync(path.join(root, '.tinadec', 'logs', 'host', 'gateway.log')));
    assert.ok(existsSync(path.join(root, 'TinadecProjects')));
  } finally {
    await manager.stopLocalServices();
    rmSync(root, { recursive: true, force: true });
  }
});

test('compatible existing Core and Gateway are reused without spawning or stopping them', async () => {
  const taskKills = [];
  const manager = createServiceManager({
    platform: 'win32',
    fetchImpl: async (url) => healthResponse(url.startsWith(CORE_URL) ? coreHealth : gatewayHealth),
    spawnImpl: () => {
      throw new Error('existing services must be reused');
    },
    spawnSyncImpl: (command, args) => {
      taskKills.push({ command, args });
      return { status: 0 };
    },
  });

  assert.deepEqual(
    await manager.ensureLocalServices({
      isPackaged: true,
      gatewayUrl: DEFAULT_GATEWAY_URL,
      resourcesPath: '',
      localAppDataPath: '',
    }),
    { started: false, ownsCore: false, ownsGateway: false },
  );
  await manager.stopLocalServices();
  assert.deepEqual(taskKills, []);
  assert.deepEqual(manager.ownedServiceLabels(), []);
});

test('public compatible fingerprints with a false HMAC are rejected without sending a private header or spawning', async () => {
  const requests = []; let spawned = false;
  const manager = createActualServiceManager({ fetchImpl: async (url, init) => {
    requests.push({ url, headers: new Headers(init.headers) });
    if (url.includes('host-challenge')) return Response.json({ role: 'core', nonce: new URL(url).searchParams.get('nonce'), proof: '0'.repeat(64) });
    return healthResponse(url.startsWith(CORE_URL) ? coreHealth : gatewayHealth);
  }, spawnImpl: () => { spawned = true; throw new Error('must not spawn'); } });
  await assert.rejects(manager.ensureLocalServices({ isPackaged: true, gatewayUrl: DEFAULT_GATEWAY_URL, hostControlToken: testHostToken }), /invalid trusted host identity proof/);
  assert.equal(spawned, false);
  assert.equal(requests.every(call => !call.headers.has('x-tinadec-host-control')), true);
});

test('existing services are reused only after both roles prove the same trusted startup key', async () => {
  const requests = [];
  const manager = createActualServiceManager({ fetchImpl: async (url, init) => {
    requests.push({ url, headers: new Headers(init.headers) });
    if (!url.includes('host-challenge')) return healthResponse(url.startsWith(CORE_URL) ? coreHealth : gatewayHealth);
    const nonce = new URL(url).searchParams.get('nonce'); const role = url.startsWith(CORE_URL) ? 'core' : 'gateway';
    return Response.json({ role, nonce, proof: createHmac('sha256', testHostToken).update(`tinadec-host-v1\0${role}\0${nonce}`).digest('hex') });
  }, spawnImpl: () => { throw new Error('authenticated services must be reused'); } });
  assert.deepEqual(await manager.ensureLocalServices({ isPackaged: true, gatewayUrl: DEFAULT_GATEWAY_URL, hostControlToken: testHostToken }), { started: false, ownsCore: false, ownsGateway: false });
  assert.equal(requests.filter(call => call.url.includes('host-challenge')).length, 2);
  assert.equal(requests.every(call => !call.headers.has('x-tinadec-host-control')), true);
});

test('startup failure rolls back only the processes launched by the failed attempt', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-rollback-'));
  const runtime = createRuntime(root);
  const ready = new Set();
  const taskKills = [];
  const launches = [];
  let nextPid = 300;
  const manager = createServiceManager({
    platform: 'win32',
    environment: { TINADEC_HOME: path.join(root, '.tinadec') },
    homedirImpl: () => root,
    startupTimeoutMs: 100,
    pollIntervalMs: 1,
    fetchImpl: async (url) => {
      if (!ready.has(url)) throw new Error('not listening');
      return healthResponse(url.startsWith(CORE_URL) ? coreHealth : gatewayHealth);
    },
    spawnImpl: (command) => {
      const child = new FakeChild(nextPid++);
      launches.push({ command, child });
      if (command === runtime.paths.core) {
        ready.add(`${CORE_URL}/api/v1/health`);
      } else {
        child.exitCode = 23;
      }
      return child;
    },
    spawnSyncImpl: (command, args) => {
      taskKills.push({ command, args });
      return { status: 0 };
    },
  });

  try {
    await assert.rejects(
      manager.ensureLocalServices({
        isPackaged: true,
        gatewayUrl: DEFAULT_GATEWAY_URL,
        resourcesPath: runtime.resourcesPath,
        localAppDataPath: runtime.localAppDataPath,
      }),
      /Tinadec Gateway exited with code 23/,
    );
    assert.deepEqual(launches.map((launch) => launch.command), [runtime.paths.core, runtime.paths.gateway]);
    assert.deepEqual(taskKills.map((entry) => entry.args), [
      ['/pid', '300', '/t', '/f'],
    ]);
    assert.deepEqual(manager.ownedServiceLabels(), []);
  } finally {
    await manager.stopLocalServices();
    rmSync(root, { recursive: true, force: true });
  }
});

test('stop uses tree kill for owned processes and never targets a reused Core', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-owned-'));
  const runtime = createRuntime(root);
  const taskKills = [];
  let gatewayReady = false;
  let nextPid = 500;
  const manager = createServiceManager({
    platform: 'win32',
    environment: { TINADEC_HOME: path.join(root, '.tinadec') },
    homedirImpl: () => root,
    startupTimeoutMs: 100,
    pollIntervalMs: 1,
    fetchImpl: async (url) => {
      if (url.startsWith(CORE_URL)) return healthResponse(coreHealth);
      if (!gatewayReady) throw new Error('not listening');
      return healthResponse(gatewayHealth);
    },
    spawnImpl: () => {
      const child = new FakeChild(nextPid++);
      gatewayReady = true;
      return child;
    },
    spawnSyncImpl: (command, args) => {
      taskKills.push({ command, args });
      return { status: 0 };
    },
  });

  try {
    assert.deepEqual(
      await manager.ensureLocalServices({
        isPackaged: true,
        gatewayUrl: DEFAULT_GATEWAY_URL,
        resourcesPath: runtime.resourcesPath,
        localAppDataPath: runtime.localAppDataPath,
      }),
      { started: true, ownsCore: false, ownsGateway: true },
    );
    await manager.stopLocalServices();
    assert.deepEqual(taskKills, [
      { command: 'taskkill.exe', args: ['/pid', '500', '/t', '/f'] },
    ]);
    assert.deepEqual(manager.ownedServiceLabels(), []);
  } finally {
    await manager.stopLocalServices();
    rmSync(root, { recursive: true, force: true });
  }
});

test('non-Windows shutdown signals the process group and never escalates', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-signals-'));
  const runtime = createRuntime(root, 'linux');
  const ready = new Set();
  const children = [];
  const groupSignals = [];
  const manager = createServiceManager({
    platform: 'linux',
    environment: { TINADEC_HOME: path.join(root, '.tinadec'), PATH: '/usr/bin' },
    homedirImpl: () => root,
    startupTimeoutMs: 100,
    pollIntervalMs: 1,
    // The default seam is `process.kill(-pid, signal)`, and these pids are invented. Whether
    // process group 600 exists is a property of the machine running the test — on the macOS runner
    // it did, so the group signal "succeeded" against somebody else's processes, the fake child
    // recorded nothing, and the assertion below failed for reasons unrelated to this code.
    signalGroupImpl: (pid, signal) => {
      groupSignals.push({ pid, signal });
      const target = children.find((child) => child.pid === pid);
      if (target) {
        target.exitCode = 0;
        queueMicrotask(() => target.emit('close'));
      }
    },
    fetchImpl: async (url) => {
      if (!ready.has(url)) throw new Error('not listening');
      return healthResponse(url.startsWith(CORE_URL) ? coreHealth : gatewayHealth);
    },
    spawnImpl: (command) => {
      const child = new FakeChild(600 + children.length);
      child.kill = (signal) => {
        child.killCalls.push(signal);
        child.exitCode = 0;
        queueMicrotask(() => child.emit('close'));
      };
      children.push(child);
      ready.add(command === runtime.paths.core
        ? `${CORE_URL}/api/v1/health`
        : `${DEFAULT_GATEWAY_URL}/api/v1/health`);
      return child;
    },
    spawnSyncImpl: () => {
      throw new Error('taskkill must not run off Windows');
    },
  });

  try {
    await manager.ensureLocalServices({
      isPackaged: true,
      gatewayUrl: DEFAULT_GATEWAY_URL,
      resourcesPath: runtime.resourcesPath,
      localAppDataPath: runtime.localAppDataPath,
    });
    await manager.stopLocalServices();
    // One group signal per service, in teardown order, and no escalation: SIGKILL here would mean
    // the tree signal never landed.
    assert.deepEqual(groupSignals, [
      { pid: 601, signal: 'SIGTERM' },
      { pid: 600, signal: 'SIGTERM' },
    ]);
    assert.deepEqual(children.map((child) => child.killCalls), [[], []], 'the single-process fallback is for a failed group signal only');
    assert.deepEqual(manager.ownedServiceLabels(), []);
  } finally {
    await manager.stopLocalServices();
    rmSync(root, { recursive: true, force: true });
  }
});

// ── cross-platform data root and process-tree termination ────────────────────

test('the data root is shared across platforms and ignores legacy platform roots', () => {
  const home = path.resolve(path.sep, 'home', 'tester');
  for (const platform of ['win32', 'darwin', 'linux']) {
    assert.equal(officeRootFor({ platform, localAppDataPath: 'ignored', environment: { XDG_DATA_HOME: 'ignored' }, homedirImpl: () => home }), path.join(home, '.tinadec'));
  }
  assert.equal(officeRootFor({ environment: { TINADEC_HOME: home }, homedirImpl: () => 'ignored' }), home);
});

test('Linux starts services detached and terminates the process group, not just the child', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-linux-'));
  const resourcesPath = path.join(root, 'resources');
  const paths = bundledRuntimePaths(resourcesPath, 'linux');
  for (const file of [paths.core, paths.gateway, paths.tools]) {
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, '');
  }
  // Deliberately no git/cmd or git/bin: that is what a Linux package actually contains, and
  // the startup path must accept it.
  const runtime = { resourcesPath, localAppDataPath: path.join(root, 'unused'), paths };
  const xdgData = path.join(root, 'xdg-data');
  const ready = new Set();
  const launches = [];
  const groupSignals = [];
  let nextPid = 500;
  const manager = createServiceManager({
    platform: 'linux',
    environment: { XDG_DATA_HOME: xdgData, PATH: '/usr/bin' },
    homedirImpl: () => root,
    signalGroupImpl: (pid, signal) => {
      groupSignals.push({ pid, signal });
      const target = launches.find((launch) => launch.child.pid === pid)?.child;
      if (target) {
        target.exitCode = 0;
        target.emit('close');
      }
    },
    startupTimeoutMs: 100,
    pollIntervalMs: 1,
    fetchImpl: async (url) => {
      if (!ready.has(url)) throw new Error('not listening');
      return healthResponse(url.startsWith(CORE_URL) ? coreHealth : gatewayHealth);
    },
    spawnImpl: (command, args, options) => {
      const child = new FakeChild(nextPid++);
      launches.push({ command, args, options, child });
      ready.add(command === runtime.paths.core
        ? `${CORE_URL}/api/v1/health`
        : `${DEFAULT_GATEWAY_URL}/api/v1/health`);
      return child;
    },
    spawnSyncImpl: () => {
      throw new Error('taskkill is a Windows path; a POSIX stop must not reach for it');
    },
  });

  try {
    assert.deepEqual(
      await manager.ensureLocalServices({
        isPackaged: true,
        gatewayUrl: DEFAULT_GATEWAY_URL,
        resourcesPath: runtime.resourcesPath,
        localAppDataPath: undefined,
      }),
      { started: true, ownsCore: true, ownsGateway: true },
    );

    // No .exe suffix, and the bundled PortableGit dirs are absent: requiring them is what
    // made a complete Linux install refuse to start.
    assert.ok(!runtime.paths.tools.endsWith('.exe'));
    assert.equal(existsSync(runtime.paths.gitCmdDir), false);
    assert.equal(launches[0].options.detached, true);
    assert.equal(
      launches[0].options.env.TINADEC_HOME,
      path.join(root, '.tinadec'));
    assert.equal(launches[0].options.env.PATH, `${runtime.paths.toolsDir}:/usr/bin`);

    await manager.stopLocalServices();
    assert.deepEqual(groupSignals.map((entry) => entry.signal), ['SIGTERM', 'SIGTERM']);
    assert.deepEqual(
      groupSignals.map((entry) => entry.pid).sort(),
      launches.map((launch) => launch.child.pid).sort(),
    );
    for (const launch of launches) {
      assert.deepEqual(launch.child.killCalls, [], 'the group signal reached the tree; no per-child kill');
    }
  } finally {
    await manager.stopLocalServices();
    rmSync(root, { recursive: true, force: true });
  }
});

 test('cancelled startup never starts a process after its initial health result arrives', async () => {
  const controller = new AbortController(); let release; let starts = 0;
  const manager = createServiceManager({ fetchImpl: () => new Promise(resolve => { release = resolve; }), spawnImpl: () => { starts++; } });
  const pending = manager.ensureLocalServices({ isPackaged: true, gatewayUrl: DEFAULT_GATEWAY_URL, signal: controller.signal });
  controller.abort(new Error('host shutting down'));
  release(healthResponse(coreHealth));
  await assert.rejects(pending, /host shutting down/);
  assert.equal(starts, 0);
});
