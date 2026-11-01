// P5 end-to-end probe: real Electron bridge + real TinadecTools child process + real page.
// Run: npx electron .tinadec_dev/evidence/2026-11-02-agent-browser-p5/probe.cjs
const { app, BrowserWindow, WebContentsView } = require('electron');
const http = require('node:http');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');

const repo = path.join(__dirname, '../../..');
const electronDir = path.join(repo, 'apps/desktop/electron');
const { createAgentBrowserManager } = require(path.join(electronDir, 'agentBrowser.cjs'));
const { createBrowserBridge } = require(path.join(electronDir, 'browserBridge.cjs'));
const { stringsForLocale } = require(path.join(electronDir, 'agentBrowserStrings.cjs'));
const toolsExe = path.join(repo, 'TinadecTools/bin/Debug/net10.0/TinadecTools.exe');

const results = [];
function record(name, ok, detail, optional = false) {
  results.push({ name, ok, optional, detail });
  console.log(`[${ok ? 'PASS' : 'FAIL'}${optional ? ' (optional)' : ''}] ${name}` + (detail === undefined ? '' : ` :: ${JSON.stringify(detail)}`));
}
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const fixture = `<!doctype html><html><body>
<h1>Agent Browser E2E</h1>
<button id="btn" onclick="document.getElementById('out').textContent='clicked'">Click me</button>
<div id="out">idle</div>
<label>Name <input id="name" /></label>
<button id="delay" onclick="setTimeout(function(){document.getElementById('out').textContent='delayed-ready';},300)">Delay</button>
<button id="popup" onclick="window.open('https://example.com','_blank')">Popup</button>
<button id="alertBtn" onclick="alert('dialog-e2e')">Alert</button>
<a id="download" href="/download" download="x.txt">Download</a>
<input id="file" type="file" />
<shadow-box></shadow-box>
<script>
class ShadowBox extends HTMLElement {
  constructor() {
    super();
    const root = this.attachShadow({ mode: 'open' });
    root.innerHTML = '<button id="sbtn">Shadow action</button><span id="sout"></span>';
    root.getElementById('sbtn').addEventListener('click', function () { root.getElementById('sout').textContent = 'shadow-clicked'; });
  }
}
customElements.define('shadow-box', ShadowBox);
</script>
</body></html>`;

function startServer() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      if (req.url === '/download') {
        res.writeHead(200, { 'content-type': 'text/plain', 'content-disposition': 'attachment; filename="x.txt"' });
        res.end('download');
        return;
      }
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      res.end(fixture);
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

class ToolClient {
  constructor(exe, cwd, env) {
    this.proc = spawn(exe, [], { cwd, env, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    this.proc.stdout.setEncoding('utf8');
    this.proc.stderr.setEncoding('utf8');
    this.buffer = '';
    this.pending = new Map();
    this.next = 1;
    this.proc.stdout.on('data', (chunk) => this.consume(chunk));
    this.proc.stderr.on('data', () => {});
    this.proc.on('exit', (code) => {
      for (const { reject } of this.pending.values()) reject(new Error(`tools exited ${code}`));
      this.pending.clear();
    });
  }

  consume(chunk) {
    this.buffer += chunk;
    let index;
    while ((index = this.buffer.indexOf('\n')) >= 0) {
      const line = this.buffer.slice(0, index).trim();
      this.buffer = this.buffer.slice(index + 1);
      if (!line) continue;
      let parsed;
      try { parsed = JSON.parse(line); } catch { continue; }
      const pending = this.pending.get(parsed.call_id);
      if (!pending) continue;
      this.pending.delete(parsed.call_id);
      if (parsed.success === false) pending.resolve({ ok: false, code: 'wire_error', message: parsed.error });
      else pending.resolve(parsed.result);
    }
  }

  call(toolId, params, approved = false, timeoutMs = 60_000) {
    const id = this.next++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        if (this.pending.delete(id)) reject(new Error(`tool ${toolId} timed out`));
      }, timeoutMs);
      const done = (value) => { clearTimeout(timer); resolve(value); };
      const fail = (error) => { clearTimeout(timer); reject(error); };
      this.pending.set(id, { resolve: done, reject: fail });
      this.proc.stdin.write(JSON.stringify({ tool_id: toolId, session_id: 'e2e', toolcall_id: id, approved, params }) + '\n');
    });
  }

  stop() { try { this.proc.kill(); } catch { /* gone */ } }
}

