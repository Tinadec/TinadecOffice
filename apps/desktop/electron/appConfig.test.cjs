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
  saveCloseToTray,
  saveCompanionCapsuleReveal,
  saveCompanionCapsuleStyle,
  saveCompanionMode,
  saveDebugStudioEnabled,
} = require('./appConfig.cjs');
const {
  CAPSULE_REVEALS,
  CAPSULE_STYLES,
  DEFAULT_CAPSULE_REVEAL,
  DEFAULT_CAPSULE_STYLE,
} = require('./companionGeometry.cjs');

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

test('close-to-tray follows the unset default and rejects non-boolean values', () => {
  temporaryConfig((file) => {
    // A packaged build passes `true`; everything else leaves it off unless the file says otherwise.
    assert.equal(loadAppConfig(file, {}).close_to_tray, false);
    assert.equal(loadAppConfig(file, {}, { defaultCloseToTray: true }).close_to_tray, true);
    assert.equal(loadAppConfig(file, { TINADEC_GATEWAY_URL: 'https://managed.example.com' },
      { defaultCloseToTray: true }).close_to_tray, true);

    fs.writeFileSync(file, '[window]\nclose_to_tray = false\n');
    assert.equal(loadAppConfig(file, {}, { defaultCloseToTray: true }).close_to_tray, false);
    fs.writeFileSync(file, 'window.close_to_tray = true\n');
    assert.equal(loadAppConfig(file, {}).close_to_tray, true);

    for (const value of ['"true"', '1', '[true]', '{ value = true }']) {
      fs.writeFileSync(file, `[window]\nclose_to_tray = ${value}\n`);
      assert.throws(() => loadAppConfig(file, {}), /close_to_tray must be a boolean/);
    }
    fs.writeFileSync(file, 'window = "on"\n');
    assert.throws(() => loadAppConfig(file, {}), /window must be a table/);
  });
});

test('saving close-to-tray keeps comments, the developer table, and the rest of the file', () => {
  temporaryConfig((file) => {
    const original = '# connection\ngateway_url = "https://stored.example.com"\r\n'
      + 'user_root = "C:/TinadecData"\r\n'
      + '[appearance]\r\nmode = "dark" # keep theme\r\n'
      + '[developer] # local preferences\r\ndebug_studio_enabled = true\r\n'
      + '[window]\r\n# close_to_tray = false is only an example\r\n';
    fs.writeFileSync(file, original);

    const saved = saveCloseToTray(file, true, {});
    assert.equal(saved.close_to_tray, true);
    const after = fs.readFileSync(file, 'utf8');
    assert.ok(after.includes('# connection') && after.includes('# keep theme'), 'comments survive');
    assert.ok(after.includes('# close_to_tray = false is only an example'), 'the example comment is untouched');
    const document = parse(after);
    assert.equal(document.window.close_to_tray, true);
    assert.equal(document.developer.debug_studio_enabled, true);
    assert.equal(document.appearance.mode, 'dark');
    assert.equal(document.gateway_url, 'https://stored.example.com');

    // Saving again flips the stored value in place rather than appending a second key.
    saveCloseToTray(file, false, {});
    const flipped = fs.readFileSync(file, 'utf8');
    assert.equal(parse(flipped).window.close_to_tray, false);
    const settings = flipped.split('\n').map((line) => line.trim()).filter((line) => line.startsWith('close_to_tray'));
    assert.deepEqual(settings, ['close_to_tray = false']);

    // A file with no [window] table gains one, and a non-boolean is refused without touching it.
    fs.writeFileSync(file, 'gateway_url = "https://stored.example.com"\n');
    saveCloseToTray(file, true, {});
    assert.equal(parse(fs.readFileSync(file, 'utf8')).window.close_to_tray, true);
    const intact = fs.readFileSync(file, 'utf8');
    assert.throws(() => saveCloseToTray(file, 'yes', {}), /must be a boolean/);
    assert.equal(fs.readFileSync(file, 'utf8'), intact);
  });
});

