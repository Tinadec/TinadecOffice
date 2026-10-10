const { app, BrowserWindow } = require('electron');
const assert = require('node:assert/strict');
const { mkdtempSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
const evidence = process.env.SETTINGS_UI_EVIDENCE;
app.commandLine.appendSwitch('no-proxy-server');
app.setPath('userData', mkdtempSync(join(tmpdir(), 'tinadec-settings-electron-')));
const events = [];
const timeout = setTimeout(() => { console.error('Settings UI acceptance timeout'); app.exit(1); }, 240_000);
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function waitFor(contents, expression) {
  for (let i = 0; i < 240; i++) {
    if (await contents.executeJavaScript(expression)) return;
    await delay(250);
  }
  throw new Error('UI condition timeout: ' + expression);
}
app.whenReady().then(async () => {
  const window = new BrowserWindow({ show: false, width: 1400, height: 1000, webPreferences: { sandbox: true, contextIsolation: true, nodeIntegration: false, backgroundThrottling: false, offscreen: true } });
  const js = expression => window.webContents.executeJavaScript(expression);
  window.webContents.on('console-message', (_event, level, message) => {
    if (level >= 2 && /error|failed|not defined|not a function|SyntaxError/i.test(message)) console.error(message.slice(0, 800));
  });
  window.webContents.on('dom-ready', () => console.log('Owned Settings document DOM ready.'));
  window.webContents.on('did-finish-load', () => console.log('Owned Settings document loaded.'));
  const control = mode => js(`fetch('/__settings_control', {method:'POST',body:${JSON.stringify(mode)}}).then(r=>r.json())`);
  try {
    void window.loadURL(process.env.SETTINGS_UI_URL).catch(error => console.error(error.message));
    await waitFor(window.webContents, "Array.from(document.querySelectorAll('.settings-nav-item')).some(el=>el.innerText.includes('智能体中心'))");
    await js("Array.from(document.querySelectorAll('.settings-nav-item')).find(el=>el.innerText.includes('智能体中心')).click()");
    await waitFor(window.webContents, "document.querySelectorAll('.agent-card').length === 20 && document.querySelector('textarea.prompt-editor') !== null");
    const first = await control('stats');
    assert.ok(first.network_drops >= 1);
    assert.equal(first.agent_reads, 2);
    assert.equal(first.configuration_or_install_writes, 0);
    events.push({ step: 'initial_network_loss', displayed_agents: 20, agent_read_attempts: 2, automatic_read_recovery: true });
    // Redact prompt/description fields in screenshots only; the editable value is
    // checked by equality in memory and never recorded in the evidence.
    await js("document.head.insertAdjacentHTML('beforeend','<style>textarea{color:transparent!important;text-shadow:none!important}</style>')");
    await delay(400);
    writeFileSync(join(evidence, 'desktop-agents-loaded.png'), (await window.webContents.capturePage()).toPNG());
    await js("(()=>{const el=document.querySelector('textarea.prompt-editor');el.value='Isolated unsaved draft';el.dispatchEvent(new Event('input',{bubbles:true}));})()");
    await control('persistent');
    await js("document.querySelector('.agent-center-merged > .center-command-bar button').click()");
    await waitFor(window.webContents, "document.querySelector('[data-testid=agent-center-read-failure]') !== null");
    assert.equal(await js("document.querySelectorAll('.agent-card').length"), 20);
    assert.equal(await js("document.querySelector('textarea.prompt-editor').value"), 'Isolated unsaved draft');
    const failed = await control('stats');
    assert.equal(failed.agent_reads, 5);
    events.push({ step: 'persistent_network_loss', retained_agents: 20, read_attempts_in_refresh: 3, inline_failure: true, draft_preserved: true });
    await delay(400);
    writeFileSync(join(evidence, 'desktop-agents-read-failure.png'), (await window.webContents.capturePage()).toPNG());
    await control('healthy');
    await js("document.querySelector('[data-testid=agent-center-read-failure] button').click()");
    await waitFor(window.webContents, "document.querySelector('[data-testid=agent-center-read-failure]') === null");
    assert.equal(await js("document.querySelector('textarea.prompt-editor').value"), 'Isolated unsaved draft');
    assert.equal((await control('stats')).agent_reads, 6);
    events.push({ step: 'explicit_read_retry', displayed_agents: 20, read_requests: 1, draft_preserved: true });
    await js("window.__settingsFixture.host('unavailable')");
    await delay(100);
    await js("window.__settingsFixture.host('ready')");
    await waitFor(window.webContents, "!document.querySelector('.agent-center-merged > .center-command-bar button').disabled");
    assert.equal(await js("document.querySelector('textarea.prompt-editor').value"), 'Isolated unsaved draft');
    const final = await control('stats');
    assert.equal(final.agent_reads, 7);
    assert.equal(final.configuration_or_install_writes, 0);
    assert.equal(final.pack_preview_posts, first.pack_preview_posts);
    events.push({ step: 'host_recovery', read_refreshes: 1, draft_preserved: true, writes_replayed: 0 });
    writeFileSync(join(evidence, 'desktop-ui-acceptance.json'), JSON.stringify({ checked_at: new Date().toISOString(), electron: process.versions.electron, real_settings_page: true, real_core_gateway: true, isolated_exact_toml_copy: true, main_preload: 'fixture_status_bridge_not_product_main', events, requests: final }, null, 2) + '\n');
    console.log(JSON.stringify({ accepted: true, events: events.length, electron: process.versions.electron }));
    clearTimeout(timeout); window.destroy(); app.exit(0);
  } catch (error) {
    console.error(error.stack);
    try { writeFileSync(join(evidence, 'desktop-failure.png'), (await window.webContents.capturePage()).toPNG()); } catch {}
    clearTimeout(timeout); window.destroy(); app.exit(1);
  }
});
