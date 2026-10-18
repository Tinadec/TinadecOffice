const { existsSync, mkdirSync } = require('node:fs');
const { spawn, spawnSync } = require('node:child_process');
const { homedir } = require('node:os');
const { join } = require('node:path');
const { storagePaths } = require('./storagePaths.cjs');
const { createLogSink } = require('./logSink.cjs');
const { verifyHostIdentity } = require('./hostIdentity.cjs');

const DEFAULT_GATEWAY_URL = 'http://127.0.0.1:48730';
const CORE_URL = 'http://127.0.0.1:48731';
const SERVICE_VERSION = '0.1.0';

function bundledRuntimePaths(resourcesPath, platform = process.platform) {
  const root = join(resourcesPath, 'runtime');
  const coreDir = join(root, 'core');
  const gatewayDir = join(root, 'gateway');
  const toolsDir = join(root, 'tools');
  const gitCmdDir = join(root, 'git', 'cmd');
  const gitBinDir = join(root, 'git', 'bin');
  const executable = (name) => platform === 'win32' ? `${name}.exe` : name;
  return {
    root,
    core: join(coreDir, executable('TinadecCore.Api')),
    coreDir,
    gateway: join(gatewayDir, executable('TinadecGateway')),
    gatewayDir,
    tools: join(toolsDir, executable('TinadecTools')),
    toolsDir,
    gitCmdDir,
    gitBinDir,
  };
}

/**
 * The same explicit user storage root used by Electron and Core on every platform.
 * @returns {string}
 */
function officeRootFor({ platform, localAppDataPath, environment, homedirImpl }) {
  return storagePaths(environment ?? {}, () => homedirImpl?.() || environment?.HOME || homedir()).root;
}

function managedApiPorts(environment = process.env) {
  const configured = String(environment.TINADEC_MANAGED_API_PORTS ?? '')
    .split(',')
    .map(value => value.trim())
    .filter(value => /^\d{2,5}$/.test(value));
  if (configured.length > 0) return new Set(configured);
  const gatewayPort = String(environment.TINADEC_GATEWAY_PORT ?? '').trim();
  return new Set(gatewayPort ? [gatewayPort] : ['48730']);
}

function canonicalLocalGatewayUrl(gatewayUrl, environment = process.env) {
  if (typeof gatewayUrl !== 'string' || !gatewayUrl.trim()) return null;
  try {
    const url = new URL(gatewayUrl.trim());
    if (
      url.protocol !== 'http:' ||
      !['127.0.0.1', 'localhost'].includes(url.hostname) ||
      !managedApiPorts(environment).has(url.port) ||
      (url.pathname !== '/' && url.pathname !== '') ||
      url.username ||
      url.password ||
      url.search ||
      url.hash
    ) {
      return null;
    }
    return `http://127.0.0.1:${url.port}`;
  } catch {
    return null;
  }
}

function shouldManageLocalServices(isPackaged, gatewayUrl) {
  return Boolean(isPackaged && canonicalLocalGatewayUrl(gatewayUrl));
}

function matchesServiceIdentity(service, health) {
  if (!health || typeof health !== 'object' || Array.isArray(health)) return false;
  if (health.name !== 'tinadec-core' || health.status !== 'ok' || health.version !== SERVICE_VERSION) {
    return false;
  }
  if (service === 'core') return true;
  return service === 'gateway' && (
    health.gateway === 'ok' &&
    health.core_status === 'ready' &&
    health.mode === 'local' &&
    health.core_url === CORE_URL
  );
}

async function probeService(
  url,
  service,
  { fetchImpl = globalThis.fetch, timeoutMs = 800 } = {},
) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetchImpl(url, {
      headers: { accept: 'application/json' },
      signal: controller.signal,
    });
    let health;
    try {
      health = await response.json();
    } catch {
      return { status: 'mismatch' };
    }
    if (response.ok && matchesServiceIdentity(service, health)) {
      return { status: 'ready' };
    }
    if (
      !response.ok &&
      service === 'gateway' &&
      health?.gateway === 'ok' &&
      health?.core_status === 'unreachable' &&
      health?.mode === 'local' &&
      health?.core_url === CORE_URL
    ) {
      return { status: 'unavailable' };
    }
    return { status: 'mismatch' };
  } catch {
    return { status: 'unavailable' };
  } finally {
    clearTimeout(timeout);
  }
}

