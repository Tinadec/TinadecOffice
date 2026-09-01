/**
 * Terminal Manager — Electron main process PTY management.
 *
 * Uses node-pty for real pseudo-terminal support when available, with a
 * graceful fallback to child_process.spawn for environments where the
 * native module cannot be loaded (e.g. ABI mismatch or missing build tools).
 *
 * Each terminal is identified by a unique string ID. Data and exit events
 * are sent back to the renderer via IPC channels scoped per terminal ID.
 */

const { ipcMain, BrowserWindow, app } = require('electron');
const childProcess = require('child_process');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');

// ---- PTY backend selection ----
// node-pty resolves its native addon lazily on Windows, so a successful `require`
// proves nothing: the real verdict only arrives when `pty.spawn` throws.
let pty = null;
let ptyLoadError = null;
try {
  pty = require('node-pty');
} catch (err) {
  ptyLoadError = err.message;
  console.warn('[terminalManager] node-pty not available:', err.message);
}

/**
 * @typedef {Object} TerminalEntry
 * @property {any} [process]            - node-pty IPty instance or ChildProcess
 * @property {string} id                - unique terminal ID
 * @property {string} shell             - shell executable path
 * @property {string[]} args            - shell arguments
 * @property {string} cwd               - working directory
 * @property {number} cols              - terminal width in columns
 * @property {number} rows              - terminal height in rows
 * @property {boolean} exited           - whether the process has exited
 * @property {'pty'|null} backend      - what was actually spawned
 * @property {number|null} exitCode     - last reported exit code
 * @property {string} title             - terminal display title
 * @property {number} [ownerWebContentsId] - webContents that created the terminal
 * @property {string[]} pending         - output awaiting the coalescing flush
 * @property {number} pendingTimer      - handle of the armed flush, 0 when idle
 * @property {string[]} replay          - bounded recent output for late subscribers
 * @property {number} replayBytes       - total bytes held in `replay`
 */

/** @type {Map<string, TerminalEntry>} */
const terminals = new Map();

let terminalCounter = 0;

/**
 * Generate a unique terminal ID.
 * @returns {string}
 */
function generateId() {
  return `term-${++terminalCounter}`;
}

// ---- Shell profiles ----

const systemRoot = () => process.env.SystemRoot || process.env.windir || 'C:\\Windows';
const programFiles = () => process.env.ProgramFiles || 'C:\\Program Files';
const localAppData = () => process.env.LOCALAPPDATA || '';

function firstExisting(candidates) {
  for (const candidate of candidates) {
    if (!candidate) continue;
    try {
      if (fs.existsSync(candidate)) return candidate;
    } catch {
      // Unreadable candidate path: skip it.
    }
  }
  return null;
}

/**
 * Shell catalog for this machine, built from absolute paths only.
 *
 * A bare `'powershell.exe'` depends on a PATH lookup the spawned process may not
 * inherit, and `process.env.SHELL` is meaningless on Windows — when Electron is
 * launched from Git Bash it holds an MSYS path node-pty cannot spawn.
 * @returns {Array<{id: string, label: string, shell: string, args: string[]}>}
 */
function resolveStaticShells() {
  if (process.platform === 'win32') {
    const shells = [];
    const pwsh = firstExisting([
      path.join(programFiles(), 'PowerShell', '7', 'pwsh.exe'),
      path.join(programFiles(), 'PowerShell', '6', 'pwsh.exe'),
      path.join(localAppData(), 'Programs', 'PowerShell', '7', 'pwsh.exe'),
    ]);
    if (pwsh) {
      shells.push({ id: 'pwsh', label: 'PowerShell 7', shell: pwsh, args: ['-NoLogo'] });
    }
    shells.push({
      id: 'powershell',
      label: 'Windows PowerShell',
      shell: path.join(systemRoot(), 'System32', 'WindowsPowerShell', 'v1.0', 'powershell.exe'),
      args: ['-NoLogo'],
    });
    shells.push({
      id: 'cmd',
      label: 'Command Prompt',
      shell: process.env.ComSpec || path.join(systemRoot(), 'System32', 'cmd.exe'),
      args: [],
    });
    const gitBash = firstExisting([
      path.join(programFiles(), 'Git', 'bin', 'bash.exe'),
      path.join(localAppData(), 'Programs', 'Git', 'bin', 'bash.exe'),
    ]);
    if (gitBash) {
      shells.push({ id: 'gitbash', label: 'Git Bash', shell: gitBash, args: ['--login', '-i'] });
    }
    return shells;
  }

  if (process.platform === 'darwin') {
    return [
      { id: 'zsh', label: 'zsh', shell: '/bin/zsh', args: ['-l'] },
      { id: 'bash', label: 'bash', shell: '/bin/bash', args: ['-l'] },
    ];
  }

  const shells = [{ id: 'bash', label: 'bash', shell: '/bin/bash', args: ['-l'] }];
  try {
    if (fs.existsSync('/bin/zsh')) shells.push({ id: 'zsh', label: 'zsh', shell: '/bin/zsh', args: ['-l'] });
  } catch { /* ignore */ }
  return shells;
}

