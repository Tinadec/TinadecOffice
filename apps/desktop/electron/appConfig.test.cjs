const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const { parse } = require('smol-toml');
const {
  DEFAULT_GATEWAY_URL,
  loadAppConfig,
  normalizeGatewayUrl,
  resetGatewayUrl,
  saveGatewayUrl,
  saveDebugStudioEnabled,
} = require('./appConfig.cjs');

test('normalizes and validates Gateway URLs', () => {
  assert.equal(normalizeGatewayUrl(' https://office.example.com/ '), 'https://office.example.com');
  assert.throws(() => normalizeGatewayUrl('file:///tmp/gateway'), /HTTP or HTTPS/);
  assert.throws(() => normalizeGatewayUrl('https://user@example.com'), /credentials/);
});

function temporaryConfig(run) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-debug-config-'));
  try { return run(path.join(root, 'desktop.toml')); }
  finally { fs.rmSync(root, { recursive: true, force: true }); }
}

test('Debug Studio defaults off and rejects nonboolean preferences, including managed Gateway mode', () => {
  temporaryConfig((file) => {
    const env = { TINADEC_GATEWAY_URL: 'https://managed.example.com' };
    assert.equal(loadAppConfig(file, {}).debug_studio_enabled, false);
    assert.equal(loadAppConfig(file, env).debug_studio_enabled, false);
    fs.writeFileSync(file, '[developer]\ndebug_studio_enabled = true\n');
    assert.equal(loadAppConfig(file, {}).debug_studio_enabled, true);
    const managed = loadAppConfig(file, env);
    assert.equal(managed.gateway_url, 'https://managed.example.com');
    assert.equal(managed.source, 'environment');
    assert.equal(managed.managed, true);
    assert.equal(managed.debug_studio_enabled, true);
    for (const value of ['"true"', '1', '[true]', '{ value = true }']) {
      fs.writeFileSync(file, `[developer]\ndebug_studio_enabled = ${value}\n`);
      assert.throws(() => loadAppConfig(file, {}), /debug_studio_enabled must be a boolean/);
      assert.throws(() => loadAppConfig(file, env), /debug_studio_enabled must be a boolean/);
    }
    fs.writeFileSync(file, 'developer = false\n');
    assert.throws(() => loadAppConfig(file, env), /developer must be a table/);
    fs.writeFileSync(file, '[developer\n');
    assert.throws(() => loadAppConfig(file, env));
  });
});

test('saving a local Debug Studio preference preserves fields and source comments', () => {
  temporaryConfig((file) => {
    const original = '# connection\r\ngateway_url = "https://stored.example.com"\r\n'
      + 'user_root = "C:/TinadecData"\r\n# debug_studio_enabled = false is only an example\r\n'
      + '[appearance]\r\nmode = "dark" # keep theme\r\n'
      + '[developer] # local preferences\r\n# explicit opt-in\r\n'
      + 'debug_studio_enabled = false # actual preference\r\nother_flag = true\r\n';
    fs.writeFileSync(file, original);
    const env = { TINADEC_GATEWAY_URL: 'https://managed.example.com' };
    assert.equal(saveDebugStudioEnabled(file, true, env).debug_studio_enabled, true);
    const expected = original.replace('false # actual preference', 'true # actual preference');
    assert.equal(fs.readFileSync(file, 'utf8'), expected);
    assert.equal(parse(expected).user_root, 'C:/TinadecData');
    assert.equal(saveDebugStudioEnabled(file, false, env).managed, true);
    assert.equal(fs.readFileSync(file, 'utf8'), original);
    assert.deepEqual(fs.readdirSync(path.dirname(file)), ['desktop.toml']);
  });
});

test('new table, quoted table, dotted and inline preferences preserve their existing content', () => {
  temporaryConfig((file) => {
    const sources = [
      '# unchanged heading\ngateway_url = "https://stored.example.com"\n[appearance]\nmode = "dark"\n',
      '# unchanged heading\n["developer"] # quoted table\nother_flag = true\n',
      '# unchanged heading\ndeveloper.other_flag = true\n',
      '# unchanged heading\ndeveloper = { other_flag = true } # inline table\n',
      '# unchanged heading\ndeveloper = { debug_studio_enabled = false, other_flag = true } # inline table\n',
      '# unchanged heading\n[developer]\nnotes = """\n[developer]\ndebug_studio_enabled = false\n"""\n',
    ];
    for (const source of sources) {
      fs.writeFileSync(file, source);
      const before = parse(source);
      assert.equal(saveDebugStudioEnabled(file, true, {}).debug_studio_enabled, true);
      const written = fs.readFileSync(file, 'utf8');
      assert.ok(written.includes('# unchanged heading'));
      if (source.includes('# inline table')) assert.ok(written.includes('# inline table'));
      if (source.includes('# quoted table')) assert.ok(written.includes('# quoted table'));
      const after = parse(written);
      assert.equal(after.gateway_url, before.gateway_url);
      assert.deepEqual(after.appearance, before.appearance);
      assert.equal(after.developer.other_flag, before.developer?.other_flag);
      assert.equal(after.developer.notes, before.developer?.notes);
      assert.equal(after.developer.debug_studio_enabled, true);
    }
  });
});