function refOf(snapshot, name) {
  if (typeof snapshot !== 'string') return null;
  const target = name.replace(/\s+/g, ' ').trim();
  for (const line of snapshot.split('\n')) {
    const match = line.match(/^(.*?)"(.*?)"(.*)\[ref=(\d+)\]/);
    if (!match) continue;
    if (match[2].replace(/\s+/g, ' ').trim() === target) return Number(match[4]);
  }
  return null;
}

async function main() {
  const server = await startServer();
  const pageUrl = `http://127.0.0.1:${server.address().port}/a`;
  const officeRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-p5-'));
  const workspace = path.join(officeRoot, 'workspace');
  fs.mkdirSync(workspace, { recursive: true });

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
  const bridge = createBrowserBridge({ agentBrowser: manager, officeRoot });
  await bridge.start();
  record('bridge started with a token file', fs.existsSync(bridge.info().bridgeFile));

  const tools = new ToolClient(toolsExe, workspace, { ...process.env, TINADEC_HOME: officeRoot });
  try {
    const manifest = await tools.call('#manifest', {});
    record('real tools process advertises the browser surface',
      Array.isArray(manifest.tools) && manifest.tools.some((tool) => tool.id === 'browser_navigate'),
      { tools: manifest.tools?.length });

    const navigated = await tools.call('browser_navigate', { url: pageUrl, wait_ms: 5000 }, true);
    record('browser_navigate opens the real window and loads the page', navigated.ok === true && navigated.ready === true, navigated);
    const tabId = navigated.tab_id;

    const first = await tools.call('browser_snapshot', {});
    const buttonRef = refOf(first.snapshot, 'Click me');
    const inputRef = refOf(first.snapshot, 'Name');
    record('snapshot exposes refs for the button and the input', buttonRef !== null && inputRef !== null,
      { buttonRef, inputRef, snapshot: first.snapshot?.slice(0, 500) });

    const clicked = await tools.call('browser_click', { ref: buttonRef }, true);
    record('browser_click reports success', clicked.ok === true, clicked);
    const afterClick = await tools.call('browser_snapshot', {});
    record('real click updated the page', afterClick.snapshot?.includes('clicked'), { snapshot: afterClick.snapshot?.slice(0, 500) });

    const typed = await tools.call('browser_type', { ref: inputRef, text: 'hello-e2e' }, true);
    record('browser_type verifies the typed value', typed.ok === true && typed.verified === true && typed.value === 'hello-e2e', typed);
    const afterType = await tools.call('browser_snapshot', {});
    record('snapshot shows the typed value', afterType.snapshot?.includes('hello-e2e'), { snapshot: afterType.snapshot?.slice(0, 500) });

    const delayRef = refOf(afterType.snapshot, 'Delay');
    if (delayRef !== null) {
      await tools.call('browser_click', { ref: delayRef }, true);
      const waited = await tools.call('browser_wait', { text: 'delayed-ready', timeout_ms: 5000 });
      record('browser_wait observes a delayed DOM update', waited.ok === true, waited);
    } else {
      record('browser_wait observes a delayed DOM update', false, 'Delay ref missing', true);
    }

    const batch = await tools.call('browser_batch', {
      steps: [
        { tool: 'browser_click', args: { ref: buttonRef } },
        { tool: 'browser_type', args: { ref: inputRef, text: 'batch-text' } },
      ],
    }, true);
    record('browser_batch runs both steps and returns fresh refs',
      batch.ok === true && batch.steps?.length === 2 && batch.steps.every((step) => step.ok) && batch.snapshot?.includes('batch-text'),
      { steps: batch.steps, snapshot: batch.snapshot?.slice(0, 400) });

    const shot = await tools.call('browser_screenshot', { full_page: false });
    record('browser_screenshot writes a PNG', shot.ok === true && fs.existsSync(shot.path), { path: shot.path, bytes: shot.bytes });

    const tabs = await tools.call('browser_tabs_list', {});
    record('browser_tabs_list reports the active tab', tabs.ok === true && tabs.tabs?.some((tab) => tab.id === tabId && tab.active), tabs);

    const blocked = await tools.call('browser_navigate', { url: 'javascript:alert(1)' }, true);
    record('javascript: navigation is refused', blocked.ok === false && blocked.code === 'browser_navigation_blocked', blocked);

    const shadowRef = refOf(afterType.snapshot, 'Shadow action');
    if (shadowRef !== null) {
      const shadow = await tools.call('browser_click', { ref: shadowRef }, true);
      const shadowSnapshot = await tools.call('browser_snapshot', {});
      record('open shadow-root button click is dispatched', shadow.ok === true && shadowSnapshot.snapshot?.includes('shadow-clicked'),
        { click: shadow, snapshot: shadowSnapshot.snapshot?.slice(0, 600) });
    } else {
      record('open shadow-root button click is dispatched', false, 'Shadow action ref missing from AX tree', true);
    }

    const popupRef = refOf(afterType.snapshot, 'Popup');
    if (popupRef !== null) {
      const popup = await tools.call('browser_click', { ref: popupRef }, true);
      record('popup is blocked and reported as a notice', popup.ok === true && Array.isArray(popup.notices) && popup.notices.some((notice) => notice.code === 'browser_popup_blocked'), popup);
    } else {
      record('popup is blocked and reported as a notice', false, 'Popup ref missing', true);
    }

    const downloadRef = refOf(afterType.snapshot, 'Download');
    if (downloadRef !== null) {
      const download = await tools.call('browser_click', { ref: downloadRef }, true);
      await sleep(300);
      const afterDownload = await tools.call('browser_snapshot', {});
      record('download is a hard stop',
        (download.code === 'browser_download_started') || (afterDownload.ok === false && afterDownload.code === 'browser_download_started'),
        { click: download, next: afterDownload });
    } else {
      record('download is a hard stop', false, 'Download ref missing', true);
    }

    const fileRef = refOf(afterType.snapshot, '选择文件') ?? refOf(afterType.snapshot, 'Choose File');
    if (fileRef !== null) {
      await tools.call('browser_click', { ref: fileRef }, true);
      const afterFile = await tools.call('browser_snapshot', {});
      record('file chooser is a hard stop', afterFile.ok === false && afterFile.code === 'browser_file_chooser_opened', afterFile);
    } else {
      record('file chooser is a hard stop', false, 'file input ref missing', true);
    }

    const alertRef = refOf(afterType.snapshot, 'Alert');
    if (alertRef !== null) {
      await tools.call('browser_click', { ref: alertRef }, true);
      const blockedByDialog = await tools.call('browser_snapshot', {});
      record('JavaScript dialog pauses the agent', blockedByDialog.ok === false && blockedByDialog.code === 'browser_dialog_open', blockedByDialog);
      await manager.handleDialog('e2e', true);
      const afterDialog = await tools.call('browser_snapshot', {});
      record('handling the dialog lets the next snapshot succeed', afterDialog.ok === true, afterDialog);
    } else {
      record('JavaScript dialog pauses the agent', false, 'Alert ref missing', true);
    }

    manager.setControl('e2e', 'user');
    const controlled = await tools.call('browser_snapshot', {});
    record('user takeover hard-stops the tool surface', controlled.ok === false && controlled.code === 'browser_user_in_control', controlled);
    manager.setControl('e2e', 'agent');

    const resumed = await tools.call('browser_snapshot', {});
    record('handing control back resumes snapshots', resumed.ok === true, resumed);
  } finally {
    tools.stop();
    bridge.stopSync();
    manager.closeAll();
    try { server.closeAllConnections?.(); } catch { /* not available */ }
    server.close();
    await sleep(200);
    try { fs.rmSync(officeRoot, { recursive: true, force: true }); } catch { /* windows lock */ }
  }

  const failed = results.filter((entry) => !entry.ok && !entry.optional);
  console.log(`\nSUMMARY ${JSON.stringify({ total: results.length, failed: failed.length, optionalFailed: results.filter((entry) => !entry.ok && entry.optional).map((entry) => entry.name), failures: failed.map((entry) => entry.name) })}`);
  const exitCode = failed.length === 0 ? 0 : 1;
  app.exit(exitCode);
  setTimeout(() => process.exit(exitCode), 500).unref();
}

app.on('window-all-closed', () => { /* keep the probe alive */ });
app.whenReady().then(main).catch((error) => {
  console.error('P5 PROBE CRASHED', error);
  app.exit(2);
});
setTimeout(() => { console.error('P5 PROBE TIMEOUT'); process.exit(3); }, 120_000).unref();
