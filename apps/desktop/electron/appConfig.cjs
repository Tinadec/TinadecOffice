const fs = require('node:fs');
const path = require('node:path');

const DEFAULT_GATEWAY_URL = 'http://127.0.0.1:48730';

function normalizeGatewayUrl(value) {
  if (typeof value !== 'string' || !value.trim()) {
    throw new Error('Gateway URL is required.');
  }

  let url;
  try {
    url = new URL(value.trim());
  } catch {
    throw new Error('Gateway URL must be a valid HTTP or HTTPS URL.');
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new Error('Gateway URL must use HTTP or HTTPS.');
  }
  if (url.username || url.password || url.search || url.hash) {
    throw new Error('Gateway URL cannot contain credentials, a query, or a fragment.');
  }

  return url.toString().replace(/\/$/, '');
}

function loadAppConfig(configFile, env = process.env) {
  const managedUrl = env.TINADEC_GATEWAY_URL?.trim();
  if (managedUrl) {
    return { gateway_url: normalizeGatewayUrl(managedUrl), source: 'environment', managed: true };
  }

  try {
    const stored = JSON.parse(fs.readFileSync(configFile, 'utf8'));
    return { gateway_url: normalizeGatewayUrl(stored.gateway_url), source: 'user', managed: false };
  } catch {
    return { gateway_url: DEFAULT_GATEWAY_URL, source: 'default', managed: false };
  }
}

function saveGatewayUrl(configFile, value, env = process.env) {
  if (env.TINADEC_GATEWAY_URL?.trim()) {
    throw new Error('Gateway URL is managed by TINADEC_GATEWAY_URL.');
  }

  const gatewayUrl = normalizeGatewayUrl(value);
  fs.mkdirSync(path.dirname(configFile), { recursive: true });
  const temporary = `${configFile}.${process.pid}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify({ gateway_url: gatewayUrl }, null, 2), 'utf8');
    fs.renameSync(temporary, configFile);
  } finally {
    if (fs.existsSync(temporary)) fs.rmSync(temporary, { force: true });
  }
  return { gateway_url: gatewayUrl, source: 'user', managed: false };
}

function resetGatewayUrl(configFile, env = process.env) {
  if (env.TINADEC_GATEWAY_URL?.trim()) {
    return loadAppConfig(configFile, env);
  }
  fs.rmSync(configFile, { force: true });
  return loadAppConfig(configFile, env);
}

module.exports = {
  DEFAULT_GATEWAY_URL,
  loadAppConfig,
  normalizeGatewayUrl,
  resetGatewayUrl,
  saveGatewayUrl,
};
