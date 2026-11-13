const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const {
  APP_NAME,
  TRAY_STRINGS,
  buildTrayMenuTemplate,
  createTrayController,
  resolveTrayLocale,
  shouldHideOnClose,
  trayIconFile,
  trayIconPath,
  trayStrings,
} = require('./tray.cjs');

/**
 * The tray is what makes "close the window, keep running" safe: without a reachable icon the app
 * becomes a process with no window. These cases grade the three parts separately — locale mapping,
 * the menu description, and the controller's create/destroy lifecycle — because each has its own
 * failure mode, and the main-process wiring is pinned at the bottom.
 */

test('any locale tag resolves to one of the five bundled languages', () => {
  assert.equal(resolveTrayLocale('zh-CN'), 'zh-CN');
  assert.equal(resolveTrayLocale('zh-TW'), 'zh-TW');
  // Traditional Chinese arrives as a region or a script subtag depending on the OS.
  assert.equal(resolveTrayLocale('zh-HK'), 'zh-TW');
  assert.equal(resolveTrayLocale('zh-Hant-HK'), 'zh-TW');
  assert.equal(resolveTrayLocale('zh'), 'zh-CN');
  assert.equal(resolveTrayLocale('zh-Hans-CN'), 'zh-CN');
  assert.equal(resolveTrayLocale('en-US'), 'en');
  assert.equal(resolveTrayLocale('ja-JP'), 'ja');
  assert.equal(resolveTrayLocale('ru-RU'), 'ru');
  // An OS language the renderer cannot show either falls back to the default bundle.
  assert.equal(resolveTrayLocale('de-DE'), 'zh-CN');
  assert.equal(resolveTrayLocale(''), 'zh-CN');
  assert.equal(resolveTrayLocale(undefined), 'zh-CN');
  assert.equal(resolveTrayLocale(42), 'zh-CN');
});

test('every language carries the same non-empty labels', () => {
  assert.deepEqual(Object.keys(TRAY_STRINGS).sort(), ['en', 'ja', 'ru', 'zh-CN', 'zh-TW']);
  const reference = Object.keys(TRAY_STRINGS['zh-CN']).sort();
  for (const [locale, strings] of Object.entries(TRAY_STRINGS)) {
    assert.deepEqual(Object.keys(strings).sort(), reference, `${locale} is missing tray labels`);
    for (const [key, value] of Object.entries(strings)) {
      assert.ok(typeof value === 'string' && value.trim(), `${locale}.${key} must not be empty`);
    }
  }
  assert.equal(trayStrings('en-GB').show, TRAY_STRINGS.en.show);
});

test('the icon file follows the platform and both candidates ship', () => {
  assert.equal(trayIconFile('win32'), 'tinadec.ico');
  assert.equal(trayIconFile('darwin'), 'tinadec-logo.png');
  assert.equal(trayIconFile('linux'), 'tinadec-logo.png');

  const electronDir = path.join('C:', 'app', 'electron');
  assert.equal(trayIconPath({ electronDir, isDev: true, platform: 'win32' }),
    path.join('C:', 'app', 'public', 'tinadec.ico'));
  assert.equal(trayIconPath({ electronDir, isDev: false, platform: 'linux' }),
    path.join('C:', 'app', 'dist', 'tinadec-logo.png'));

  // The paths above are only meaningful if the files exist; dev serves public/, packaged serves dist/.
  for (const file of ['public/tinadec.ico', 'public/tinadec-logo.png']) {
    assert.ok(fs.existsSync(path.join(__dirname, '..', file)), `${file} must exist`);
  }
});

test('closing only hides while the preference is on and an icon exists', () => {
  assert.equal(shouldHideOnClose({ shuttingDown: false, closeToTray: true, trayActive: true }), true);
  // No tray means a hidden window would be unreachable, so the close button keeps closing.
  assert.equal(shouldHideOnClose({ shuttingDown: false, closeToTray: true, trayActive: false }), false);
  assert.equal(shouldHideOnClose({ shuttingDown: false, closeToTray: false, trayActive: true }), false);
  // Quitting must be able to close the window it just asked to close.
  assert.equal(shouldHideOnClose({ shuttingDown: true, closeToTray: true, trayActive: true }), false);
});