test('companion mode defaults off and rejects a non-boolean or non-table preference', () => {
  temporaryConfig((file) => {
    assert.equal(loadAppConfig(file, {}).companion_mode_enabled, false);
    // A managed Gateway decides transport, not whether this machine shows a companion.
    assert.equal(loadAppConfig(file, { TINADEC_GATEWAY_URL: 'https://managed.example.com' }).companion_mode_enabled, false);
    fs.writeFileSync(file, '[companion]\nenabled = true\n');
    assert.equal(loadAppConfig(file, {}).companion_mode_enabled, true);
    fs.writeFileSync(file, 'companion.enabled = true\n');
    assert.equal(loadAppConfig(file, {}).companion_mode_enabled, true);

    for (const value of ['"true"', '1', '[true]', '{ value = true }']) {
      fs.writeFileSync(file, `[companion]\nenabled = ${value}\n`);
      assert.throws(() => loadAppConfig(file, {}), /companion\.enabled must be a boolean/);
    }
    fs.writeFileSync(file, 'companion = "on"\n');
    assert.throws(() => loadAppConfig(file, {}), /companion must be a table/);
  });
});

test('saving companion mode keeps comments and the other tables intact', () => {
  temporaryConfig((file) => {
    const original = '# connection\r\ngateway_url = "https://stored.example.com"\r\n'
      + '[window]\r\nclose_to_tray = true\r\n'
      + '[companion] # docked companion\r\n# enabled = false is only an example\r\n';
    fs.writeFileSync(file, original);

    const saved = saveCompanionMode(file, true, {});
    assert.equal(saved.companion_mode_enabled, true);
    const after = fs.readFileSync(file, 'utf8');
    assert.ok(after.includes('# connection') && after.includes('[companion] # docked companion'), 'comments survive');
    assert.ok(after.includes('# enabled = false is only an example'), 'the example comment is untouched');
    const document = parse(after);
    assert.equal(document.companion.enabled, true);
    assert.equal(document.window.close_to_tray, true);
    assert.equal(document.gateway_url, 'https://stored.example.com');

    // Saving again flips the stored value in place rather than appending a second key.
    saveCompanionMode(file, false, {});
    const flipped = fs.readFileSync(file, 'utf8');
    assert.equal(parse(flipped).companion.enabled, false);
    const settings = flipped.split('\n').map((line) => line.trim()).filter((line) => line.startsWith('enabled'));
    assert.deepEqual(settings, ['enabled = false']);
    assert.equal(parse(flipped).window.close_to_tray, true);

    // A file with no [companion] table gains one, and a non-boolean is refused without touching it.
    fs.writeFileSync(file, 'gateway_url = "https://stored.example.com"\n');
    saveCompanionMode(file, true, {});
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.enabled, true);
    const intact = fs.readFileSync(file, 'utf8');
    assert.throws(() => saveCompanionMode(file, 'yes', {}), /must be a boolean/);
    assert.equal(fs.readFileSync(file, 'utf8'), intact);
  });
});

test('capsule style defaults to the peek and reads only the two supported styles', () => {
  temporaryConfig((file) => {
    assert.equal(loadAppConfig(file, {}).companion_capsule_style, DEFAULT_CAPSULE_STYLE);
    for (const style of CAPSULE_STYLES) {
      fs.writeFileSync(file, `[companion]\ncapsule_style = "${style}"\n`);
      assert.equal(loadAppConfig(file, {}).companion_capsule_style, style);
      // Dotted keys and single quotes are valid TOML too.
      fs.writeFileSync(file, `companion.capsule_style = '${style}'\n`);
      assert.equal(loadAppConfig(file, {}).companion_capsule_style, style);
    }
    // An unknown style comes from a newer build or a hand-edited file: fall back, never refuse to
    // start the app over a display preference.
    for (const value of ['"fullscreen"', '""', '"Peek"']) {
      fs.writeFileSync(file, `[companion]\ncapsule_style = ${value}\n`);
      assert.equal(loadAppConfig(file, {}).companion_capsule_style, DEFAULT_CAPSULE_STYLE);
    }
    // A value of the wrong type is a broken file, the same way a non-integer width is.
    for (const value of ['true', '1', '[1]', '{ style = "grip" }']) {
      fs.writeFileSync(file, `[companion]\ncapsule_style = ${value}\n`);
      assert.throws(() => loadAppConfig(file, {}), /companion\.capsule_style must be a string/);
    }
  });
});