/** @type {{id: string, label: string, shell: string, args: string[]} | null} */
let wslProfile = null;
let wslProbed = false;
/** @type {Array<{id: string, label: string, shell: string, args: string[]}> | null} */
let shellCatalog = null;

/**
 * Detect WSL once, off the create path. `wsl --list` used to run through execSync
 * inside every `terminal:create` and blocked Electron's main process for up to 3s.
 */
function probeWsl() {
  if (process.platform !== 'win32' || wslProbed) return;
  wslProbed = true;
  childProcess.execFile(
    'wsl.exe',
    ['--list', '--quiet'],
    { encoding: 'utf-8', timeout: 3000, windowsHide: true },
    (err, stdout) => {
      if (!err && stdout && stdout.trim()) {
        wslProfile = { id: 'wsl', label: 'WSL', shell: path.join(systemRoot(), 'System32', 'wsl.exe'), args: [] };
        shellCatalog = null;
      }
      console.log('[terminalManager] WSL probe:', wslProfile ? 'available' : 'none');
    },
  );
}

/**
 * Detect available shell profiles on the current platform (cached: it is a pure
 * function of the machine).
 * @returns {Array<{id: string, label: string, shell: string, args: string[]}>}
 */
function getAvailableShells() {
  if (!shellCatalog) {
    shellCatalog = resolveStaticShells();
    if (wslProfile) shellCatalog = [...shellCatalog, wslProfile];
  }
  return shellCatalog;
}

/**
 * Get the default shell for the current platform.
 * @returns {{shell: string, args: string[]}}
 */
function getDefaultShell() {
  const preferred = getAvailableShells()[0];
  if (preferred) return { shell: preferred.shell, args: preferred.args };
  return process.platform === 'win32'
    ? { shell: path.join(systemRoot(), 'System32', 'cmd.exe'), args: [] }
    : { shell: process.env.SHELL || '/bin/sh', args: [] };
}

/**
 * Resolve environment variables for the terminal process.
 * Merges process.env with a clean TERM setting, drops the npm and colour overrides
 * a packaged Electron inherits, and filters out undefined values.
 * @param {Record<string, string>} extra
 * @returns {Record<string, string>}
 */
function buildEnv(extra = {}) {
  const cleanEnv = {};
  for (const [key, value] of Object.entries(process.env)) {
    if (value === undefined || value === null) continue;
    if (key.startsWith('npm_config_') || key.startsWith('npm_package_')) continue;
    if (key === 'NO_COLOR' || key === 'FORCE_COLOR' || key === 'COLORFGBG') continue;
    cleanEnv[key] = value;
  }

  return {
    ...cleanEnv,
    TERM: 'xterm-256color',
    COLORTERM: 'truecolor',
    LANG: process.env.LANG || 'en_US.UTF-8',
    ...extra,
  };
}

/**
 * Create a new terminal process.
 *
 * @param {Object} options
 * @param {string} [options.id]          - pre-assigned terminal ID
 * @param {string} [options.shell]       - shell executable (defaults to platform default)
 * @param {string[]} [options.args]      - shell arguments
 * @param {string} [options.cwd]         - working directory
 * @param {number} [options.cols=80]     - initial terminal width
 * @param {number} [options.rows=24]     - initial terminal height
 * @param {string} [options.title]       - terminal display title
 * @param {number} [ownerWebContentsId]  - webContents that asked for this terminal
 * @returns {{id: string|null, shell?: string, title?: string, backend: 'pty'|null, error?: string}}
 */