function recordingActions() {
  const calls = [];
  return {
    calls,
    show: () => calls.push(['show']),
    hide: () => calls.push(['hide']),
    showPanel: (windowId) => calls.push(['panel', windowId]),
    setCloseToTray: (enabled) => calls.push(['close-to-tray', enabled]),
    quit: () => calls.push(['quit']),
  };
}

test('the menu always offers a way back and a way out', () => {
  const actions = recordingActions();
  const template = buildTrayMenuTemplate({ strings: trayStrings('en'), visible: false, closeToTray: true, actions });
  assert.deepEqual(template.map((item) => item.id ?? item.type), ['show', 'separator', 'close-to-tray', 'separator', 'quit']);
  assert.equal(template[0].label, 'Open main window');
  assert.equal(template.find((item) => item.id === 'close-to-tray').checked, true);
  assert.equal(template.at(-1).label, `Quit ${APP_NAME}`);

  template[0].click();
  template.at(-1).click();
  assert.deepEqual(actions.calls, [['show'], ['quit']]);
});

test('the hide item appears only while there is a window to hide', () => {
  const hidden = buildTrayMenuTemplate({ strings: trayStrings('en'), visible: false, closeToTray: true, actions: recordingActions() });
  assert.equal(hidden.some((item) => item.id === 'hide'), false);

  const actions = recordingActions();
  const shown = buildTrayMenuTemplate({ strings: trayStrings('en'), visible: true, closeToTray: true, actions });
  const hide = shown.find((item) => item.id === 'hide');
  assert.ok(hide);
  hide.click();
  assert.deepEqual(actions.calls, [['hide']]);
});

test('the checkbox reports the toggled state Electron hands it', () => {
  const actions = recordingActions();
  const template = buildTrayMenuTemplate({ strings: trayStrings('en'), visible: true, closeToTray: true, actions });
  const checkbox = template.find((item) => item.id === 'close-to-tray');
  assert.equal(checkbox.type, 'checkbox');
  checkbox.click({ checked: false });
  checkbox.click({ checked: true });
  assert.deepEqual(actions.calls, [['close-to-tray', false], ['close-to-tray', true]]);
});

test('detached panels become a submenu that focuses the window it names', () => {
  const actions = recordingActions();
  const withoutPanels = buildTrayMenuTemplate({ strings: trayStrings('en'), visible: true, closeToTray: false, actions });
  assert.equal(withoutPanels.some((item) => item.id === 'panels'), false);

  const template = buildTrayMenuTemplate({
    strings: trayStrings('zh-CN'), visible: true, closeToTray: false, actions,
    panels: [{ windowId: 7, title: 'Git' }, { windowId: 9 }],
  });
  const panels = template.find((item) => item.id === 'panels');
  assert.deepEqual(panels.submenu.map((item) => item.label), ['Git', '9']);
  panels.submenu[1].click();
  assert.deepEqual(actions.calls, [['panel', 9]]);
});

/** A Tray double with just the surface tray.cjs touches. */
class FakeTray {
  static instances = [];

  constructor(icon) {
    FakeTray.instances.push(this);
    this.icon = icon;
    this.destroyed = false;
    this.handlers = new Map();
    this.tooltips = [];
    this.menus = [];
    this.balloons = [];
  }

  on(event, handler) { this.handlers.set(event, handler); return this; }
  setToolTip(text) { this.tooltips.push(text); }
  setContextMenu(menu) { this.menus.push(menu); }
  displayBalloon(options) { this.balloons.push(options); }
  isDestroyed() { return this.destroyed; }
  destroy() { this.destroyed = true; }
  click(event = 'click') { this.handlers.get(event)?.(); }
}

