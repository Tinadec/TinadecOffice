// P0 probe — temporary, not committed. Validates the Electron/CDP facts the
// agent-browser plan depends on. Run: npx electron apps/desktop/electron/agentBrowser.probe.cjs

const { app, BrowserWindow, WebContentsView } = require('electron');
const http = require('node:http');

const results = [];
const failures = [];

function record(name, ok, detail, optional = false) {
  const entry = { name, ok, optional, detail };
  results.push(entry);
  if (!ok && !optional) failures.push(entry);
  console.log(`[${ok ? 'PASS' : 'FAIL'}${optional ? ' (optional)' : ''}] ${name}` +
    (detail === undefined ? '' : ` :: ${JSON.stringify(detail)}`));
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const PAGE_A = `<!doctype html><html><body>
<h1>Probe A</h1>
<button id="btn" onclick="document.getElementById('out').textContent='clicked'">Click me</button>
<div id="out">idle</div>
<input id="name" />
<input id="file" type="file" />
</body></html>`;

const PAGE_B = `<!doctype html><html><body><h1>Probe B</h1><p>other page</p></body></html>`;

function startServer() {
  return new Promise((resolve) => {
    const server = http.createServer((req, res) => {
      const body = req.url === '/b' ? PAGE_B : PAGE_A;
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
      res.end(body);
    });
    server.listen(0, '127.0.0.1', () => resolve(server));
  });
}

async function main() {
  const server = await startServer();
  const port = server.address().port;
  const base = `http://127.0.0.1:${port}`;

  const win = new BrowserWindow({
    width: 1000,
    height: 800,
    show: true,
    webPreferences: { sandbox: true, contextIsolation: true, nodeIntegration: false },
  });
  const view = new WebContentsView({
    webPreferences: {
      sandbox: true,
      contextIsolation: true,
      nodeIntegration: false,
      partition: 'persist:tinadec-agent-browser-probe',
    },
  });
  win.contentView.addChildView(view);
  view.setBounds({ x: 0, y: 0, width: 1000, height: 800 });

  const wc = view.webContents;
  let dialog = null;
  wc.debugger.on('message', (_event, method, params) => {
    if (method === 'Page.javascriptDialogOpening') dialog = params;
  });

  async function cdp(method, params = {}, sessionId) {
    return wc.debugger.sendCommand(method, params, sessionId);
  }

  await wc.loadURL(`${base}/a`);
  wc.debugger.attach('1.3');
  await cdp('Page.enable');
  await cdp('DOM.enable');
  await cdp('Runtime.enable');
  await cdp('Accessibility.enable');

  // 1. AX tree → find the button and its backendDOMNodeId.
  let buttonBackendId = null;
  try {
    const tree = await cdp('Accessibility.getFullAXTree');
    const button = (tree.nodes || []).find((node) =>
      node.role?.value === 'button' && String(node.name?.value || '').includes('Click me'));
    buttonBackendId = button?.backendDOMNodeId ?? null;
    record('getFullAXTree exposes backendDOMNodeId', buttonBackendId !== null, {
      nodes: (tree.nodes || []).length,
      backendDOMNodeId: buttonBackendId,
    });
  } catch (error) {
    record('getFullAXTree exposes backendDOMNodeId', false, String(error.message));
  }

  // 2. backendDOMNodeId → frontend nodeId.
  let frontendNodeId = null;
  try {
    await cdp('DOM.getDocument', { depth: 0 });
    const pushed = await cdp('DOM.pushNodesByBackendIdsToFrontend', { backendNodeIds: [buttonBackendId] });
    frontendNodeId = pushed.nodeIds?.[0] ?? null;
    record('pushNodesByBackendIdsToFrontend resolves backend id', frontendNodeId !== null, pushed);
  } catch (error) {
    record('pushNodesByBackendIdsToFrontend resolves backend id', false, String(error.message));
  }

  // 3. getBoxModel accepts backendNodeId.
  let center = null;
  try {
    const box = await cdp('DOM.getBoxModel', { backendNodeId: buttonBackendId });
    const quad = box.model?.content || box.model?.border;
    if (quad && quad.length === 8) {
      center = { x: (quad[0] + quad[2] + quad[4] + quad[6]) / 4, y: (quad[1] + quad[3] + quad[5] + quad[7]) / 4 };
    }
    record('getBoxModel accepts backendNodeId', center !== null, center);
  } catch (error) {
    record('getBoxModel accepts backendNodeId', false, String(error.message));
  }

  // 4. scrollIntoViewIfNeeded accepts backendNodeId.
  try {
    await cdp('DOM.scrollIntoViewIfNeeded', { backendNodeId: buttonBackendId });
    record('scrollIntoViewIfNeeded accepts backendNodeId', true);
  } catch (error) {
    record('scrollIntoViewIfNeeded accepts backendNodeId', false, String(error.message));
  }

  // 5. resolveNode + callFunctionOn (fixed script only).
  try {
    const resolved = await cdp('DOM.resolveNode', { backendNodeId: buttonBackendId });
    const called = await cdp('Runtime.callFunctionOn', {
      objectId: resolved.object.objectId,
      functionDeclaration: 'function(){ return this.tagName + ":" + this.textContent; }',
      returnByValue: true,
    });
    record('resolveNode + callFunctionOn', called.result?.value === 'BUTTON:Click me', called.result?.value);
  } catch (error) {
    record('resolveNode + callFunctionOn', false, String(error.message));
  }

  // 6. Real input events update the page.
  try {
    win.show();
    win.focus();
    await sleep(300);
    const fresh = await cdp('DOM.getBoxModel', { backendNodeId: buttonBackendId });
    const freshQuad = fresh.model?.content || fresh.model?.border;
    const cx = Math.round((freshQuad[0] + freshQuad[2] + freshQuad[4] + freshQuad[6]) / 4);
    const cy = Math.round((freshQuad[1] + freshQuad[3] + freshQuad[5] + freshQuad[7]) / 4);
    const diag = await cdp('Runtime.evaluate', {
      expression: `(() => {
        const b = document.getElementById('btn').getBoundingClientRect();
        const hit = document.elementFromPoint(${cx}, ${cy});
        return {
          focused: document.hasFocus(),
          inner: [innerWidth, innerHeight],
          rect: [b.x, b.y, b.width, b.height],
          hit: hit ? hit.tagName + '#' + hit.id : null,
          dpr: devicePixelRatio
        };
      })()`,
      returnByValue: true,
    });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', x: cx, y: cy, button: 'none' });
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', x: cx, y: cy, button: 'left', buttons: 1, clickCount: 1 });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', x: cx, y: cy, button: 'left', buttons: 0, clickCount: 1 });
    await sleep(250);
    const out = await cdp('Runtime.evaluate', {
      expression: "document.getElementById('out').textContent",
      returnByValue: true,
    });
    record('Input.dispatchMouseEvent drives a real click', out.result?.value === 'clicked', {
      value: out.result?.value,
      point: [cx, cy],
      diag: diag.result?.value,
    });
  } catch (error) {
    record('Input.dispatchMouseEvent drives a real click', false, String(error.message));
  }

  // 6b. Does input require focus? Reset the page, blur, click again.
  async function clickButtonNow(label) {
    const fresh = await cdp('DOM.getBoxModel', { backendNodeId: buttonBackendId });
    const quad = fresh.model?.content || fresh.model?.border;
    if (!quad || quad.length !== 8) throw new Error('box model missing');
    const x = Math.round((quad[0] + quad[2] + quad[4] + quad[6]) / 4);
    const y = Math.round((quad[1] + quad[3] + quad[5] + quad[7]) / 4);
    const focused = await cdp('Runtime.evaluate', { expression: 'document.hasFocus()', returnByValue: true });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, button: 'none' });
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', buttons: 1, clickCount: 1 });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', buttons: 0, clickCount: 1 });
    await sleep(200);
    const out = await cdp('Runtime.evaluate', {
      expression: "document.getElementById('out').textContent",
      returnByValue: true,
    });
    record(label, out.result?.value === 'clicked', { value: out.result?.value, hadFocus: focused.result?.value });
  }
  try {
    await cdp('Runtime.evaluate', { expression: "document.getElementById('out').textContent='idle'" });
    win.blur();
    await sleep(250);
    await clickButtonNow('real click works while the window is not focused');
  } catch (error) {
    record('real click works while the window is not focused', false, String(error.message));
  }
  try {
    await cdp('Runtime.evaluate', { expression: "document.getElementById('out').textContent='idle'" });
    win.hide();
    await sleep(250);
    await clickButtonNow('real click works while the window is hidden');
  } catch (error) {
    record('real click works while the window is hidden', false, String(error.message));
  }
  win.show();

  // 7. Screenshot.
  try {
    const shot = await cdp('Page.captureScreenshot', { format: 'png' });
    record('Page.captureScreenshot returns a PNG', typeof shot.data === 'string' && shot.data.length > 1000, {
      bytes: typeof shot.data === 'string' ? Math.floor((shot.data.length * 3) / 4) : 0,
    });
  } catch (error) {
    record('Page.captureScreenshot returns a PNG', false, String(error.message));
  }

  // 8. JavaScript dialog: event + handle.
  try {
    dialog = null;
    await cdp('Runtime.evaluate', { expression: "setTimeout(() => alert('probe-dialog'), 10); 'scheduled'", returnByValue: true });
    for (let i = 0; i < 30 && !dialog; i += 1) await sleep(50);
    record('Page.javascriptDialogOpening event', dialog?.message === 'probe-dialog', dialog);
    await cdp('Page.handleJavaScriptDialog', { accept: true });
    const after = await cdp('Runtime.evaluate', { expression: '1 + 1', returnByValue: true });
    record('Page.handleJavaScriptDialog unblocks the page', after.result?.value === 2, after.result?.value);
  } catch (error) {
    record('Page.javascriptDialogOpening event', false, String(error.message));
    record('Page.handleJavaScriptDialog unblocks the page', false, String(error.message));
  }

  // 9. Frame identity + navigation invalidation.
  let loaderBefore = null;
  try {
    const tree = await cdp('Page.getFrameTree');
    loaderBefore = tree.frameTree?.frame?.loaderId ?? null;
    record('Page.getFrameTree exposes loaderId', loaderBefore !== null, loaderBefore);
  } catch (error) {
    record('Page.getFrameTree exposes loaderId', false, String(error.message));
  }
  try {
    await cdp('Page.navigate', { url: `${base}/b` });
    for (let i = 0; i < 50; i += 1) {
      const state = await cdp('Runtime.evaluate', {
        expression: '({ ready: document.readyState, href: location.href })',
        returnByValue: true,
      });
      if (state.result?.value?.ready === 'complete' && state.result?.value?.href.includes('/b')) break;
      await sleep(100);
    }
    const tree = await cdp('Page.getFrameTree');
    const loaderAfter = tree.frameTree?.frame?.loaderId ?? null;
    record('navigation changes the document loaderId', loaderAfter !== null && loaderAfter !== loaderBefore, {
      before: loaderBefore,
      after: loaderAfter,
    });
    let oldResolved = false;
    try {
      const resolved = await cdp('DOM.resolveNode', { backendNodeId: buttonBackendId });
      oldResolved = Boolean(resolved.object?.objectId);
    } catch {
      oldResolved = false;
    }
    record('old backendDOMNodeId no longer resolves after navigation', !oldResolved, { oldResolved });
  } catch (error) {
    record('navigation changes the document loaderId', false, String(error.message));
  }

  // 10. File chooser interception.
  try {
    await cdp('Page.navigate', { url: `${base}/a` });
    for (let i = 0; i < 50; i += 1) {
      const state = await cdp('Runtime.evaluate', { expression: 'document.readyState', returnByValue: true });
      if (state.result?.value === 'complete') break;
      await sleep(100);
    }
    await cdp('Page.setInterceptFileChooserDialog', { enabled: true });
    const doc = await cdp('DOM.getDocument', { depth: 0 });
    const fileNode = await cdp('DOM.querySelector', { nodeId: doc.root.nodeId, selector: '#file' });
    const described = await cdp('DOM.describeNode', { nodeId: fileNode.nodeId });
    const fileBackendId = described.node.backendNodeId;
    const box = await cdp('DOM.getBoxModel', { backendNodeId: fileBackendId });
    const quad = box.model.content;
    const x = (quad[0] + quad[2] + quad[4] + quad[6]) / 4;
    const y = (quad[1] + quad[3] + quad[5] + quad[7]) / 4;
    let chooser = null;
    const onChooser = (_event, method, params) => { if (method === 'Page.fileChooserOpened') chooser = params; };
    wc.debugger.on('message', onChooser);
    await cdp('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, button: 'none' });
    await cdp('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', clickCount: 1 });
    await cdp('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', clickCount: 1 });
    for (let i = 0; i < 20 && !chooser; i += 1) await sleep(50);
    wc.debugger.removeListener('message', onChooser);
    record('Page.fileChooserOpened is interceptable', chooser !== null, chooser);
    await cdp('Page.setInterceptFileChooserDialog', { enabled: false });
  } catch (error) {
    record('Page.fileChooserOpened is interceptable', false, String(error.message));
  }

  // 11. Api surface facts.
  record('View.setVisible exists', typeof view.setVisible === 'function', typeof view.setVisible);
  try {
    wc.debugger.attach('1.3');
    record('second debugger.attach fails deterministically', false, 'attach did not throw');
  } catch (error) {
    record('second debugger.attach fails deterministically', true, String(error.message));
  }

  // 12. Typing path: focus + insertText + readback.
  try {
    const doc = await cdp('DOM.getDocument', { depth: 0 });
    const nameNode = await cdp('DOM.querySelector', { nodeId: doc.root.nodeId, selector: '#name' });
    const described = await cdp('DOM.describeNode', { nodeId: nameNode.nodeId });
    await cdp('DOM.focus', { backendNodeId: described.node.backendNodeId });
    await cdp('Input.insertText', { text: 'hello-probe' });
    const value = await cdp('Runtime.evaluate', {
      expression: "document.getElementById('name').value",
      returnByValue: true,
    });
    record('focus + Input.insertText + readback', value.result?.value === 'hello-probe', value.result?.value);
  } catch (error) {
    record('focus + Input.insertText + readback', false, String(error.message));
  }

  try { wc.debugger.detach(); } catch { /* already detached */ }
  win.destroy();
  server.close();

  console.log('\nSUMMARY ' + JSON.stringify({
    total: results.length,
    failed: failures.length,
    failures: failures.map((f) => f.name),
    results,
  }, null, 2));
  app.exit(failures.length === 0 ? 0 : 1);
}

app.on('window-all-closed', () => { /* keep alive until main() finishes */ });

app.whenReady().then(main).catch((error) => {
  console.error('PROBE CRASHED', error);
  app.exit(2);
});

setTimeout(() => {
  console.error('PROBE TIMEOUT');
  app.exit(3);
}, 60_000).unref();