test('invalid saves and a failed atomic replacement leave the existing file intact', () => {
  temporaryConfig((file) => {
    const original = '# retained\n[developer]\ndebug_studio_enabled = false\n';
    fs.writeFileSync(file, original);
    assert.throws(() => saveDebugStudioEnabled(file, 'true', {}), /must be a boolean/);
    assert.equal(fs.readFileSync(file, 'utf8'), original);
    const rename = fs.renameSync;
    fs.renameSync = () => { throw new Error('simulated atomic rename failure'); };
    try { assert.throws(() => saveDebugStudioEnabled(file, true, {}), /atomic rename failure/); }
    finally { fs.renameSync = rename; }
    assert.equal(fs.readFileSync(file, 'utf8'), original);
    assert.deepEqual(fs.readdirSync(path.dirname(file)), ['desktop.toml']);
    fs.writeFileSync(file, '[developer]\ndebug_studio_enabled = "false"\n');
    const invalid = fs.readFileSync(file, 'utf8');
    assert.throws(() => saveDebugStudioEnabled(file, true, {}), /must be a boolean/);
    assert.equal(fs.readFileSync(file, 'utf8'), invalid);
  });
});

/** Run the actual main IPC registration without Electron startup or user-root access. */
function mainConfigFixture(configFile) {
  const handlers = new Map();
  const state = { trusted: true, opened: 0, debug: null, messages: [], closed: 0 };
  const makeWindow = (id, destroyed = false) => ({
    id,
    isDestroyed: () => destroyed,
    close: () => { state.closed++; },
    webContents: {
      id,
      isDestroyed: () => destroyed,
      send: (...args) => state.messages.push({ id, args }),
    },
  });
  const main = makeWindow(1);
  const panel = makeWindow(2);
  const disposed = makeWindow(4, true);
  const env = { TINADEC_RESOLVED_GATEWAY_URL: DEFAULT_GATEWAY_URL };
  const nodeProcess = { platform: process.platform, env };
  const hostPaths = { desktopConfig: configFile, root: path.dirname(configFile), source: 'default', hostLogs: 'unused-test-log' };
  const fakeElectron = {
    app: { on() {}, whenReady: () => ({ then() {} }), setAppUserModelId() {} },
    BrowserWindow: { getAllWindows: () => [main, panel, state.debug, disposed].filter(Boolean) },
    // Real Electron throws on a duplicate channel. The fake used to overwrite silently, which
    // made it laxer than the thing it stands in for and let a handler-inside-a-handler slip
    // through: host-status answered the first call and would have thrown on the second in
    // production while this fixture stayed green.
    ipcMain: {
      handle: (channel, handler) => {
        if (handlers.has(channel)) throw new Error(`Attempted to register a second handler for '${channel}'`);
        handlers.set(channel, handler);
      },
      on() {},
    },
    protocol: { registerSchemesAsPrivileged() {} },
  };
  const dependencies = {
    electron: fakeElectron,
    './storagePaths.cjs': { configureElectronStorage: () => hostPaths },
    './logSink.cjs': { createLogSink: () => ({ write() {} }) },
    './appConfig.cjs': {
      loadAppConfig: (file) => loadAppConfig(file, env),
      saveDebugStudioEnabled: (file, enabled) => saveDebugStudioEnabled(file, enabled, env),
    },
    './serviceDiscovery.cjs': {},
    './serviceManager.cjs': { canonicalLocalGatewayUrl: (url) => url === DEFAULT_GATEWAY_URL ? url : null },
    './hostControl.cjs': { createHostControl: () => ({ serviceToken: 'unused-fixture-credential' }) },
    './hostIdentity.cjs': {},
    './hostConnection.cjs': {},
    './trustedHostRequests.cjs': {
      initializeTrustedHostRequests() {},
      isTrustedHostDocumentSender: (event) => event.mainFrame === true,
      isTrustedHostSender: (event) => state.trusted && event.mainFrame === true,
    },
    './layoutStore.cjs': {},
    './debug-studio.cjs': {
      getDebugStudioWindow: () => state.debug,
      createDebugStudioWindow: async () => { state.opened++; state.debug = makeWindow(3); return state.debug; },
    },
    './panelWindow.cjs': { getMainWindow: () => main },
    './terminalManager.cjs': { registerTerminalIpc() {} },
    './petWindow.cjs': {},
    './petStore.cjs': {},
    './appBundle.cjs': {},
    './localMedia.cjs': {},
    // main.cjs serves tool-authored HTML from this scheme and exposes the one IPC entry point
    // that can register a widget. Stubbed to its no-ops: the scheme's own behaviour is pinned by
    // toolWidget.test.cjs, and all this file needs is for the require to resolve.
    './toolWidget.cjs': { registerWidgetProtocol() {}, registerWidget: () => null, dropWidget: () => false },
    './externalLinks.cjs': {},
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'main.cjs'), 'utf8'), {
    require: (name) => {
      if (Object.hasOwn(dependencies, name)) return dependencies[name];
      if (name.startsWith('node:')) return require(name);
      throw new Error(`Unstubbed Electron dependency: ${name}`);
    },
    process: nodeProcess, __dirname,
    console: { log() {}, info() {}, warn() {}, error() {} },
  }, { filename: 'main.cjs' });
  return {
    state, handlers,
    event: { sender: main.webContents, mainFrame: true },
    panelEvent: { sender: panel.webContents, mainFrame: true },
  };
}

