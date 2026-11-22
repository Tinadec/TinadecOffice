const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

/**
 * Why this is a source assertion rather than a behavioral one: exercising the real
 * whenReady() path needs the whole main-process surface (twenty require stubs plus a
 * BrowserWindow double), and the failure being guarded is purely one of ordering. A
 * functional version of this test would re-assert the same two lines of source by
 * proxying them through mocks, so it would break for unrelated reasons while catching
 * the same regression. Pin the ordering directly.
 */
const source = fs.readFileSync(path.join(__dirname, 'main.cjs'), 'utf8');

test('startup never blocks the first paint on host verification', () => {
  const hostStart = source.indexOf('const hostStartup = hostConnection.start();');
  assert.ok(hostStart >= 0, 'host verification is started and its promise kept');

  // Packaged startup may spend a full bounded wait (90s x2) bringing Core and Gateway up.
  // Awaiting it before the window exists meant a blank screen for the whole wait
  // (measured 11-13 s of Core boot on this machine alone).
  assert.equal(source.includes('await hostConnection.start()'), false,
    'host verification must not be awaited inline: that gates the window behind it');

  const createWindow = source.indexOf('await createWindow();');
  assert.ok(createWindow >= 0, 'the first window is still created on the startup path');
  assert.ok(hostStart < createWindow,
    'verification must be started before the window is created, not after it');

  // The settled result still has to be reported; dropping it would hide startup failures.
  assert.match(source, /hostStartup\.then\(/,
    'the deferred verification result is still reported');

  // Trust is granted per request from trustedHostReady, so painting early cannot authorize
  // anything: trustedHostReady only flips inside publishHostStatus.
  assert.match(source, /function publishHostStatus\(snapshot\)\s*{\s*trustedHostReady = snapshot\.managed && snapshot\.state === 'ready';/,
    'business authority still follows verified host state, not window existence');
});

test('main window chrome delegates to platform-aware option builder and wires fullscreen IPC + darwin menu', () => {
  assert.match(source, /buildMainWindowOptions\(process\.platform/,
    'createWindow uses buildMainWindowOptions so darwin gets trafficLightPosition without .ico');
  assert.match(source, /attachWindowFullScreenEvents\(win\)/,
    'main window forwards enter-full-screen and leave-full-screen to renderer');
  assert.match(
    source,
    /ipcMain\.handle\('tinadec:window-is-fullscreen',\s*\(event\)\s*=>\s*BrowserWindow\.fromWebContents\(event\.sender\)\?\.isFullScreen\(\)\s*\?\?\s*false\)/,
    'tinadec:window-is-fullscreen IPC handler queries sender BrowserWindow fullscreen state',
  );
  assert.match(source, /if\s*\(process\.platform\s*===\s*'darwin'\)\s*{[\s\S]*?Menu\.setApplicationMenu\(/,
    'darwin sets native application menu with standard Edit/View/Window roles');
  assert.match(source, /ipcMain\.handle\('tinadec:companion-drag-move'/,
    'companion 2D drag-move IPC glue is registered');
  assert.match(source, /ipcMain\.handle\('tinadec:companion-drag-end'/,
    'companion 2D drag-end IPC glue is registered');
  assert.match(source, /ipcMain\.handle\('tinadec:companion-open-main'[\s\S]*?tinadec:navigate-surface/,
    'companion openMainSurface IPC glue restores main window and emits tinadec:navigate-surface');
});