function fakeControllerOptions(overrides = {}) {
  const state = { enabled: true, visible: true, closeToTray: true, panels: [], ...overrides.state };
  return {
    state,
    options: {
      Tray: FakeTray,
      Menu: { buildFromTemplate: (template) => ({ template }) },
      nativeImage: { createFromPath: (iconPath) => ({ iconPath }) },
      iconPath: '/tmp/tinadec.ico',
      platform: 'win32',
      actions: overrides.actions ?? recordingActions(),
      getState: () => state,
      locale: overrides.locale ?? 'zh-CN',
      onError: overrides.onError ?? (() => {}),
      ...overrides.options,
    },
  };
}

test('the icon is created once, rebuilt in place, and destroyed when the preference turns off', () => {
  FakeTray.instances = [];
  const { state, options } = fakeControllerOptions();
  const controller = createTrayController(options);

  assert.equal(controller.refresh(), true);
  assert.equal(FakeTray.instances.length, 1);
  const tray = FakeTray.instances[0];
  assert.equal(tray.icon.iconPath, '/tmp/tinadec.ico');
  assert.equal(tray.tooltips.at(-1), APP_NAME);
  assert.equal(tray.menus.length, 1);
  assert.equal(tray.menus[0].template.some((item) => item.id === 'hide'), true);

  // Re-refreshing follows the window: still one Tray, rebuilt menu, hide item gone.
  state.visible = false;
  assert.equal(controller.refresh(), true);
  assert.equal(FakeTray.instances.length, 1);
  assert.equal(tray.menus.length, 2);
  assert.equal(tray.menus[1].template.some((item) => item.id === 'hide'), false);

  state.enabled = false;
  assert.equal(controller.refresh(), false);
  assert.equal(tray.destroyed, true);
  assert.equal(controller.isActive(), false);

  // Turning it back on creates a fresh icon rather than resurrecting the destroyed one.
  state.enabled = true;
  assert.equal(controller.refresh(), true);
  assert.equal(FakeTray.instances.length, 2);
  assert.equal(controller.isActive(), true);
});

test('clicking the icon reopens the window; the controller never hides it', () => {
  FakeTray.instances = [];
  const actions = recordingActions();
  const { options } = fakeControllerOptions({ actions });
  createTrayController(options).refresh();
  FakeTray.instances[0].click('click');
  FakeTray.instances[0].click('double-click');
  assert.deepEqual(actions.calls, [['show'], ['show']]);
});

test('a desktop without a tray reports failure instead of hiding the window', () => {
  const errors = [];
  const state = { enabled: true, visible: true, closeToTray: true, panels: [] };
  const controller = createTrayController({
    Tray: class { constructor() { throw new Error('no status notifier'); } },
    Menu: { buildFromTemplate: () => ({}) },
    nativeImage: { createFromPath: () => ({}) },
    iconPath: '/tmp/tinadec.ico',
    getState: () => state,
    actions: recordingActions(),
    onError: (error) => errors.push(error.message),
  });
  assert.equal(controller.refresh(), false);
  assert.equal(controller.isActive(), false);
  assert.deepEqual(errors, ['no status notifier']);
  assert.equal(shouldHideOnClose({ shuttingDown: false, closeToTray: true, trayActive: controller.isActive() }), false);
});

test('the language switch relabels the menu only when the language really changes', () => {
  FakeTray.instances = [];
  const { options } = fakeControllerOptions();
  const controller = createTrayController(options);
  controller.refresh();
  const tray = FakeTray.instances[0];
  const labels = () => tray.menus.at(-1).template.map((item) => item.label ?? item.type);
  assert.equal(labels()[0], TRAY_STRINGS['zh-CN'].show);

  controller.setLocale('en-US');
  assert.equal(labels()[0], TRAY_STRINGS.en.show);
  const rebuilt = tray.menus.length;

  // Same resolved language: a renderer boot in every window must not churn the menu.
  controller.setLocale('en');
  assert.equal(tray.menus.length, rebuilt);

  controller.setLocale('ja-JP');
  assert.equal(labels()[0], TRAY_STRINGS.ja.show);
  assert.equal(controller.getLocale(), 'ja');
});

