const assert = require('node:assert/strict');
const { EventEmitter } = require('node:events');
const test = require('node:test');
const { createTrustedHostRequests, isManagedApiUrl } = require('./trustedHostRequests.cjs');

const token = 'private-main-process-credential-with-43-characters';
function hostFixture(options = {}) {
  const controller = createTrustedHostRequests({ token, ...options });
  const listeners = [];
  const session = { webRequest: { onBeforeSendHeaders: (filter, listener) => listeners.push({ filter, listener }) } };
  const contents = new EventEmitter();
  contents.id = 42;
  contents.url = 'app://bundle/index.html#/settings';
  contents.destroyed = false;
  contents.getURL = () => contents.url;
  contents.isDestroyed = () => contents.destroyed;
  contents.mainFrame = { processId: 5, routingId: 9, parent: null, url: contents.url };
  contents.session = session;
  controller.registerWindow({ webContents: contents });
  const send = (changes = {}) => {
    let result;
    controller.beforeSendHeaders({ url: 'http://127.0.0.1:48730/api/v1/sessions', resourceType: 'xhr',
      webContentsId: contents.id, frame: contents.mainFrame, requestHeaders: { accept: 'application/json' }, ...changes }, value => { result = value.requestHeaders; });
    return result;
  };
  return { controller, contents, listeners, send };
}

test('only fixed local Core/Gateway API URLs receive a host credential', () => {
  for (const value of ['http://127.0.0.1:48730/api/v1/sessions', 'http://127.0.0.1:48731/api/v1/storage/scopes/open'])
    assert.equal(isManagedApiUrl(value), true);
  for (const value of ['https://127.0.0.1:48730/api/v1/sessions', 'http://127.0.0.2:48730/api/v1/sessions',
    'http://[::1]:48730/api/v1/sessions', 'http://localhost:48730/api/v1/sessions', 'http://localhost:48731/api/v1/storage/scopes/open',
    'http://127.0.0.1:48732/api/v1/sessions', 'http://localhost:48730/docs',
    'http://localhost:48730/api/v10/sessions', 'http://user:pass@localhost:48730/api/v1/sessions',
    'http://localhost.example.com:48730/api/v1/sessions', 'https://gateway.example.com/api/v1/sessions', 'invalid'])
    assert.equal(isManagedApiUrl(value), false, value);
});

test('an isolated host may declare its own loopback API ports', () => {
  const previous = process.env.TINADEC_MANAGED_API_PORTS;
  process.env.TINADEC_MANAGED_API_PORTS = '48740, 48741';
  try {
    assert.equal(isManagedApiUrl('http://127.0.0.1:48740/api/v1/events'), true);
    assert.equal(isManagedApiUrl('http://127.0.0.1:48741/api/v1/health'), true);
    assert.equal(isManagedApiUrl('http://127.0.0.1:48730/api/v1/events'), false);
  } finally {
    if (previous === undefined) delete process.env.TINADEC_MANAGED_API_PORTS;
    else process.env.TINADEC_MANAGED_API_PORTS = previous;
  }
});

test('trusted registered main frame is signed for HTTP and SSE without mutating renderer headers', () => {
  const { send } = hostFixture();
  const requestHeaders = { accept: 'text/event-stream', 'x-tinadec-storage-id': 'project', 'x-tinadec-host-control': 'forged' };
  const headers = send({ resourceType: 'other', requestHeaders });
  assert.equal(headers['X-Tinadec-Host-Control'], token);
  assert.equal(headers['x-tinadec-host-control'], undefined);
  assert.equal(headers['x-tinadec-storage-id'], 'project');
  assert.equal(requestHeaders['x-tinadec-host-control'], 'forged');
});

test('unregistered windows, workers and child frames never inherit the trusted main frame credential', () => {
  const { send, contents } = hostFixture();
  for (const changes of [{ webContentsId: 99 }, { webContentsId: undefined }, { frame: null },
    { frame: { ...contents.mainFrame, routingId: 10, parent: contents.mainFrame, url: 'https://preview.example.com/' } },
    { frame: { ...contents.mainFrame, routingId: 10, parent: contents.mainFrame } },
    { frame: { ...contents.mainFrame, processId: 999 } }, { resourceType: 'mainFrame' }, { resourceType: 'subFrame' }])
    assert.equal(send(changes)['X-Tinadec-Host-Control'], undefined);
});

test('navigation suspends signing until the trusted document commits and remote pages stay unsigned', () => {
  const { send, contents } = hostFixture();
  contents.emit('did-start-navigation', { isMainFrame: true, isInPlace: false });
  assert.equal(send()['X-Tinadec-Host-Control'], undefined);
  contents.url = 'https://preview.example.com/'; contents.mainFrame.url = contents.url;
  contents.emit('did-navigate', {}, contents.url);
  assert.equal(send()['X-Tinadec-Host-Control'], undefined);
  contents.url = 'app://bundle/index.html#/home'; contents.mainFrame.url = contents.url;
  contents.emit('did-navigate', {}, contents.url);
  assert.equal(send()['X-Tinadec-Host-Control'], token);
  contents.emit('did-start-navigation', { isMainFrame: true, isInPlace: true });
  assert.equal(send()['X-Tinadec-Host-Control'], token);
});