test('saving a capsule style rewrites one quoted string and keeps the rest of the file intact', () => {
  temporaryConfig((file) => {
    fs.writeFileSync(file, '# docked companion\r\n[companion]\r\nenabled = true\r\n');

    const saved = saveCompanionCapsuleStyle(file, 'grip', {});
    assert.equal(saved.companion_capsule_style, 'grip');
    // The boolean in the same table is not disturbed.
    assert.equal(saved.companion_mode_enabled, true);
    const after = fs.readFileSync(file, 'utf8');
    assert.ok(after.includes('# docked companion'), 'comments survive');
    assert.equal(parse(after).companion.capsule_style, 'grip');

    // Changing it again replaces the literal in place rather than appending a second key.
    saveCompanionCapsuleStyle(file, 'peek', {});
    const rewritten = fs.readFileSync(file, 'utf8');
    const settings = rewritten.split('\n').map((line) => line.trim()).filter((line) => line.startsWith('capsule_style'));
    assert.deepEqual(settings, ['capsule_style = "peek"']);

    // A file without a [companion] table gains one; an unknown style is refused untouched.
    fs.writeFileSync(file, 'gateway_url = "https://stored.example.com"\n');
    saveCompanionCapsuleStyle(file, 'grip', {});
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.capsule_style, 'grip');
    const intact = fs.readFileSync(file, 'utf8');
    assert.throws(() => saveCompanionCapsuleStyle(file, 'fullscreen', {}), /must be one of peek, grip/);
    assert.throws(() => saveCompanionCapsuleStyle(file, 1, {}), /must be a string/);
    assert.equal(fs.readFileSync(file, 'utf8'), intact);
  });
});