test('actual main IPC opens Debug Studio only for a trusted main window with fresh opt-in', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-debug-ipc-'));
  try {
    const file = path.join(root, 'desktop.toml');
    const fixture = mainConfigFixture(file);
    const open = fixture.handlers.get('tinadec:open-debug-studio');
    assert.equal(await open(fixture.event), false);
    saveDebugStudioEnabled(file, true, {});
    assert.equal(await open(fixture.panelEvent), false);
    fixture.state.trusted = false;
    assert.equal(await open(fixture.event), false);
    fixture.state.trusted = true;
    assert.equal(await open({ ...fixture.event, mainFrame: false }), false);
    assert.equal(fixture.state.opened, 0);
    assert.equal(await open(fixture.event), true);
    assert.equal(fixture.state.opened, 1);
    saveDebugStudioEnabled(file, false, {});
    assert.equal(await open(fixture.event), false);
    assert.equal(fixture.state.opened, 1);
    fs.writeFileSync(file, '[developer]\ndebug_studio_enabled = "true"\n');
    await assert.rejects(() => open(fixture.event), /must be a boolean/);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('actual main IPC restricts saves, broadcasts without payload and closes Debug Studio on disable', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-debug-ipc-save-'));
  try {
    const file = path.join(root, 'desktop.toml');
    const original = '# local preference\n[appearance]\nmode = "dark"\n';
    fs.writeFileSync(file, original);
    const fixture = mainConfigFixture(file);
    const save = fixture.handlers.get('tinadec:debug-studio-enabled-save');
    assert.throws(() => save(fixture.panelEvent, true), /trusted main host page/);
    fixture.state.trusted = false;
    assert.throws(() => save(fixture.event, true), /trusted main host page/);
    fixture.state.trusted = true;
    assert.throws(() => save({ ...fixture.event, mainFrame: false }, true), /trusted main host page/);
    assert.throws(() => save(fixture.event, 'true'), /must be a boolean/);
    assert.equal(fs.readFileSync(file, 'utf8'), original);
    assert.equal(fixture.state.messages.length, 0);
    const enabled = save(fixture.event, true);
    assert.equal(enabled.debug_studio_enabled, true);
    assert.deepEqual(enabled, fixture.handlers.get('tinadec:app-config')());
    assert.ok(enabled.storage.root);
    assert.deepEqual(fixture.state.messages.map((message) => message.id), [1, 2]);
    assert.ok(fixture.state.messages.every((message) => message.args.length === 1
      && message.args[0] === 'tinadec:debug-studio-enabled-changed'));
    await fixture.handlers.get('tinadec:open-debug-studio')(fixture.event);
    fixture.state.messages.length = 0;
    const disabled = save(fixture.event, false);
    assert.equal(disabled.debug_studio_enabled, false);
    assert.equal(fixture.state.closed, 1);
    assert.deepEqual(fixture.state.messages.map((message) => message.id), [1, 2, 3]);
    assert.ok(fixture.state.messages.every((message) => message.args.length === 1));
    assert.equal(parse(fs.readFileSync(file, 'utf8')).appearance.mode, 'dark');
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('actual preload bridges Debug Studio save and disposes no-payload change listeners', async () => {
  let bridge;
  const calls = [];
  const listeners = new Map();
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, 'preload.cjs'), 'utf8'), {
    require: (name) => {
      assert.equal(name, 'electron');
      return {
        contextBridge: { exposeInMainWorld: (_name, value) => { bridge = value; } },
        ipcRenderer: {
          invoke: (...args) => { calls.push(args); return Promise.resolve({ debug_studio_enabled: args[1] }); },
          on: (channel, handler) => listeners.set(channel, handler),
          removeListener: (channel, handler) => {
            if (listeners.get(channel) === handler) listeners.delete(channel);
          },
        },
      };
    },
    process: { env: {} },
  }, { filename: 'preload.cjs' });
  const saved = await bridge.saveDebugStudioEnabled(false);
  assert.equal(saved.debug_studio_enabled, false);
  assert.deepEqual(calls, [['tinadec:debug-studio-enabled-save', false]]);
  const received = [];
  const dispose = bridge.onDebugStudioEnabledChanged((...args) => received.push(args));
  listeners.get('tinadec:debug-studio-enabled-changed')({}, { ignored: true });
  assert.deepEqual(received, [[]]);
  dispose();
  assert.equal(listeners.size, 0);
});

test('persists a Gateway URL while the environment remains authoritative', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-app-config-'));
  const configFile = path.join(root, 'desktop.toml');
  try {
    assert.equal(loadAppConfig(configFile, {}).gateway_url, DEFAULT_GATEWAY_URL);
    assert.equal(saveGatewayUrl(configFile, 'https://office.example.com/api/', {}).gateway_url, 'https://office.example.com/api');
    assert.equal(loadAppConfig(configFile, {}).gateway_url, 'https://office.example.com/api');
    assert.equal(loadAppConfig(configFile, { TINADEC_GATEWAY_URL: 'https://managed.example.com' }).source, 'environment');
    assert.throws(() => saveGatewayUrl(configFile, 'https://other.example.com', { TINADEC_GATEWAY_URL: 'https://managed.example.com' }), /managed/);
    assert.equal(resetGatewayUrl(configFile, {}).gateway_url, DEFAULT_GATEWAY_URL);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test('TOML is authoritative and malformed configuration is never silently replaced', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-app-toml-'));
  const file = path.join(root, 'desktop.toml');
  try {
    fs.writeFileSync(file, 'gateway_url = "https://office.example.com"\n[appearance]\nmode = "dark"\n');
    saveGatewayUrl(file, 'https://other.example.com', {});
    assert.match(fs.readFileSync(file, 'utf8'), /mode = "dark"/);
    resetGatewayUrl(file, {});
    assert.match(fs.readFileSync(file, 'utf8'), /mode = "dark"/);
    fs.writeFileSync(file, 'gateway_url = "unterminated');
    assert.throws(() => loadAppConfig(file, {}));
    assert.throws(() => saveGatewayUrl(file, 'https://valid.example.com', {}));
    assert.equal(fs.readFileSync(file, 'utf8'), 'gateway_url = "unterminated');
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

 test('actual host status and retry remain accessible to the trusted main document after business authority is unavailable', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-host-status-ipc-'));
  try {
    const fixture = mainConfigFixture(path.join(root, 'desktop.toml'));
    fixture.state.trusted = false;
    const status = fixture.handlers.get('tinadec:host-status');
    const retry = fixture.handlers.get('tinadec:host-retry');
    assert.equal(status(fixture.event).state, 'checking');
    assert.equal(retry(fixture.event).state, 'checking');
    assert.throws(() => status({ ...fixture.event, mainFrame: false }), /trusted host page/);
    assert.throws(() => retry(fixture.panelEvent), /trusted main host page/);
    assert.throws(() => retry({ ...fixture.event, mainFrame: false }), /trusted main host page/);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('host-status answers every time, not only the first', () => {
  // Real Electron throws when a channel is handled twice, and a handler registered INSIDE another
  // handler only exists once that handler has run. So host-status used to answer the first call
  // and throw on the second — "the host is running but nothing connects", because the desktop
  // never got its status and nothing was granted. The widget channels are registered at load now;
  // this drives the real handler twice, which is the shape of the bug.
  const fixture = mainConfigFixture(path.join(os.tmpdir(), `tinadec-host-status-${process.pid}.toml`));
  const hostStatus = fixture.handlers.get('tinadec:host-status');
  assert.ok(hostStatus, 'host-status must be registered');

  const first = hostStatus(fixture.event);
  const second = hostStatus(fixture.event);

  assert.ok(first, 'the first host-status call must answer');
  assert.ok(second, 'the second host-status call must answer too');
  assert.equal(typeof second.state, 'string');
});

test('the widget channels are registered at load, not from inside another handler', () => {
  // A lazily-registered channel is the same bug one step later: the renderer asks for it before
  // anything has called host-status, and gets "no handler registered".
  const fixture = mainConfigFixture(path.join(os.tmpdir(), `tinadec-widget-channels-${process.pid}.toml`));
  assert.ok(fixture.handlers.has('tinadec:register-tool-widget'));
  assert.ok(fixture.handlers.has('tinadec:release-tool-widget'));
});