test('live frame URL is rechecked even if navigation events have not arrived', () => {
  const { send, contents } = hostFixture();
  contents.mainFrame.url = 'https://preview.example.com/';
  assert.equal(send()['X-Tinadec-Host-Control'], undefined);
});

test('foreign destinations and redirects lose any supplied private header', () => {
  const { send } = hostFixture();
  for (const url of ['https://preview.example.com/api/v1/sessions', 'http://localhost:48730/api/v1/sessions']) {
    const headers = send({ url, requestHeaders: { 'X-TINADEC-HOST-CONTROL': token, accept: 'application/json' } });
    assert.deepEqual(headers, { accept: 'application/json' });
  }
});

test('destroyed windows, disposed frames and disabled managed host fail closed', () => {
  const { send, contents } = hostFixture();
  contents.destroyed = true;
  assert.equal(send()['X-Tinadec-Host-Control'], undefined);
  contents.destroyed = false; contents.emit('destroyed');
  assert.equal(send()['X-Tinadec-Host-Control'], undefined);
  assert.equal(hostFixture({ isManagedHost: () => false }).send()['X-Tinadec-Host-Control'], undefined);
  const disposed = hostFixture();
  Object.defineProperty(disposed.contents.mainFrame, 'url', { get: () => { throw new Error('Frame disposed'); } });
  assert.equal(disposed.send()['X-Tinadec-Host-Control'], undefined);
});

test('the interceptor is installed once per session and catches redirect destinations', () => {
  const { controller, contents, listeners } = hostFixture();
  controller.registerWindow({ webContents: contents });
  const second = Object.assign(new EventEmitter(), contents, { id: 43 });
  controller.registerWindow({ webContents: second });
  assert.equal(listeners.length, 1);
  assert.deepEqual(listeners[0].filter.urls, ['http://*/*', 'https://*/*', 'ws://*/*', 'wss://*/*']);
});

test('only the packaged entry or the fixed local dev entry can be trusted', () => {
  const { controller } = hostFixture({ devServerUrl: 'http://127.0.0.1:5173/' });
  for (const value of ['app://bundle/index.html?x=1#/settings', 'http://127.0.0.1:5173/?splash=0#/debug-studio', 'http://127.0.0.1:5173/index.html'])
    assert.equal(controller.isTrustedDocument(value), true, value);
  for (const value of ['app://bundle/preview.html', 'app://other/index.html', 'app://user@bundle/index.html',
    'http://127.0.0.1:5173/preview.html', 'http://localhost:5173/', 'https://127.0.0.1:5173/', 'about:blank', 'file:///C:/index.html'])
    assert.equal(controller.isTrustedDocument(value), false, value);
  assert.equal(hostFixture({ devServerUrl: 'https://remote.example.com/' }).controller.isTrustedDocument('https://remote.example.com/'), false);
});

test('host IPC requires the same registered trusted main frame and loses authority on navigation', () => {
  const { controller, contents } = hostFixture();
  const event = { sender: contents, senderFrame: contents.mainFrame };
  assert.equal(controller.isTrustedSender(event), true);
  assert.equal(controller.isTrustedSender({ ...event, senderFrame: { ...contents.mainFrame, routingId: 10, parent: contents.mainFrame } }), false);
  assert.equal(controller.isTrustedSender({ sender: { ...contents }, senderFrame: contents.mainFrame }), false);
  contents.emit('did-start-navigation', { isMainFrame: true, isInPlace: false });
  assert.equal(controller.isTrustedSender(event), false);
  contents.url = 'app://bundle/preview.html'; contents.mainFrame.url = contents.url;
  contents.emit('did-navigate', {}, contents.url);
  assert.equal(controller.isTrustedSender(event), false);
});

 test('status and retry can identify a trusted document while business signing is revoked', () => {
  const { controller, contents, send } = hostFixture({ isManagedHost: () => false });
  const event = { sender: contents, senderFrame: contents.mainFrame };
  assert.equal(controller.isTrustedDocumentSender(event), true);
  assert.equal(controller.isTrustedSender(event), false);
  assert.equal(send()['X-Tinadec-Host-Control'], undefined);
  assert.equal(controller.isTrustedDocumentSender({ ...event, senderFrame: { ...contents.mainFrame, routingId: 10, parent: contents.mainFrame } }), false);
  contents.emit('did-start-navigation', { isMainFrame: true, isInPlace: false });
  assert.equal(controller.isTrustedDocumentSender(event), false);
});
