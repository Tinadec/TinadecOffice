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
  createServiceManager,
  shouldManageLocalServices,
} = require('./serviceManager.cjs');

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
    environment: { Path: 'C:\\Windows' },
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
    assert.equal(launches[0].options.env.TinadecPersistence__DataRoot, path.join(runtime.localAppDataPath, 'TinadecOffice', 'data'));
    assert.equal(launches[0].options.env.TinadecPersistence__Sqlite__DatabasePath, path.join(runtime.localAppDataPath, 'TinadecOffice', 'data', 'tinadec.db'));
    assert.equal(launches[0].options.env.TinadecTools__ExecutablePath, runtime.paths.tools);
    assert.equal(launches[0].options.env.TinadecTools__DefaultWorkspaceRoot, path.join(runtime.localAppDataPath, 'TinadecOffice', 'workspaces', 'default'));
    assert.equal(launches[1].options.env.TINADEC_GATEWAY_MODE, 'local');
    assert.equal(launches[1].options.env.TINADEC_GATEWAY_PORT, '48730');
    assert.equal(launches[1].options.env.TINADEC_CORE_URL, CORE_URL);
    assert.equal(launches[0].options.env.Path, `${runtime.paths.toolsDir};${runtime.paths.gitCmdDir};${runtime.paths.gitBinDir};C:\\Windows`);
    assert.ok(probes.every((url) => url === `${CORE_URL}/api/v1/health` || url === `${DEFAULT_GATEWAY_URL}/api/v1/health`));
    assert.ok(existsSync(path.join(runtime.localAppDataPath, 'TinadecOffice', 'logs', 'core.log')));
    assert.ok(existsSync(path.join(runtime.localAppDataPath, 'TinadecOffice', 'logs', 'gateway.log')));
    assert.ok(existsSync(path.join(runtime.localAppDataPath, 'TinadecOffice', 'workspaces', 'default')));
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

test('startup failure rolls back only the processes launched by the failed attempt', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-rollback-'));
  const runtime = createRuntime(root);
  const ready = new Set();
  const taskKills = [];
  const launches = [];
  let nextPid = 300;
  const manager = createServiceManager({
    platform: 'win32',
    environment: {},
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
    environment: {},
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

test('non-Windows shutdown falls back to owned-process signals', async () => {
  const root = mkdtempSync(path.join(os.tmpdir(), 'tinadec-service-signals-'));
  const runtime = createRuntime(root, 'linux');
  const ready = new Set();
  const children = [];
  const manager = createServiceManager({
    platform: 'linux',
    environment: { PATH: '/usr/bin' },
    startupTimeoutMs: 100,
    pollIntervalMs: 1,
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
    assert.deepEqual(children.map((child) => child.killCalls), [['SIGTERM'], ['SIGTERM']]);
    assert.deepEqual(manager.ownedServiceLabels(), []);
  } finally {
    await manager.stopLocalServices();
    rmSync(root, { recursive: true, force: true });
  }
});