function buildServiceEnvironment(paths, env, platform = process.platform) {
  const result = { ...env };
  const pathKey = platform === 'win32'
    ? Object.keys(result).find((key) => key.toLowerCase() === 'path') ?? 'PATH'
    : 'PATH';
  const separator = platform === 'win32' ? ';' : ':';
  result[pathKey] = [
    paths.toolsDir,
    ...(platform === 'win32' ? [paths.gitCmdDir, paths.gitBinDir] : []),
    result[pathKey],
  ].filter(Boolean).join(separator);
  return result;
}

function createServiceManager({
  platform = process.platform,
  fetchImpl = globalThis.fetch,
  verifyHostIdentityImpl = verifyHostIdentity,
  spawnImpl = spawn,
  spawnSyncImpl = spawnSync,
  environment = process.env,
  homedirImpl = homedir,
  signalGroupImpl = (pid, signal) => process.kill(-pid, signal),
  healthTimeoutMs = 800,
  startupTimeoutMs = 90_000,
  pollIntervalMs = 250,
} = {}) {
  const ownedChildren = new Map();
  let stopping;

  function closeLog(child) {
    child.stdout?.removeAllListeners('data');
    child.stderr?.removeAllListeners('data');
  }

  function startProcess(label, command, args, cwd, env, logsDir) {
    const logSink = createLogSink(logsDir, { budgetDirectory: join(logsDir, '..') });
    logSink.write(label, `${new Date().toISOString()} starting ${label}\n`);
    let child;
    try {
      child = spawnImpl(command, args, {
        cwd,
        env,
        // A POSIX child that leads its own process group can be terminated as a tree with
        // one signal. That matters here specifically because Core spawns TinadecTools as its
        // own child: killing just Core would leave a tool host holding the workspace.
        // Windows keeps the group semantics it has (taskkill /t /f) instead of detached,
        // which would change console inheritance for the whole service tree.
        detached: platform !== 'win32',
        windowsHide: true,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    } catch (error) {
      throw error;
    }

    child.startupError = null;
    for (const stream of [child.stdout, child.stderr]) stream?.on('data', data => {
      try { logSink.write(label, data); } catch (error) { console.error(`[tinadec] ${label} log write failed: ${error.message}`); }
    });
    ownedChildren.set(label, child);
    child.once('error', (error) => {
      child.startupError = error;
      closeLog(child);
    });
    child.once('close', () => {
      if (ownedChildren.get(label) === child) ownedChildren.delete(label);
      closeLog(child);
    });
    return child;
  }

  async function waitForService(url, service, child, label, hostControlToken, signal) {
    const deadline = Date.now() + startupTimeoutMs;
    while (Date.now() < deadline) {
      signal?.throwIfAborted();
      const probe = await probeService(url, service, { fetchImpl, timeoutMs: healthTimeoutMs });
      signal?.throwIfAborted();
      if (probe.status === 'ready') {
        await verifyHostIdentityImpl(new URL(url).origin, service, hostControlToken, { fetchImpl, timeoutMs: healthTimeoutMs, signal });
        signal?.throwIfAborted();
        return;
      }
      if (probe.status === 'mismatch') {
        throw new Error(`${label} endpoint at ${url} is occupied by an unexpected service.`);
      }
      if (child.startupError) throw child.startupError;
      if (child.exitCode !== null || child.signalCode != null) {
        throw new Error(`${label} exited with code ${child.exitCode}. See the service log for details.`);
      }
      await new Promise((resolve) => setTimeout(resolve, pollIntervalMs));
    }
    throw new Error(`${label} did not become ready within ${startupTimeoutMs / 1000} seconds.`);
  }

  function waitForExit(child, timeoutMs) {
    if (child.exitCode !== null || child.signalCode != null) {
      return Promise.resolve(true);
    }
    return new Promise((resolve) => {
      const onClose = () => finish(true);
      const timeout = setTimeout(() => finish(false), timeoutMs);
      function finish(exited) {
        clearTimeout(timeout);
        child.removeListener('close', onClose);
        resolve(exited);
      }
      child.once('close', onClose);
    });
  }

  async function terminateChild(child) {
    if (!child.pid || child.exitCode !== null || child.signalCode != null) return;
    if (platform === 'win32') {
      const result = spawnSyncImpl('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], {
        stdio: 'ignore',
        windowsHide: true,
      });
      if (!result || result.error || result.status !== 0) child.kill();
      return;
    }

    // Services start detached on POSIX, so the pid is also the process-group id and one
    // signal reaches Core and the TinadecTools hosts it spawned. If the group signal does
    // not land (the child never became a leader, or it is already gone), fall back to the
    // single process rather than assume the tree is clean.
    const signalTree = (signal) => {
      try {
        signalGroupImpl(child.pid, signal);
        return true;
      } catch {
        try {
          child.kill(signal);
          return true;
        } catch {
          return false;
        }
      }
    };

    if (!signalTree('SIGTERM')) return;
    if (await waitForExit(child, 3_000)) return;
    if (!signalTree('SIGKILL')) return;
    await waitForExit(child, 1_000);
  }

  async function stopChildren(children) {
    const terminations = [];
    for (const [label, child] of [...children].reverse()) {
      if (ownedChildren.get(label) === child) ownedChildren.delete(label);
      terminations.push(terminateChild(child));
    }
    await Promise.all(terminations);
    for (const [, child] of children) closeLog(child);
  }

  async function stopLocalServices() {
    if (stopping) return stopping;
    stopping = (async () => {
      const children = [...ownedChildren.entries()];
      await stopChildren(children);
    })().finally(() => {
      stopping = undefined;
    });
    return stopping;
  }

  function requireRuntime(resourcesPath, localAppDataPath, userStorageRoot) {
    if (!resourcesPath) throw new Error('Electron resources path is unavailable.');
    const paths = bundledRuntimePaths(resourcesPath, platform);
    for (const name of ['core', 'gateway', 'tools']) {
      if (!existsSync(paths[name])) {
        throw new Error(`Bundled ${name} runtime is missing: ${paths[name]}`);
      }
    }
    if (platform === 'win32') {
      // PortableGit ships in the Windows package; on Linux and macOS the tools use the
      // system git, so requiring those directories here would refuse a good install.
      for (const name of ['gitCmdDir', 'gitBinDir']) {
        if (!existsSync(paths[name])) {
          throw new Error(`Bundled ${name} runtime is missing: ${paths[name]}`);
        }
      }
    }
    const officeRoot = userStorageRoot ?? officeRootFor({ platform, localAppDataPath, environment, homedirImpl });
    const dataRoot = join(officeRoot, 'data');
    const logsDir = join(officeRoot, 'logs', 'host');
    const workspaceRoot = environment.TinadecTools__DefaultWorkspaceRoot ?? join(homedirImpl(), 'TinadecProjects');
    mkdirSync(dataRoot, { recursive: true });
    mkdirSync(logsDir, { recursive: true });
    mkdirSync(workspaceRoot, { recursive: true });
    return {
      paths,
      officeRoot,
      dataRoot,
      logsDir,
      workspaceRoot,
      databasePath: join(dataRoot, 'tinadec.db'),
    };
  }

  async function ensureLocalServices({ isPackaged, gatewayUrl, resourcesPath, localAppDataPath, hostControlToken, userStorageRoot, signal }) {
    signal?.throwIfAborted();
    if (!shouldManageLocalServices(isPackaged, gatewayUrl)) {
      return { started: false, ownsCore: false, ownsGateway: false };
    }
    if (stopping) await stopping;
    signal?.throwIfAborted();

    const canonicalGatewayUrl = canonicalLocalGatewayUrl(gatewayUrl);
    const coreHealthUrl = `${CORE_URL}/api/v1/health`;
    const gatewayHealthUrl = `${canonicalGatewayUrl}/api/v1/health`;
    const coreProbe = await probeService(coreHealthUrl, 'core', {
      fetchImpl,
      timeoutMs: healthTimeoutMs,
    });
    signal?.throwIfAborted();
    if (coreProbe.status === 'mismatch') {
      throw new Error(`Tinadec Core endpoint at ${coreHealthUrl} is occupied by an unexpected service.`);
    }
    if (coreProbe.status === 'ready') await verifyHostIdentityImpl(CORE_URL, 'core', hostControlToken, { fetchImpl, timeoutMs: healthTimeoutMs, signal });
    const gatewayProbe = await probeService(gatewayHealthUrl, 'gateway', {
      fetchImpl,
      timeoutMs: healthTimeoutMs,
    });
    signal?.throwIfAborted();
    if (gatewayProbe.status === 'mismatch') {
      throw new Error(`Tinadec Gateway endpoint at ${gatewayHealthUrl} is occupied by an unexpected service.`);
    }
    if (gatewayProbe.status === 'ready') await verifyHostIdentityImpl(canonicalGatewayUrl, 'gateway', hostControlToken, { fetchImpl, timeoutMs: healthTimeoutMs, signal });
    if (coreProbe.status === 'ready' && gatewayProbe.status === 'ready') {
      return {
        started: false,
        ownsCore: ownedChildren.has('core'),
        ownsGateway: ownedChildren.has('gateway'),
      };
    }

    signal?.throwIfAborted();
    const runtime = requireRuntime(resourcesPath, localAppDataPath, userStorageRoot);
    const baseEnvironment = buildServiceEnvironment(runtime.paths, { ...environment, TINADEC_HOME: runtime.officeRoot, TINADEC_STORAGE_ID: 'user', ...(hostControlToken ? { TINADEC_HOST_CONTROL_TOKEN: hostControlToken } : {}) }, platform);
    const startedChildren = [];
    try {
      signal?.throwIfAborted();
      if (coreProbe.status !== 'ready') {
        const core = startProcess(
          'core',
          runtime.paths.core,
          ['--urls', CORE_URL],
          runtime.paths.coreDir,
          {
            ...baseEnvironment,
            ASPNETCORE_URLS: CORE_URL,
            TinadecTools__ExecutablePath: runtime.paths.tools,
            TinadecTools__DefaultWorkspaceRoot: runtime.workspaceRoot,
          },
          runtime.logsDir,
        );
        startedChildren.push(['core', core]);
        await waitForService(coreHealthUrl, 'core', core, 'Tinadec Core', hostControlToken, signal);
      }

      signal?.throwIfAborted();
      let currentGatewayProbe = gatewayProbe;
      if (currentGatewayProbe.status !== 'ready') {
        currentGatewayProbe = await probeService(gatewayHealthUrl, 'gateway', {
          fetchImpl,
          timeoutMs: healthTimeoutMs,
        });
        signal?.throwIfAborted();
        if (currentGatewayProbe.status === 'mismatch') {
          throw new Error(`Tinadec Gateway endpoint at ${gatewayHealthUrl} is occupied by an unexpected service.`);
        }
        if (currentGatewayProbe.status === 'ready') await verifyHostIdentityImpl(canonicalGatewayUrl, 'gateway', hostControlToken, { fetchImpl, timeoutMs: healthTimeoutMs, signal });
      }
      signal?.throwIfAborted();
      if (currentGatewayProbe.status !== 'ready') {
        const gateway = startProcess(
          'gateway',
          runtime.paths.gateway,
          [],
          runtime.paths.gatewayDir,
          {
            ...baseEnvironment,
            TINADEC_GATEWAY_MODE: 'local',
            TINADEC_GATEWAY_PORT: '48730',
            TINADEC_CORE_URL: CORE_URL,
          },
          runtime.logsDir,
        );
        startedChildren.push(['gateway', gateway]);
        await waitForService(gatewayHealthUrl, 'gateway', gateway, 'Tinadec Gateway', hostControlToken, signal);
      }

      signal?.throwIfAborted();
      return {
        started: startedChildren.length > 0,
        ownsCore: ownedChildren.has('core'),
        ownsGateway: ownedChildren.has('gateway'),
      };
    } catch (error) {
      await stopChildren(startedChildren);
      throw error;
    }
  }

  return {
    ensureLocalServices,
    ownedServiceLabels: () => [...ownedChildren.keys()],
    stopLocalServices,
  };
}

const serviceManager = createServiceManager();

module.exports = {
  CORE_URL,
  DEFAULT_GATEWAY_URL,
  SERVICE_VERSION,
  buildServiceEnvironment,
  bundledRuntimePaths,
  canonicalLocalGatewayUrl,
  createServiceManager,
  ensureLocalServices: serviceManager.ensureLocalServices,
  matchesServiceIdentity,
  officeRootFor,
  probeService,
  shouldManageLocalServices,
  stopLocalServices: serviceManager.stopLocalServices,
};