test('the first hide explains itself once, and only where balloons exist', () => {
  FakeTray.instances = [];
  const { options } = fakeControllerOptions();
  const controller = createTrayController(options);
  controller.refresh();
  const tray = FakeTray.instances[0];
  assert.equal(controller.notifyHidden(), true);
  assert.equal(controller.notifyHidden(), false);
  assert.equal(tray.balloons.length, 1);
  assert.equal(tray.balloons[0].title, TRAY_STRINGS['zh-CN'].hiddenTitle);

  FakeTray.instances = [];
  const mac = createTrayController({ ...fakeControllerOptions().options, platform: 'darwin' });
  mac.refresh();
  assert.equal(mac.notifyHidden(), false);
  assert.equal(FakeTray.instances[0].balloons.length, 0);
});

/**
 * The tray module can be perfect and still not keep the app alive: the behavior lives in main.cjs's
 * close handler, which cannot run without a real Electron window. Pin the wiring directly, the same
 * way mainStartup.test.cjs pins the startup order.
 */
test('main.cjs hides the window on close, quits only when nothing is keeping it alive', () => {
  const source = fs.readFileSync(path.join(__dirname, 'main.cjs'), 'utf8');

  assert.match(source, /win\.on\('close', \(event\) => \{\s*if \(!keepRunningInTray\(\)\) \{[\s\S]{0,240}?companionWindow\.closeCompanionWindow\(\);[\s\S]{0,120}?agentBrowserManager\.closeAll\(\);\s*return;\s*\}\s*event\.preventDefault\(\);\s*win\.hide\(\);[\s\S]{0,80}?\}\);/,
    'closing hides the window only while a tray exists, always prevents the teardown, and takes the companion and agent browser down with it');
  assert.match(source, /function keepRunningInTray\(\) \{\s*return shouldHideOnClose\(\{[\s\S]{0,160}?trayActive: Boolean\(trayController\?\.isActive\(\)\),/,
    'the decision comes from the shared rule, including the tray check');
  assert.match(source, /win\.on\('closed', \(\) => \{ if \(mainWindow === win\) mainWindow = null; \}\);/,
    'a really closed window must not be resurrected by the tray');

  // window-all-closed must not race the hidden window: quit only when nothing keeps it alive.
  assert.match(source, /app\.on\('window-all-closed',[\s\S]{0,400}?if \(process\.platform !== 'darwin' && !keepRunningInTray\(\)\)/,
    'window-all-closed keeps the process alive while the tray does');
  assert.match(source, /quit: \(\) => \{ shuttingDown = true; app\.quit\(\); \}/,
    'the tray quit item raises the flag that lets the close through');
  assert.match(source, /app\.on\('will-quit', \(\) => \{[\s\S]{0,120}?trayController\?\.destroy\(\);/, 'quit removes the icon');

  // A hidden window has no taskbar entry: launching again must reach the running instance.
  assert.match(source, /const hasSingleInstanceLock = !app\.isPackaged \|\| app\.requestSingleInstanceLock\(\);/);
  assert.match(source, /app\.on\('second-instance', \(\) => \{ if \(mainWindow\) void showMainWindow\(\); \}\)/);
  assert.match(source, /if \(!hasSingleInstanceLock\) return;/, 'a losing instance never starts local services');

  // Unchecking the box destroys the icon, so the hidden window has to come back with it.
  assert.match(source, /applyCloseToTray\(enabled\);\s*\/\/ Turning the icon off while the window is hidden would leave nothing to click\.\s*if \(!enabled\) void showMainWindow\(\);/);
});