function createTerminal(options = {}, ownerWebContentsId = null) {
  const id = options.id || generateId();
  const defaultShell = getDefaultShell();
  const shell = options.shell || defaultShell.shell;
  const args = options.args || defaultShell.args;
  const cwd = options.cwd || process.env.HOME || process.env.USERPROFILE || os.homedir();
  const cols = options.cols || 80;
  const rows = options.rows || 24;
  const title = options.title || path.basename(shell);

  console.log('[terminalManager] Creating terminal:', { id, shell, args, cwd, cols, rows, title, ptyAvailable: !!pty });

  if (!pty) {
    return { id: null, backend: null, error: ptyLoadError || 'node-pty is not available in this build' };
  }

  // A stale project directory would otherwise surface as an opaque spawn failure.
  if (options.cwd) {
    try {
      if (!fs.existsSync(cwd) || !fs.statSync(cwd).isDirectory()) {
        return { id: null, backend: null, error: `Working directory is not available: ${cwd}` };
      }
    } catch (err) {
      return { id: null, backend: null, error: `Working directory is not readable: ${cwd} (${err.message})` };
    }
  }

  const env = buildEnv();

  /** @type {TerminalEntry} */
  const entry = {
    id,
    shell,
    args,
    cwd,
    cols,
    rows,
    exited: false,
    backend: 'pty',
    exitCode: null,
    title,
    ownerWebContentsId,
    pending: [],
    pendingTimer: 0,
    replay: [],
    replayBytes: 0,
    process: null,
  };

  try {
    const ptyProcess = pty.spawn(shell, args, {
      // On the Windows ConPTY path `name` does not set the child's TERM, so the
      // value stays authoritative in buildEnv(); the two must agree either way.
      name: 'xterm-256color',
      cols,
      rows,
      cwd,
      env,
    });

    entry.process = ptyProcess;

    ptyProcess.onData((data) => {
      notifyData(id, data);
    });

    ptyProcess.onExit(({ exitCode, signal }) => {
      entry.exited = true;
      entry.exitCode = exitCode;
      flushPending(id);
      notifyExit(id, exitCode, signal);
    });
  } catch (err) {
    console.error('[terminalManager] Failed to spawn PTY:', err.message);
    return { id: null, backend: null, error: err.message };
  }

  terminals.set(id, entry);
  return { id, shell, title, backend: entry.backend };
}

/**
 * Write data to a terminal's input.
 * @param {string} id - terminal ID
 * @param {string} data - data to write
 */
function writeTerminal(id, data) {
  const entry = terminals.get(id);
  if (!entry || entry.exited || !entry.process) return;

  entry.process.write(data);
}

/**
 * Resize a terminal.
 * @param {string} id - terminal ID
 * @param {number} cols - new column count
 * @param {number} rows - new row count
 */
function resizeTerminal(id, cols, rows) {
  const entry = terminals.get(id);
  if (!entry || entry.exited || !entry.process) return;

  // The size comes from the renderer's fit calculation; a zero measured against a
  // hidden host would make node-pty throw.
  if (!(cols >= 1) || !(rows >= 1)) return;

  entry.cols = cols;
  entry.rows = rows;

  try {
    entry.process.resize(cols, rows);
  } catch {
    // The shell may have exited between the check and the resize.
  }
}

/**
 * Destroy a terminal and kill its process.
 * @param {string} id - terminal ID
 */
function destroyTerminal(id) {
  const entry = terminals.get(id);
  if (!entry) return;

  if (!entry.exited && entry.process) {
    try {
      entry.process.kill();
    } catch {
      // Process may have already exited
    }
  }

  if (entry.pendingTimer) {
    clearTimeout(entry.pendingTimer);
    entry.pendingTimer = 0;
  }

  terminals.delete(id);
}

/**
 * Destroy all terminals (called on app quit).
 */
function destroyAllTerminals() {
  for (const id of terminals.keys()) {
    destroyTerminal(id);
  }
}

/**
 * Get info about a terminal.
 * @param {string} id
 * @returns {TerminalEntry | undefined}
 */
function getTerminal(id) {
  return terminals.get(id);
}

/**
 * List all active terminals.
 * @returns {Array<{id: string, shell: string, title: string, exited: boolean}>}
 */
function listTerminals() {
  const result = [];
  for (const [, entry] of terminals) {
    result.push({
      id: entry.id,
      shell: entry.shell,
      title: entry.title,
      exited: entry.exited,
    });
  }
  return result;
}

// ---- Output routing ----

/**
 * Windows allowed to receive terminal output. Defaults to every window so this
 * module stays usable before the host wires a filter in.
 * @type {() => import('electron').BrowserWindow[]}
 */
