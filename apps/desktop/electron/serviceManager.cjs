const { closeSync, existsSync, mkdirSync, openSync } = require('node:fs');
const { spawn, spawnSync } = require('node:child_process');
const { join } = require('node:path');

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

function canonicalLocalGatewayUrl(gatewayUrl) {
  if (typeof gatewayUrl !== 'string' || !gatewayUrl.trim()) return null;
  try {
    const url = new URL(gatewayUrl.trim());
    if (
      url.protocol !== 'http:' ||
      !['127.0.0.1', 'localhost'].includes(url.hostname) ||
      url.port !== '48730' ||
      (url.pathname !== '/' && url.pathname !== '') ||
      url.username ||
      url.password ||
      url.search ||
      url.hash
    ) {
      return null;
    }
    return DEFAULT_GATEWAY_URL;
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
  spawnImpl = spawn,
  spawnSyncImpl = spawnSync,
  environment = process.env,
  healthTimeoutMs = 800,
  startupTimeoutMs = 90_000,
  pollIntervalMs = 250,
} = {}) {
  const ownedChildren = new Map();
  const logFds = new Map();
  let stopping;

  function closeLog(child) {
    const logFd = logFds.get(child);
    if (logFd === undefined) return;
    logFds.delete(child);
    try {
      closeSync(logFd);
    } catch {}
  }

  function startProcess(label, command, args, cwd, env, logsDir) {
    const logFd = openSync(join(logsDir, `${label}.log`), 'a');
    let child;
    try {
      child = spawnImpl(command, args, {
        cwd,
        env,
        detached: false,
        windowsHide: true,
        stdio: ['ignore', logFd, logFd],
      });
    } catch (error) {
      closeSync(logFd);
      throw error;
    }

    child.startupError = null;
    logFds.set(child, logFd);
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

  async function waitForService(url, service, child, label) {
    const deadline = Date.now() + startupTimeoutMs;
    while (Date.now() < deadline) {
      const probe = await probeService(url, service, { fetchImpl, timeoutMs: healthTimeoutMs });
      if (probe.status === 'ready') return;
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

    try {
      child.kill('SIGTERM');
    } catch {
      return;
    }
    if (await waitForExit(child, 3_000)) return;
    try {
      child.kill('SIGKILL');
    } catch {
      return;
    }
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
      for (const child of [...logFds.keys()]) closeLog(child);
    })().finally(() => {
      stopping = undefined;
    });
    return stopping;
  }

  function requireRuntime(resourcesPath, localAppDataPath) {
    if (!resourcesPath) throw new Error('Electron resources path is unavailable.');
    if (!localAppDataPath) throw new Error('LOCALAPPDATA is unavailable.');
    const paths = bundledRuntimePaths(resourcesPath, platform);
    for (const name of ['core', 'gateway', 'tools']) {
      if (!existsSync(paths[name])) {
        throw new Error(`Bundled ${name} runtime is missing: ${paths[name]}`);
      }
    }
    if (platform === 'win32') {
      for (const name of ['gitCmdDir', 'gitBinDir']) {
        if (!existsSync(paths[name])) {
          throw new Error(`Bundled ${name} runtime is missing: ${paths[name]}`);
        }
      }
    }
    const officeRoot = join(localAppDataPath, 'TinadecOffice');
    const dataRoot = join(officeRoot, 'data');
    const logsDir = join(officeRoot, 'logs');
    const workspaceRoot = join(officeRoot, 'workspaces', 'default');
    mkdirSync(dataRoot, { recursive: true });
    mkdirSync(logsDir, { recursive: true });
    mkdirSync(workspaceRoot, { recursive: true });
    return {
      paths,
      dataRoot,
      logsDir,
      workspaceRoot,
      databasePath: join(dataRoot, 'tinadec.db'),
    };
  }

  async function ensureLocalServices({ isPackaged, gatewayUrl, resourcesPath, localAppDataPath }) {
    if (!shouldManageLocalServices(isPackaged, gatewayUrl)) {
      return { started: false, ownsCore: false, ownsGateway: false };
    }
    if (stopping) await stopping;

    const canonicalGatewayUrl = canonicalLocalGatewayUrl(gatewayUrl);
    const coreHealthUrl = `${CORE_URL}/api/v1/health`;
    const gatewayHealthUrl = `${canonicalGatewayUrl}/api/v1/health`;
    const coreProbe = await probeService(coreHealthUrl, 'core', {
      fetchImpl,
      timeoutMs: healthTimeoutMs,
    });
    if (coreProbe.status === 'mismatch') {
      throw new Error(`Tinadec Core endpoint at ${coreHealthUrl} is occupied by an unexpected service.`);
    }
    const gatewayProbe = await probeService(gatewayHealthUrl, 'gateway', {
      fetchImpl,
      timeoutMs: healthTimeoutMs,
    });
    if (gatewayProbe.status === 'mismatch') {
      throw new Error(`Tinadec Gateway endpoint at ${gatewayHealthUrl} is occupied by an unexpected service.`);
    }
    if (coreProbe.status === 'ready' && gatewayProbe.status === 'ready') {
      return {
        started: false,
        ownsCore: ownedChildren.has('core'),
        ownsGateway: ownedChildren.has('gateway'),
      };
    }

    const runtime = requireRuntime(resourcesPath, localAppDataPath);
    const baseEnvironment = buildServiceEnvironment(runtime.paths, environment, platform);
    const startedChildren = [];
    try {
      if (coreProbe.status !== 'ready') {
        const core = startProcess(
          'core',
          runtime.paths.core,
          ['--urls', CORE_URL],
          runtime.paths.coreDir,
          {
            ...baseEnvironment,
            ASPNETCORE_URLS: CORE_URL,
            TinadecPersistence__Enabled: 'true',
            TinadecPersistence__Provider: 'Sqlite',
            TinadecPersistence__DataRoot: runtime.dataRoot,
            TinadecPersistence__Sqlite__DatabasePath: runtime.databasePath,
            TinadecTools__ExecutablePath: runtime.paths.tools,
            TinadecTools__DefaultWorkspaceRoot: runtime.workspaceRoot,
          },
          runtime.logsDir,
        );
        startedChildren.push(['core', core]);
        await waitForService(coreHealthUrl, 'core', core, 'Tinadec Core');
      }

      let currentGatewayProbe = gatewayProbe;
      if (currentGatewayProbe.status !== 'ready') {
        currentGatewayProbe = await probeService(gatewayHealthUrl, 'gateway', {
          fetchImpl,
          timeoutMs: healthTimeoutMs,
        });
        if (currentGatewayProbe.status === 'mismatch') {
          throw new Error(`Tinadec Gateway endpoint at ${gatewayHealthUrl} is occupied by an unexpected service.`);
        }
      }
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
        await waitForService(gatewayHealthUrl, 'gateway', gateway, 'Tinadec Gateway');
      }

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
  probeService,
  shouldManageLocalServices,
  stopLocalServices: serviceManager.stopLocalServices,
};
