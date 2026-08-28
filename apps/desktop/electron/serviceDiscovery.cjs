const net = require('node:net');
const os = require('node:os');

const DEFAULT_PORTS = Array.from({ length: 10 }, (_, offset) => 48730 + offset);
const TCP_TIMEOUT_MS = 400;
const HEALTH_TIMEOUT_MS = 2000;

function isPrivateIpv4(ip) {
  if (typeof ip !== 'string') return false;
  const parts = ip.split('.').map(Number);
  if (parts.length !== 4 || parts.some((part) => !Number.isInteger(part) || part < 0 || part > 255)) return false;
  const [a, b] = parts;
  if (a === 10) return true;
  if (a === 192 && b === 168) return true;
  if (a === 172 && b >= 16 && b <= 31) return true;
  return false;
}

function candidateHosts(interfaces = os.networkInterfaces()) {
  const hosts = new Set(['127.0.0.1']);
  for (const entries of Object.values(interfaces)) {
    if (!Array.isArray(entries)) continue;
    for (const entry of entries) {
      if (entry.family !== 'IPv4' || entry.internal) continue;
      if (isPrivateIpv4(entry.address)) hosts.add(entry.address);
    }
  }
  return [...hosts];
}

function classifyHealth(status, payload) {
  if (!payload || typeof payload !== 'object') return null;
  if (payload.gateway === 'ok') {
    return {
      service: 'gateway',
      core_status: payload.core_status === 'unreachable' ? 'unreachable' : 'ready',
      mode: typeof payload.mode === 'string' ? payload.mode : undefined,
      version: typeof payload.version === 'string' ? payload.version : undefined,
    };
  }
  if (payload.name === 'tinadec-core') {
    return {
      service: 'core',
      core_status: 'ready',
      version: typeof payload.version === 'string' ? payload.version : undefined,
    };
  }
  return null;
}

function defaultTcpProbe(host, port, timeoutMs = TCP_TIMEOUT_MS) {
  return new Promise((resolve) => {
    const socket = new net.Socket();
    let settled = false;
    const done = (open) => {
      if (settled) return;
      settled = true;
      socket.destroy();
      resolve(open);
    };
    socket.setTimeout(timeoutMs);
    socket.once('connect', () => done(true));
    socket.once('timeout', () => done(false));
    socket.once('error', () => done(false));
    socket.connect(port, host);
  });
}

async function defaultFetchHealth(url) {
  const response = await fetch(`${url}/api/v1/health`, {
    headers: { accept: 'application/json' },
    signal: AbortSignal.timeout(HEALTH_TIMEOUT_MS),
  });
  let payload = null;
  try {
    payload = await response.json();
  } catch {
    payload = null;
  }
  return { status: response.status, payload };
}

async function discoverServices(options = {}) {
  const hosts = options.hosts ?? candidateHosts();
  const ports = options.ports ?? DEFAULT_PORTS;
  const tcpProbe = options.tcpProbe ?? defaultTcpProbe;
  const fetchHealth = options.fetchHealth ?? defaultFetchHealth;
  const currentGatewayUrl = typeof options.currentGatewayUrl === 'string' ? options.currentGatewayUrl.replace(/\/$/, '') : undefined;

  const candidates = [];
  for (const host of hosts) {
    for (const port of ports) candidates.push({ host, port, url: `http://${host}:${port}` });
  }

  const probes = await Promise.all(
    candidates.map(async (candidate) => {
      const open = await tcpProbe(candidate.host, candidate.port).catch(() => false);
      if (!open) return null;
      let health;
      try {
        health = await fetchHealth(candidate.url);
      } catch {
        return null;
      }
      const classification = classifyHealth(health.status, health.payload);
      if (!classification) return null;
      return {
        url: candidate.url,
        service: classification.service,
        core_status: classification.core_status,
        mode: classification.mode,
        version: classification.version,
        current: candidate.url === currentGatewayUrl,
      };
    })
  );

  const found = probes.filter(Boolean);
  found.sort((left, right) => {
    if (left.service !== right.service) return left.service === 'gateway' ? -1 : 1;
    if (left.current !== right.current) return left.current ? -1 : 1;
    return left.url.localeCompare(right.url);
  });
  return found;
}

module.exports = {
  DEFAULT_PORTS,
  candidateHosts,
  classifyHealth,
  discoverServices,
  isPrivateIpv4,
};
