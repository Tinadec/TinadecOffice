import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import { createHash, randomBytes } from 'node:crypto';
import { mkdir, readFile, writeFile, copyFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parse } from '../../../node_modules/smol-toml/dist/index.js';

const root = resolve(fileURLToPath(new URL('../../../', import.meta.url)));
const evidence = fileURLToPath(new URL('.', import.meta.url));
const phase = process.argv[2] === 'after' ? 'after' : 'before';
const scratch = join(root, '.tinadec_dev/tmp/settings-recovery', randomBytes(8).toString('hex'));
const userRoot = join(scratch, 'user');
const ids = ['agents', 'models', 'prompts', 'tools', 'mcp', 'skills', 'storage', 'logging', 'runtime'];
const children = [];
const token = randomBytes(32).toString('base64url');
async function hashes() {
  return Promise.all(ids.map(async id => {
    const bytes = await readFile(join(process.env.USERPROFILE, '.tinadec/config', id + '.toml'));
    return { name: id + '.toml', bytes: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
  }));
}
async function port() {
  const server = createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const port = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return port;
}
function start(command, args, cwd, env) {
  const child = spawn(command, args, { cwd, windowsHide: true, env: { ...process.env, Version: '', 'Ice-Version': '', ...env }, stdio: ['ignore', 'pipe', 'pipe'] });
  const chunks = [];
  child.stdout.on('data', chunk => chunks.push(chunk));
  child.stderr.on('data', chunk => chunks.push(chunk));
  child.safeStatus = () => Buffer.concat(chunks).toString().split(/\r?\n/).filter(line => /Now listening|Application started|Unhandled exception|Exception:|error:|fail:/.test(line)).map(line => line.replaceAll(token, '[private]')).slice(-6);
  children.push(child);
  child.on('error', () => {});
  return child;
}
async function waitFor(url, child) {
  for (let attempt = 0; attempt < 180; attempt++) {
    if (child.exitCode !== null) throw new Error('Owned service exited before becoming healthy.');
    try { if ((await fetch(url + '/api/v1/health', { signal: AbortSignal.timeout(1000) })).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  console.log(JSON.stringify({ step: 'owned_service_timeout', status: child.safeStatus() }));
  throw new Error('Owned service health timeout.');
}
const before = await hashes();
try {
  await mkdir(join(userRoot, 'config'), { recursive: true });
  await mkdir(join(scratch, 'workspace'), { recursive: true });
  for (const id of ids) await copyFile(join(process.env.USERPROFILE, '.tinadec/config', id + '.toml'), join(userRoot, 'config', id + '.toml'));
  const agents = parse(await readFile(join(userRoot, 'config/agents.toml'), 'utf8'));
  const counts = Object.fromEntries(Object.entries(agents).filter(([, value]) => Array.isArray(value)).map(([key, value]) => [key, value.length]));
  const states = {};
  for (const row of agents.agent_definitions ?? []) states[row.status ?? 'unset'] = (states[row.status ?? 'unset'] ?? 0) + 1;
  await writeFile(join(evidence, phase + '-config-inventory.json'), JSON.stringify({ checked_at: new Date().toISOString(), files: before, table_counts: counts, agent_status_counts: states }, null, 2) + '\n');
  console.log(JSON.stringify({ step: 'config_inventory', table_counts: counts, agent_status_counts: states }));
  const coreUrl = 'http://127.0.0.1:' + await port();
  const gatewayPort = await port();
  const gatewayUrl = 'http://127.0.0.1:' + gatewayPort;
  const env = { TINADEC_HOST_CONTROL_TOKEN: token, TINADEC_HOME: userRoot, ASPNETCORE_ENVIRONMENT: 'Development', TinadecStorage__Enabled: 'true', TinadecStorage__UserRoot: userRoot, TinadecTools__DefaultWorkspaceRoot: join(scratch, 'workspace') };
  const buildRoot = phase === 'after' ? 'settings-recovery-build' : 'workspace-api-audit';
  const core = start(join(process.env.ProgramFiles, 'dotnet/dotnet.exe'), ['exec', join(root, '.tinadec_dev/tmp', buildRoot, 'bin/TinadecCore.Api/debug/TinadecCore.Api.dll')], join(root, 'TinadecCore/Api'), { ...env, ASPNETCORE_URLS: coreUrl });
  await waitFor(coreUrl, core);
  const gateway = start('bun', ['src/index.ts'], join(root, 'TinadecGateway'), { ...env, TINADEC_CORE_URL: coreUrl, TINADEC_GATEWAY_PORT: String(gatewayPort) });
  await waitFor(gatewayUrl, gateway);
  const reads = [];
  for (const [service, origin] of [['core', coreUrl], ['gateway', gatewayUrl]]) {
    for (const path of ['/api/v1/agents', '/api/v1/agent-modes', '/api/v1/agent-packs']) {
      const start = Date.now();
      console.log(JSON.stringify({ step: 'reading', service, path }));
      let response;
      try { response = await fetch(origin + path, { headers: { 'x-tinadec-host-control': token, 'x-tinadec-storage-id': 'user' }, signal: AbortSignal.timeout(90000) }); }
      catch (error) { reads.push({ service, path, elapsed_ms: Date.now() - start, error_name: error.name }); continue; }
      const data = await response.json();
      reads.push({ service, path, status: response.status, elapsed_ms: Date.now() - start, count: Array.isArray(data) ? data.length : null,
        ...(response.ok ? {} : { code: data.code, category: data.category, trace_id: data.trace_id }) });
      if (path === '/api/v1/agents' && response.ok && service === 'gateway') {
        let passed = 0;
        for (const agent of data) {
          const detail = await fetch(origin + path + '/' + encodeURIComponent(agent.id), { headers: { 'x-tinadec-host-control': token, 'x-tinadec-storage-id': 'user' }, signal: AbortSignal.timeout(30000) });
          if (detail.ok) passed++;
          await detail.arrayBuffer();
        }
        reads.push({ service, path: '/api/v1/agents/{id}', sequential_reads: data.length, successful: passed });
      }
    }
  }
  const after = await hashes();
  const result = { checked_at: new Date().toISOString(), exact_current_user_toml_copy: true, copied_database_or_credentials: false, user_config_unchanged: JSON.stringify(before) === JSON.stringify(after), files_before: before, files_after: after, table_counts: counts, agent_status_counts: states,
    pack_status_counts: (agents.agent_pack_installations ?? []).reduce((acc, item) => { const status = item.status ?? 'unset'; acc[status] = (acc[status] ?? 0) + 1; return acc; }, {}), reads };
  await writeFile(join(evidence, phase + '-current-copy-read.json'), JSON.stringify(result, null, 2) + '\n');
  console.log(JSON.stringify({ user_config_unchanged: result.user_config_unchanged, table_counts: counts, agent_status_counts: states, reads }, null, 2));
} finally {
  for (const child of children.reverse()) if (child.exitCode === null) spawnSync('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
}