/** Run the actual main IPC registration without Electron startup or user-root access. */function mainConfigFixture(configFile) {
  const handlers = new Map();
  const state = {
    trusted: true,
    opened: 0,
    debug: null,
    messages: [],
    closed: 0,
    // The companion module is a stub here: these records are what the IPC handlers forwarded to it.
    companionSenderId: null,
    companion: { applied: [], moved: [], sides: [], styles: [], reveals: [], expanded: 0, collapsed: 0, closed: 0, checks: 0, applyError: null },
  };
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
    ipcMain: { handle: (channel, handler) => handlers.set(channel, handler), on() {} },
    protocol: { registerSchemesAsPrivileged() {} },
  };
  const dependencies = {
    electron: fakeElectron,
    './storagePaths.cjs': { configureElectronStorage: () => hostPaths },
    './logSink.cjs': { createLogSink: () => ({ write() {} }) },
    './appConfig.cjs': {
      loadAppConfig: (file) => loadAppConfig(file, env),
      saveDebugStudioEnabled: (file, enabled) => saveDebugStudioEnabled(file, enabled, env),
      saveCloseToTray: (file, enabled) => saveCloseToTray(file, enabled, env),
      saveCompanionMode: (file, enabled) => saveCompanionMode(file, enabled, env),
      saveCompanionCapsuleStyle: (file, style) => saveCompanionCapsuleStyle(file, style, env),
      saveCompanionCapsuleReveal: (file, reveal) => saveCompanionCapsuleReveal(file, reveal, env),
    },
    // The bridge is built per call inside the IPC handler and immediately invoked.
    './companionApi.cjs': {
      createCompanionApiBridge: () => async () => ({ status: 503, body: { code: 'companion_api_stubbed' } }),
    },
    // Real geometry/window work belongs to companionWindow.test.cjs; here the module only has to
    // answer main.cjs's destructuring and record which calls the IPC handlers forwarded.
    './companionWindow.cjs': {
      applyCompanionMode: async (enabled) => {
        if (state.companion.applyError) throw state.companion.applyError;
        state.companion.applied.push(enabled);
        return { open: enabled, expanded: false, side: 'right' };
      },
      closeCompanionWindow: () => { state.companion.closed++; },
      collapseCompanionWindow: () => { state.companion.collapsed++; return { open: true, expanded: false, side: 'right' }; },
      createCompanionWindow: async () => ({ open: true, expanded: false, side: 'right' }),
      expandCompanionWindow: () => { state.companion.expanded++; return { open: true, expanded: true, side: 'right' }; },
      isCompanionWindowSender: (sender) => state.companionSenderId !== null && sender?.id === state.companionSenderId,
      moveTo: (top) => { state.companion.moved.push(top); return { open: true, expanded: false, side: 'right' }; },
      scheduleCollapseCheck: () => { state.companion.checks++; },
      setCapsuleStyle: (style) => { state.companion.styles.push(style); return { open: true, expanded: false, side: 'right', capsuleStyle: style }; },
      setCapsuleReveal: (reveal) => { state.companion.reveals.push(reveal); return { open: true, expanded: false, side: 'right', capsuleReveal: reveal }; },
      setSide: (side) => { state.companion.sides.push(side); return { open: true, expanded: false, side }; },
      snapshot: () => ({ open: true, expanded: false, side: 'right' }),
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
    './tray.cjs': { createTrayController: () => ({ refresh() {}, destroy() {}, isActive: () => false }), trayIconPath: () => 'unused-test-icon' },
    './terminalManager.cjs': { registerTerminalIpc() {} },
    './petWindow.cjs': {},
    './petStore.cjs': {},
    // Committed after this fixture: the destructuring at main.cjs load time only needs the names.
    './agentAvatars.cjs': {
      clearPet: async () => {},
      loadState: async () => ({ preference: null, instances: {} }),
      setPreference: async (_agentId, preference) => ({ preference }),
      saveInstance: async (input) => input,
      releaseInstances: () => {},
    },
    './appBundle.cjs': {},
    './localMedia.cjs': {},
    './externalLinks.cjs': {},
    // Logto auth (1eb448b4): no case here exercises the account channels, the stub
    // only satisfies main.cjs's destructuring at load time with the real shapes.
    './auth.cjs': {
      openLogin: async () => null,
      logout: async () => {},
      getAuthState: async () => ({ loggedIn: false, nickname: '', avatar_url: '' }),
      updateProfile: async () => { throw new Error('auth is stubbed in this fixture'); },
    },
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
    // A sender the stub only recognises while a test claims it via `state.companionSenderId`.
    companionEvent: { sender: { id: 5, isDestroyed: () => false }, mainFrame: true },
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

test('actual main IPC stores close-to-tray only for the trusted main page and rebroadcasts it', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-close-to-tray-'));
  try {
    const file = path.join(root, 'desktop.toml');
    const original = '# preferences\n[developer]\ndebug_studio_enabled = true\n';
    fs.writeFileSync(file, original);
    const fixture = mainConfigFixture(file);
    const save = fixture.handlers.get('tinadec:close-to-tray-save');

    assert.throws(() => save(fixture.panelEvent, true), /trusted main host page/);
    fixture.state.trusted = false;
    assert.throws(() => save(fixture.event, true), /trusted main host page/);
    fixture.state.trusted = true;
    assert.throws(() => save({ ...fixture.event, mainFrame: false }, true), /trusted main host page/);
    assert.throws(() => save(fixture.event, 'true'), /must be a boolean/);
    assert.equal(fs.readFileSync(file, 'utf8'), original);
    assert.equal(fixture.state.messages.length, 0);

    const enabled = save(fixture.event, true);
    assert.equal(enabled.close_to_tray, true);
    // The answer is the same snapshot `tinadec:app-config` serves, so the settings page can reuse it.
    assert.deepEqual(enabled, fixture.handlers.get('tinadec:app-config')());
    // Both live windows learn the stored value; the already-destroyed one is skipped.
    assert.deepEqual(fixture.state.messages.map((message) => message.id), [1, 2]);
    assert.ok(fixture.state.messages.every((message) => message.args[0] === 'tinadec:close-to-tray-changed'
      && message.args[1].close_to_tray === true));
    assert.equal(parse(fs.readFileSync(file, 'utf8')).window.close_to_tray, true);

    fixture.state.messages.length = 0;
    assert.equal(save(fixture.event, false).close_to_tray, false);
    // Compared as JSON: the payload objects were built inside the vm realm.
    assert.deepEqual(fixture.state.messages.map((message) => JSON.stringify(message.args[1])),
      ['{"close_to_tray":false}', '{"close_to_tray":false}']);
    assert.equal(parse(fs.readFileSync(file, 'utf8')).developer.debug_studio_enabled, true);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('actual main IPC stores companion mode only for the trusted main page and keeps the flag on window failure', async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-companion-mode-'));
  try {
    const file = path.join(root, 'desktop.toml');
    const original = '# preferences\n[developer]\ndebug_studio_enabled = true\n';
    fs.writeFileSync(file, original);
    const fixture = mainConfigFixture(file);
    const save = fixture.handlers.get('tinadec:companion-mode-save');

    // The handler is async, so the trust refusal arrives as a rejected promise, not a throw.
    await assert.rejects(save(fixture.panelEvent, true), /trusted main host page/);
    fixture.state.trusted = false;
    await assert.rejects(save(fixture.event, true), /trusted main host page/);
    fixture.state.trusted = true;
    await assert.rejects(save({ ...fixture.event, mainFrame: false }, true), /trusted main host page/);
    await assert.rejects(save(fixture.event, 'true'), /Companion mode enabled must be a boolean/);
    assert.equal(fs.readFileSync(file, 'utf8'), original);
    assert.deepEqual(fixture.state.companion.applied, []);

    const enabled = await save(fixture.event, true);
    assert.equal(enabled.companion_mode_enabled, true);
    // The answer is the same snapshot `tinadec:app-config` serves.
    assert.deepEqual(enabled, fixture.handlers.get('tinadec:app-config')());
    assert.deepEqual(fixture.state.companion.applied, [true]);
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.enabled, true);
    assert.equal(parse(fs.readFileSync(file, 'utf8')).developer.debug_studio_enabled, true);

    // A window that cannot be shown fails the call but does not roll the stored preference back:
    // the user asked for it, and the next start has to retry from the same answer.
    fixture.state.companion.applyError = new Error('companion window unavailable');
    await assert.rejects(save(fixture.event, false), /companion window unavailable/);
    assert.deepEqual(fixture.state.companion.applied, [true]);
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.enabled, false);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('capsule reveal defaults to the middle step and reads only the three steps', () => {
  temporaryConfig((file) => {
    assert.equal(loadAppConfig(file, {}).companion_capsule_reveal, DEFAULT_CAPSULE_REVEAL);
    for (const reveal of CAPSULE_REVEALS) {
      fs.writeFileSync(file, `[companion]\ncapsule_reveal = ${reveal}\n`);
      assert.equal(loadAppConfig(file, {}).companion_capsule_reveal, reveal);
      fs.writeFileSync(file, `companion.capsule_reveal = ${reveal}\n`);
      assert.equal(loadAppConfig(file, {}).companion_capsule_reveal, reveal);
    }
    // A step outside the three is a display preference, not a broken file.
    for (const reveal of [0, 25, 60, 101, -10]) {
      fs.writeFileSync(file, `[companion]\ncapsule_reveal = ${reveal}\n`);
      assert.equal(loadAppConfig(file, {}).companion_capsule_reveal, DEFAULT_CAPSULE_REVEAL);
    }
    // A non-integer is a type error, the same way the style treats a non-string.
    for (const value of ['"50"', 'true', '49.5', '[50]', '{ value = 50 }']) {
      fs.writeFileSync(file, `[companion]\ncapsule_reveal = ${value}\n`);
      assert.throws(() => loadAppConfig(file, {}), /companion\.capsule_reveal must be an integer/);
    }
  });
});

test('saving a capsule reveal rewrites one integer and leaves the style alone', () => {
  temporaryConfig((file) => {
    fs.writeFileSync(file, '# docked companion\r\n[companion]\r\ncapsule_style = "grip"\r\n');

    const saved = saveCompanionCapsuleReveal(file, 10, {});
    assert.equal(saved.companion_capsule_reveal, 10);
    assert.equal(saved.companion_capsule_style, 'grip');
    const after = fs.readFileSync(file, 'utf8');
    assert.ok(after.includes('# docked companion'), 'comments survive');
    assert.equal(parse(after).companion.capsule_reveal, 10);
    assert.equal(parse(after).companion.capsule_style, 'grip');

    saveCompanionCapsuleReveal(file, 100, {});
    const rewritten = fs.readFileSync(file, 'utf8');
    const settings = rewritten.split('\n').map((line) => line.trim()).filter((line) => line.startsWith('capsule_reveal'));
    assert.deepEqual(settings, ['capsule_reveal = 100']);

    // A file without a [companion] table gains one; an unknown step is refused untouched.
    fs.writeFileSync(file, 'gateway_url = "https://stored.example.com"\n');
    saveCompanionCapsuleReveal(file, 50, {});
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.capsule_reveal, 50);
    const intact = fs.readFileSync(file, 'utf8');
    assert.throws(() => saveCompanionCapsuleReveal(file, 25, {}), /must be one of 10, 50, 100/);
    assert.throws(() => saveCompanionCapsuleReveal(file, '50', {}), /must be an integer/);
    assert.equal(fs.readFileSync(file, 'utf8'), intact);
  });
});

test('actual main IPC persists and applies the capsule style only for the trusted main page', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-companion-style-'));
  try {
    const file = path.join(root, 'desktop.toml');
    fs.writeFileSync(file, '[companion]\nenabled = true\n');
    const fixture = mainConfigFixture(file);
    const save = fixture.handlers.get('tinadec:companion-capsule-style-save');
    assert.ok(save, 'the capsule-style channel is registered');

    // Only the trusted main document may restyle the capsule: a detached panel cannot.
    assert.throws(() => save(fixture.panelEvent, 'grip'), /trusted main host page/);
    fixture.state.trusted = false;
    assert.throws(() => save(fixture.event, 'grip'), /trusted main host page/);
    fixture.state.trusted = true;
    assert.deepEqual(fixture.state.companion.styles, []);

    // An unknown style is refused before anything is written or applied.
    assert.throws(() => save(fixture.event, 'fullscreen'), /must be one of peek, grip/);
    assert.throws(() => save(fixture.event, 1), /must be a string/);
    assert.deepEqual(fixture.state.companion.styles, []);
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.capsule_style, undefined);

    const saved = save(fixture.event, 'grip');
    assert.equal(saved.companion_capsule_style, 'grip');
    // The live capsule is restyled with the value that was actually persisted.
    assert.deepEqual(fixture.state.companion.styles, ['grip']);
    const document = parse(fs.readFileSync(file, 'utf8'));
    assert.equal(document.companion.capsule_style, 'grip');
    // The companion-mode flag in the same table survives the edit.
    assert.equal(document.companion.enabled, true);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('actual main IPC persists and applies the capsule reveal only for the trusted main page', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-companion-reveal-'));
  try {
    const file = path.join(root, 'desktop.toml');
    fs.writeFileSync(file, '[companion]\nenabled = true\n');
    const fixture = mainConfigFixture(file);
    const save = fixture.handlers.get('tinadec:companion-capsule-reveal-save');
    assert.ok(save, 'the capsule-reveal channel is registered');

    // Only the trusted main document may park more or less of the capsule on screen.
    assert.throws(() => save(fixture.panelEvent, 10), /trusted main host page/);
    fixture.state.trusted = false;
    assert.throws(() => save(fixture.event, 10), /trusted main host page/);
    fixture.state.trusted = true;
    assert.deepEqual(fixture.state.companion.reveals, []);

    // An unknown step is refused before anything is written or applied.
    assert.throws(() => save(fixture.event, 25), /must be one of 10, 50, 100/);
    assert.throws(() => save(fixture.event, '50'), /must be an integer/);
    assert.deepEqual(fixture.state.companion.reveals, []);
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.capsule_reveal, undefined);

    const saved = save(fixture.event, 10);
    assert.equal(saved.companion_capsule_reveal, 10);
    // The live capsule is parked with the value that was actually persisted.
    assert.deepEqual(fixture.state.companion.reveals, [10]);
    assert.equal(parse(fs.readFileSync(file, 'utf8')).companion.capsule_reveal, 10);
  } finally { fs.rmSync(root, { recursive: true, force: true }); }
});

test('actual main IPC trusts only the companion window for its five geometry channels', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tinadec-companion-ipc-'));
  try {
    const fixture = mainConfigFixture(path.join(root, 'desktop.toml'));
    const state = fixture.handlers.get('tinadec:companion-state');
    const expand = fixture.handlers.get('tinadec:companion-expand');
    const collapse = fixture.handlers.get('tinadec:companion-collapse');
    const move = fixture.handlers.get('tinadec:companion-move');
    const side = fixture.handlers.get('tinadec:companion-side');

    // Neither the main page nor a detached panel may drive the companion's shape.
    for (const handler of [state, expand, collapse, move, side]) {
      assert.throws(() => handler(fixture.event), /only available to the companion window/);
      assert.throws(() => handler(fixture.panelEvent), /only available to the companion window/);
    }
    assert.deepEqual(fixture.state.companion.moved, []);
    assert.equal(fixture.state.companion.expanded, 0);

    fixture.state.companionSenderId = 5;
    // The companion is not a trusted host page; its own channels must not depend on that flag.
    fixture.state.trusted = false;
    assert.equal(state(fixture.companionEvent).open, true);
    assert.equal(expand(fixture.companionEvent).expanded, true);
    assert.equal(collapse(fixture.companionEvent).expanded, false);
    assert.deepEqual(move(fixture.companionEvent, 240), { open: true, expanded: false, side: 'right' });
    assert.equal(side(fixture.companionEvent, 'left').side, 'left');
    assert.deepEqual(fixture.state.companion.moved, [240]);
    assert.deepEqual(fixture.state.companion.sides, ['left']);
    assert.equal(fixture.state.companion.expanded, 1);
    assert.equal(fixture.state.companion.collapsed, 1);
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
