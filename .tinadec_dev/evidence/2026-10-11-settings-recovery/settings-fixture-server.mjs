import { spawn, spawnSync } from 'node:child_process';
import { createServer } from 'node:http';
import { createHash, randomBytes } from 'node:crypto';
import { mkdir, copyFile, readFile, writeFile } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = resolve(fileURLToPath(new URL('../../../', import.meta.url)));
const evidence = fileURLToPath(new URL('.', import.meta.url));
const scratch = join(root, '.tinadec_dev/tmp/settings-recovery-ui', randomBytes(8).toString('hex'));
const userRoot = join(scratch, 'user');
const ids = ['agents', 'models', 'prompts', 'tools', 'mcp', 'skills', 'storage', 'logging', 'runtime'];
const token = randomBytes(32).toString('base64url');
const children = [];
let ui;
let networkMode = 'initial';
const readAttempts = new Set();
const stats = { network_drops: 0, agent_reads: 0, agent_wire_requests: 0, configuration_or_install_writes: 0, metadata_preview_posts: 0, pack_preview_posts: 0 };
async function hashes() {
  return Promise.all(ids.map(async id => {
    const bytes = await readFile(join(process.env.USERPROFILE, '.tinadec/config', id + '.toml'));
    return { name: id + '.toml', sha256: createHash('sha256').update(bytes).digest('hex') };
  }));
}
async function port() {
  const server = createServer();
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const value = server.address().port;
  await new Promise(resolve => server.close(resolve));
  return value;
}
function start(command, args, cwd, environment) {
  const child = spawn(command, args, { cwd, windowsHide: true, env: { ...process.env, Version: '', 'Ice-Version': '', ...environment }, stdio: ['ignore', 'pipe', 'pipe'] });
  children.push(child);
  child.stdout.on('data', () => {}); child.stderr.on('data', () => {});
  child.on('error', () => {});
  return child;
}
async function healthy(origin, child) {
  for (let i = 0; i < 240; i++) {
    if (child.exitCode !== null) throw new Error('Owned service exited before health.');
    try { if ((await fetch(origin + '/api/v1/health', { signal: AbortSignal.timeout(1500) })).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 500));
  }
  throw new Error('Owned service health timeout.');
}
const before = await hashes();
try {
  await mkdir(join(userRoot, 'config'), { recursive: true });
  await mkdir(join(scratch, 'workspace'), { recursive: true });
  for (const id of ids) await copyFile(join(process.env.USERPROFILE, '.tinadec/config', id + '.toml'), join(userRoot, 'config', id + '.toml'));
  const coreUrl = 'http://127.0.0.1:' + await port();
  const gatewayPort = await port();
  const gatewayUrl = 'http://127.0.0.1:' + gatewayPort;
  const uiPort = await port();
  const environment = { TINADEC_HOST_CONTROL_TOKEN: token, TINADEC_HOME: userRoot, ASPNETCORE_ENVIRONMENT: 'Development', TinadecStorage__Enabled: 'true', TinadecStorage__UserRoot: userRoot, TinadecTools__DefaultWorkspaceRoot: join(scratch, 'workspace') };
  const core = start(join(process.env.ProgramFiles, 'dotnet/dotnet.exe'), ['exec', join(root, '.tinadec_dev/tmp/settings-recovery-build/bin/TinadecCore.Api/debug/TinadecCore.Api.dll')], join(root, 'TinadecCore/Api'), { ...environment, ASPNETCORE_URLS: coreUrl });
  await healthy(coreUrl, core);
  const gateway = start('bun', ['src/index.ts'], join(root, 'TinadecGateway'), { ...environment, TINADEC_CORE_URL: coreUrl, TINADEC_GATEWAY_PORT: String(gatewayPort) });
  await healthy(gatewayUrl, gateway);
  console.log(JSON.stringify({ step: 'owned_core_gateway_ready', isolated: true }));
  const dist = join(root, '.tinadec_dev/tmp/settings-recovery-dist');
  const builtIndex = await readFile(join(dist, 'index.html'), 'utf8');
  const bridge = `<script>(()=>{let status={state:'ready',managed:true};const listeners=new Set();window.tinadec={gatewayUrl:()=>location.origin,getHostStatus:async()=>status,retryHostConnection:async()=>status,onHostStatusChanged:f=>{listeners.add(f);return()=>listeners.delete(f)},getAppConfig:async()=>({}),getUieLayout:async()=>null,saveUieLayout:async()=>{}};window.__settingsFixture={host:state=>{status={state,managed:true};for(const f of listeners)f(status)}};const original=window.fetch.bind(window);let attempt=0;window.fetch=(input,init)=>{if(new URL(input instanceof Request?input.url:String(input),location.origin).pathname==='/api/v1/agents'){const headers=new Headers(init?.headers);headers.set('x-settings-fixture-read-attempt',String(++attempt));init={...init,headers}}return original(input,init)};})()</script>`;
  ui = createServer(async (request, response) => {
          try {
            if (request.url === '/') {
              response.setHeader('content-type', 'text/html');
              response.end(builtIndex.replace('<head>', '<head>' + bridge));
              return;
            }
            if (request.url === '/__settings_control') {
              let mode = ''; for await (const chunk of request) mode += chunk;
              if (mode === 'persistent') networkMode = 'persistent';
              else if (mode === 'healthy') networkMode = 'healthy';
              else assert.equal(mode, 'stats');
              response.setHeader('content-type', 'application/json'); response.end(JSON.stringify(stats)); return;
            }
            if (!request.url?.startsWith('/api/v1/')) {
              const path = resolve(dist, '.' + decodeURIComponent(new URL(request.url, 'http://fixture').pathname));
              if (!path.startsWith(dist + sep)) { response.statusCode = 404; response.end(); return; }
              const extension = path.split('.').at(-1);
              response.setHeader('content-type', ({ js: 'text/javascript', css: 'text/css', json: 'application/json', svg: 'image/svg+xml', png: 'image/png', woff2: 'font/woff2' })[extension] ?? 'application/octet-stream');
              response.end(await readFile(path)); return;
            }
            if (request.method === 'POST' && request.url === '/api/v1/code/tools/project_templates/execute') stats.metadata_preview_posts++;
            else if (request.method === 'POST' && request.url === '/api/v1/agent-packs/install-preview') stats.pack_preview_posts++;
            else if (!['GET', 'HEAD'].includes(request.method)) stats.configuration_or_install_writes++;
            if (request.method === 'GET' && request.url === '/api/v1/agents') {
              stats.agent_wire_requests++;
              const attempt = request.headers['x-settings-fixture-read-attempt'];
              assert.ok(attempt, 'Real renderer fetch must carry its fixture-only attempt marker.');
              readAttempts.add(attempt); stats.agent_reads = readAttempts.size;
              // Chromium may retry an idempotent socket internally. All wire
              // repeats of the first application fetch must fail, so this proves
              // product recovery instead of accepting Chromium's invisible retry.
              if (networkMode === 'persistent' || networkMode === 'initial' && attempt === '1') { stats.network_drops++; response.destroy(); return; }
            }
            const chunks = []; for await (const chunk of request) chunks.push(chunk);
            const headers = { 'x-tinadec-host-control': token, 'x-tinadec-storage-id': request.headers['x-tinadec-storage-id'] ?? 'user' };
            if (request.headers['content-type']) headers['content-type'] = request.headers['content-type'];
            const upstream = await fetch(gatewayUrl + request.url, { method: request.method, headers, ...(!['GET', 'HEAD'].includes(request.method) ? { body: Buffer.concat(chunks) } : {}) });
            response.statusCode = upstream.status;
            for (const key of ['content-type', 'etag']) if (upstream.headers.has(key)) response.setHeader(key, upstream.headers.get(key));
            response.end(Buffer.from(await upstream.arrayBuffer()));
          } catch { response.statusCode = 502; response.end('{"code":"owned_fixture_proxy_failed"}'); }
  });
  await new Promise(resolve => ui.listen(uiPort, '127.0.0.1', resolve));
  const document = await fetch(`http://127.0.0.1:${uiPort}/`);
  assert.equal(document.status, 200); await document.text();
  console.log(JSON.stringify({ step: 'owned_settings_ui_start', isolated: true }));
  const electron = start(join(root, 'node_modules/electron/dist/electron.exe'), [join(evidence, 'settings-electron.cjs')], root, { SETTINGS_UI_URL: `http://127.0.0.1:${uiPort}/#/settings`, SETTINGS_UI_EVIDENCE: evidence });
  electron.stdout.on('data', chunk => process.stdout.write(chunk)); electron.stderr.on('data', chunk => process.stderr.write(chunk));
  const exitCode = await new Promise(resolve => electron.once('exit', resolve));
  const after = await hashes();
  assert.deepEqual(after, before, 'User configuration changed during isolated acceptance.');
  await writeFile(join(evidence, 'desktop-user-config-unchanged.json'), JSON.stringify({ checked_at: new Date().toISOString(), user_config_unchanged: true, database_or_credentials_copied: false, files_before: before, files_after: after }, null, 2) + '\n');
  assert.equal(exitCode, 0, 'Isolated Settings UI acceptance failed.');
} finally {
  if (ui) await new Promise(resolve => ui.close(resolve));
  for (const child of children.reverse()) if (child.exitCode === null) spawnSync('taskkill.exe', ['/pid', String(child.pid), '/t', '/f'], { windowsHide: true, stdio: 'ignore' });
}
