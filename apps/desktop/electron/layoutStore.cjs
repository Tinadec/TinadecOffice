// layoutStore.cjs — Workbench layout persistence for the Electron main process.
//
// Stores versioned Workbench layout snapshots under userData/workbench-layout.json.
// Writes are atomic (temp file + rename) so a crash never leaves a corrupt file.
// The renderer owns the layout semantics; the main process is a thin store that
// validates the payload shape and persists it.

const { app } = require('electron');
const fs = require('node:fs/promises');
const path = require('node:path');

const MAX_BYTES = 4 * 1024 * 1024; // 4 MB safety cap
const VERSION = 1;

// Test hook: allows tests to point at a temp dir instead of real userData.
let __userDataDir = null;
function __setUserDataDir(dir) {
  __userDataDir = dir;
}

function layoutPath() {
  const base = __userDataDir ?? app.getPath('userData');
  return path.join(base, 'workbench-layout.json');
}

/** Validate the top-level shape of a layout blob. Returns error string or null. */
function validateLayoutPayload(payload) {
  if (!payload || typeof payload !== 'object') return 'payload must be an object';
  if (typeof payload.version !== 'number') return 'version must be a number';
  // StorageBlob shape: { version, globalByPage?, pageByPageId?, workspaceByKey? }
  for (const key of ['globalByPage', 'pageByPageId', 'workspaceByKey']) {
    if (payload[key] !== undefined && (typeof payload[key] !== 'object' || payload[key] === null)) {
      return `${key} must be an object`;
    }
  }
  return null;
}

async function load() {
  try {
    const raw = await fs.readFile(layoutPath(), 'utf-8');
    const payload = JSON.parse(raw);
    const error = validateLayoutPayload(payload);
    if (error) {
      console.warn('[layoutStore] invalid layout payload:', error);
      return null;
    }
    return payload;
  } catch (err) {
    if (err.code === 'ENOENT') return null;
    // Corrupt JSON / unreadable file — return null so the renderer falls back
    // to the built-in preset rather than crashing.
    console.warn('[layoutStore] failed to read layout:', err.message);
    return null;
  }
}

async function save(payload) {
  const error = validateLayoutPayload(payload);
  if (error) throw new Error(`[layoutStore] refusing to save invalid payload: ${error}`);

  const dir = path.dirname(layoutPath());
  await fs.mkdir(dir, { recursive: true });
  const tmp = `${layoutPath()}.tmp-${process.pid}`;
  const json = JSON.stringify(payload, null, 2);
  if (Buffer.byteLength(json) > MAX_BYTES) {
    throw new Error('[layoutStore] payload exceeds size cap');
  }
  await fs.writeFile(tmp, json, 'utf-8');
  await fs.rename(tmp, layoutPath());
}

module.exports = { load, save, validateLayoutPayload, layoutPath, VERSION, __setUserDataDir };
