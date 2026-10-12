const { app, BrowserWindow } = require('electron');
const { mkdtempSync, writeFileSync } = require('node:fs');
const { tmpdir } = require('node:os');
const { join } = require('node:path');
app.setPath('userData', mkdtempSync(join(tmpdir(), 'tinadec-terminal-acceptance-')));
app.commandLine.appendSwitch('no-proxy-server');
const deadline = setTimeout(() => { console.error('Terminal fixture timeout'); app.exit(1); }, 60000);
app.whenReady().then(async () => {
  const window = new BrowserWindow({ show: false, width: 1000, height: 800, webPreferences: { sandbox: true, contextIsolation: true, nodeIntegration: false, backgroundThrottling: false, offscreen: true } });
  try {
    await window.loadURL(process.env.TERMINAL_FIXTURE_URL);
    let captured = false;
    let result;
    for (let step = 0; step < 200; step++) {
      result = await window.webContents.executeJavaScript('window.__terminalResult');
      if (!captured && await window.webContents.executeJavaScript('window.__terminalScreenshotReady')) {
        writeFileSync(join(process.env.TERMINAL_FIXTURE_EVIDENCE, 'terminal-xterm6.png'), (await window.webContents.capturePage()).toPNG());
        captured = true;
      }
      if (result) break;
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    if (!result) throw new Error('Missing fixture result');
    result.electron = process.versions.electron;
    result.checked_at = new Date().toISOString();
    writeFileSync(join(process.env.TERMINAL_FIXTURE_EVIDENCE, 'terminal-acceptance.json'), JSON.stringify(result, null, 2) + '\n');
    console.log(JSON.stringify(result));
    clearTimeout(deadline);
    window.destroy();
    app.exit(result.passed ? 0 : 1);
  } catch (error) {
    console.error(error.stack);
    clearTimeout(deadline); window.destroy(); app.exit(1);
  }
});