let terminalHostFilter = () => BrowserWindow.getAllWindows();

/**
 * Declare which windows may host a terminal view.
 * @param {() => import('electron').BrowserWindow[]} filter
 */
function setTerminalHostFilter(filter) {
  if (typeof filter === 'function') terminalHostFilter = filter;
}

function sendToTerminalHosts(channel, payload) {
  for (const win of terminalHostFilter()) {
    if (win && !win.isDestroyed()) {
      win.webContents.send(channel, payload);
    }
  }
}

/** Coalescing window: one IPC send per flush instead of one per pty chunk. */
const OUTPUT_FLUSH_MS = 16;
/** Recent output kept per terminal so a late subscriber can catch up. */
const REPLAY_LIMIT_BYTES = 200 * 1024;

/**
 * Emit whatever output has accumulated for a terminal.
 * @param {string} id
 */
function flushPending(id) {
  const entry = terminals.get(id);
  if (!entry) return;
  if (entry.pendingTimer) {
    clearTimeout(entry.pendingTimer);
    entry.pendingTimer = 0;
  }
  if (!entry.pending.length) return;
  const chunk = entry.pending.join('');
  entry.pending = [];
  sendToTerminalHosts(`terminal:data:${id}`, chunk);
}

/**
 * Deliver shell output: retain it for replay and queue it for the next flush.
 * @param {string} id
 * @param {string} data
 */
function notifyData(id, data) {
  const entry = terminals.get(id);
  if (!entry) return;

  entry.replay.push(data);
  entry.replayBytes += data.length;
  while (entry.replayBytes > REPLAY_LIMIT_BYTES && entry.replay.length > 1) {
    entry.replayBytes -= entry.replay.shift().length;
  }

  entry.pending.push(data);
  if (!entry.pendingTimer) {
    entry.pendingTimer = setTimeout(() => {
      entry.pendingTimer = 0;
      flushPending(id);
    }, OUTPUT_FLUSH_MS);
  }
}

/**
 * Report a terminal's exit. Pending output is flushed first so the last lines are
 * never lost behind the exit notification.
 * @param {string} id
 * @param {number} exitCode
 * @param {number} [signal]
 */
function notifyExit(id, exitCode, signal) {
  flushPending(id);
  sendToTerminalHosts(`terminal:exit:${id}`, { exitCode, signal });
}

/**
 * Everything a late-attaching view needs to catch up before it starts listening.
 * @param {string} id
 * @returns {{replay: string, exited: boolean, exitCode: number|null} | null}
 */
function readTerminalSnapshot(id) {
  const entry = terminals.get(id);
  if (!entry) return null;
  return {
    replay: entry.replay.join(''),
    exited: entry.exited,
    exitCode: entry.exitCode,
  };
}

// ---- IPC Handler Registration ----

/**
 * Register all terminal-related IPC handlers.
 * Should be called once during app initialization.
 * @param {{hostFilter?: () => import('electron').BrowserWindow[]}} [options]
 */
function registerTerminalIpc(registration = {}) {
  setTerminalHostFilter(registration.hostFilter);
  probeWsl();

  // Create a new terminal
  ipcMain.handle('terminal:create', async (event, options) => {
    return createTerminal(options || {}, event.sender.id);
  });

  // Write data to a terminal
  ipcMain.on('terminal:write', (_event, id, data) => {
    writeTerminal(id, data);
  });

  // Resize a terminal
  ipcMain.on('terminal:resize', (_event, id, cols, rows) => {
    resizeTerminal(id, cols, rows);
  });

  // Destroy a terminal
  ipcMain.on('terminal:destroy', (_event, id) => {
    destroyTerminal(id);
  });

  // Replay what a terminal has printed so far
  ipcMain.handle('terminal:snapshot', async (_event, id) => {
    return readTerminalSnapshot(id);
  });

  // Get available shell profiles
  ipcMain.handle('terminal:get-shells', async () => {
    return getAvailableShells();
  });

  // List all active terminals
  ipcMain.handle('terminal:list', async () => {
    return listTerminals();
  });
}

module.exports = {
  createTerminal,
  writeTerminal,
  resizeTerminal,
  destroyTerminal,
  destroyAllTerminals,
  getTerminal,
  listTerminals,
  getAvailableShells,
  getDefaultShell,
  readTerminalSnapshot,
  setTerminalHostFilter,
  registerTerminalIpc,
};
