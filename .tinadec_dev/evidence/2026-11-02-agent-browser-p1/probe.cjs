// P1 integration probe — real Electron, real WebContentsView, real bridge, real HTTP page.
// Run: npx electron .tinadec_dev/evidence/2026-11-02-agent-browser-p1/probe.cjs
const { app, BrowserWindow, WebContentsView } = require('electron');
const http = require('node:http');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const electronDir = path.join(__dirname, '../../../apps/desktop/electron');
const { createAgentBrowserManager } = require(path.join(electronDir, 'agentBrowser.cjs'));
const { createBrowserBridge } = require(path.join(electronDir, 'browserBridge.cjs'));
const { stringsForLocale } = require(path.join(electronDir, 'agentBrowserStrings.cjs'));

const results = [];
const logFile = path.join(__dirname, 'probe.log');
fs.writeFileSync(logFile, '');
function mark(label) {
  fs.appendFileSync(logFile, `${new Date().toISOString()} ${label}\n`);
  console.log(`[mark] ${label}`);
}
function record(name, ok, detail) {
  results.push({ name, ok, detail });
  console.log(`[${ok ? 'PASS' : 'FAIL'}] ${name}${detail === undefined ? '' : ` :: ${JSON.stringify(detail)}`}`);
}

const PAGE = `<!doctype html><html><body>
<h1>Bridge Probe</h1>
<button id="btn" onclick="document.getElementById('out').textContent='clicked'">Click me</button>
<div id="out">idle</div>
</body></html>`;

function startServer() {
  return new Promise((resolve) => {
    const server = http.createServer((_req, res) => {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      res.end(PAGE);
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

async function post(info, body, timeoutMs = 10_000) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(new Error('bridge post timeout')), timeoutMs);
  try {
    const response = await fetch(`${info.url}/`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: `Bearer ${info.token}` },
      body: JSON.stringify(body),
      signal: controller.signal,
    });
    return { status: response.status, json: await response.json() };
  } finally {
    clearTimeout(timer);
  }
}

async function main() {
  mark('main start');
  const server = await startServer();
  mark('page server ready');
  const port = server.address().port;
  const pageUrl = `http://127.0.0.1:${port}/a`;
  const officeRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-p1-probe-'));

  const manager = createAgentBrowserManager({
    BrowserWindow,
    WebContentsView,
    shell: { openExternal: async () => {} },
    app,
    shellUrl: () => 'about:blank',
    preloadPath: path.join(electronDir, 'agentBrowserPreload.cjs'),
    stateDir: path.join(officeRoot, 'state', 'agent-browser'),
    stringsForLocale,
  });
  mark('manager created');
  const bridge = createBrowserBridge({ agentBrowser: manager, officeRoot });
  const info = await bridge.start();
  mark('bridge started');
  record('bridge started and wrote a token file', fs.existsSync(info.bridgeFile), { url: info.url });

  const opened = await post(info, { op: 'tabs/open', session_id: 'it', url: pageUrl });
  mark('tabs/open returned');
  record('tabs/open creates a real window and navigates', opened.json.ok === true && opened.json.data?.tab?.url === pageUrl, opened.json.data?.tab);
  const tabId = opened.json.data?.tab?.id;
  mark(`tab id ${tabId}`);

  let ready = false;
  for (let i = 0; i < 40; i += 1) {
    const state = await post(info, { op: 'cdp', session_id: 'it', tab_id: tabId, method: 'Runtime.evaluate', params: { expression: 'document.readyState', returnByValue: true } });
    if (state.json?.data?.result?.result?.value === 'complete') { ready = true; break; }
    await new Promise((resolve) => setTimeout(resolve, 100));
  }
  mark('document ready');
  record('Page.navigate through the bridge reaches a complete document', ready);

  const ax = await post(info, { op: 'cdp', session_id: 'it', tab_id: tabId, method: 'Accessibility.getFullAXTree', params: {} });
  const button = (ax.json?.data?.result?.nodes ?? []).find((node) => node.role?.value === 'button');
  record('Accessibility snapshot crosses the bridge', Boolean(button?.backendDOMNodeId), { backendDOMNodeId: button?.backendDOMNodeId });

  const box = await post(info, { op: 'cdp', session_id: 'it', tab_id: tabId, method: 'DOM.getBoxModel', params: { backendNodeId: button.backendDOMNodeId } });
  const quad = box.json?.data?.result?.model?.content;
  record('DOM.getBoxModel crosses the bridge', Array.isArray(quad) && quad.length === 8);
  const x = Math.round((quad[0] + quad[2] + quad[4] + quad[6]) / 4);
  const y = Math.round((quad[1] + quad[3] + quad[5] + quad[7]) / 4);
  for (const params of [
    { type: 'mouseMoved', x, y, button: 'none' },
    { type: 'mousePressed', x, y, button: 'left', buttons: 1, clickCount: 1 },
    { type: 'mouseReleased', x, y, button: 'left', buttons: 0, clickCount: 1 },
  ]) {
    await post(info, { op: 'cdp', session_id: 'it', tab_id: tabId, method: 'Input.dispatchMouseEvent', params });
  }
  await new Promise((resolve) => setTimeout(resolve, 200));
  const out = await post(info, { op: 'cdp', session_id: 'it', tab_id: tabId, method: 'Runtime.evaluate', params: { expression: "document.getElementById('out').textContent", returnByValue: true } });
  record('real input through the bridge updates the page', out.json?.data?.result?.result?.value === 'clicked', out.json?.data?.result?.result?.value);

  const shot = await post(info, { op: 'screenshot', session_id: 'it', tab_id: tabId });
  record('screenshot is written to disk', shot.json.ok === true && fs.existsSync(shot.json.data?.path), shot.json.data);

  const state = await post(info, { op: 'state', session_id: 'it' });
  record('state reports one active tab and agent control', state.json.data?.tabs?.length === 1 && state.json.data?.control === 'agent', state.json.data);

  const denied = await post(info, { op: 'cdp', session_id: 'it', tab_id: tabId, method: 'Browser.getVersion', params: {} });
  record('non-allowlisted method is refused', denied.json.ok === false && denied.json.code === 'browser_method_denied', denied.json.code);

  await manager.setControl('it', 'user');
  const blocked = await post(info, { op: 'cdp', session_id: 'it', tab_id: tabId, method: 'Page.reload', params: {} });
  record('user takeover hard-stops the agent bridge', blocked.json.code === 'browser_user_in_control', blocked.json.code);

  bridge.stopSync();
  manager.closeAll();
  server.close();
  fs.rmSync(officeRoot, { recursive: true, force: true });
  const failed = results.filter((entry) => !entry.ok);
  console.log(`\nSUMMARY ${JSON.stringify({ total: results.length, failed: failed.length, failures: failed.map((entry) => entry.name) })}`);
  app.exit(failed.length === 0 ? 0 : 1);
}

app.on('window-all-closed', () => { /* keep the probe alive */ });
app.whenReady().then(() => { mark('app ready'); return main(); }).catch((error) => {
  mark(`CRASH ${error?.stack ?? error}`);
  console.error('P1 PROBE CRASHED', error);
  app.exit(2);
});
setTimeout(() => {
  mark('PROBE TIMEOUT');
  console.error('P1 PROBE TIMEOUT');
  process.exit(3);
}, 40_000).unref();
