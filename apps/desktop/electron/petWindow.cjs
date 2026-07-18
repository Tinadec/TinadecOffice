const { BrowserWindow, screen } = require('electron');
const { randomUUID } = require('node:crypto');
const path = require('node:path');
const { getPetForWindow, getPetPreferences, savePetPreferences, setEnabled } = require('./petStore.cjs');

const isDev = Boolean(process.env.VITE_DEV_SERVER_URL);
const noTransparency = process.env.TINADEC_DISABLE_TRANSPARENCY === '1';
const PET_WINDOW_WIDTH = 300;
const PET_WINDOW_HEIGHT = 320;
const PET_WINDOW_MIN_SIZE = 72;
const PET_WINDOW_MAX_SIZE = 1200;
const petWindows = new Map();

function clampBounds(bounds = {}, offset = 0) {
  const width = Math.round(Math.min(PET_WINDOW_MAX_SIZE, Math.max(PET_WINDOW_MIN_SIZE, Number(bounds.width) || PET_WINDOW_WIDTH)));
  const height = Math.round(Math.min(PET_WINDOW_MAX_SIZE, Math.max(PET_WINDOW_MIN_SIZE, Number(bounds.height) || PET_WINDOW_HEIGHT)));
  const hasPosition = Number.isFinite(bounds.x) && Number.isFinite(bounds.y);
  const display = hasPosition
    ? screen.getDisplayMatching({ x: Math.round(bounds.x), y: Math.round(bounds.y), width, height })
    : screen.getDisplayNearestPoint(screen.getCursorScreenPoint());
  const area = display.workArea;
  const defaultX = area.x + area.width - width - 28 - offset;
  const defaultY = area.y + area.height - height - 28 - offset;
  const x = hasPosition ? Math.round(bounds.x) : defaultX;
  const y = hasPosition ? Math.round(bounds.y) : defaultY;

  return {
    x: Math.max(area.x, Math.min(x, area.x + area.width - width)),
    y: Math.max(area.y, Math.min(y, area.y + area.height - height)),
    width,
    height,
  };
}

function petWindowUrl(instanceId) {
  const hash = `/pet?instanceId=${encodeURIComponent(instanceId)}`;
  if (isDev) return `${process.env.VITE_DEV_SERVER_URL}?splash=0#${hash}`;
  return { file: path.join(__dirname, '..', 'dist', 'index.html'), hash, query: { splash: '0' } };
}

async function createPetWindow(petId) {
  if (!petId) throw new Error('A downloaded pet id is required');
  for (const [instanceId, entry] of petWindows) {
    if (entry.petId === petId && !entry.window.isDestroyed()) {
      entry.window.show();
      entry.window.focus();
      return { instanceId, windowId: entry.window.id };
    }
  }
  const instanceId = randomUUID();
  const preferences = await getPetPreferences(petId);
  if (!preferences) throw new Error('Pet is not downloaded');
  const scale = preferences.scale;
  const bounds = clampBounds(preferences.window || {
    width: 192 * scale,
    height: 208 * scale,
  }, petWindows.size * 24);
  const win = new BrowserWindow({
    ...bounds,
    title: 'Tinadec Pet',
    icon: path.join(__dirname, '..', isDev ? 'public' : 'dist', 'tinadec.ico'),
    frame: false,
    transparent: !noTransparency,
    backgroundColor: noTransparency ? '#1e1e2e' : '#00000000',
    hasShadow: false,
    thickFrame: false,
    resizable: false,
    maximizable: false,
    minimizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    alwaysOnTop: true,
    show: false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, 'preload.cjs'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      webSecurity: false,
    },
  });

  win.setAlwaysOnTop(true, process.platform === 'darwin' ? 'floating' : 'normal');
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  const entry = { window: win, petId, scale, saveTimer: null };
  petWindows.set(instanceId, entry);
  const saveBounds = () => {
    clearTimeout(entry.saveTimer);
    entry.saveTimer = setTimeout(() => {
      if (!win.isDestroyed()) savePetPreferences(petId, { scale: entry.scale, window: win.getBounds() }).catch(() => {});
    }, 150);
  };
  win.on('move', saveBounds);
  win.on('resize', saveBounds);
  win.on('closed', () => {
    clearTimeout(entry.saveTimer);
    petWindows.delete(instanceId);
  });
  win.once('ready-to-show', () => win.show());

  const target = petWindowUrl(instanceId);
  if (typeof target === 'string') {
    await win.loadURL(target);
  } else {
    await win.loadFile(target.file, { hash: target.hash, query: target.query });
  }

  return { instanceId, windowId: win.id };
}

function closePetWindow(instanceId) {
  const entry = petWindows.get(instanceId);
  if (!entry || entry.window.isDestroyed()) return false;
  entry.window.close();
  return true;
}

function closePetWindowForPet(petId) {
  for (const [instanceId, entry] of petWindows) {
    if (entry.petId === petId) return closePetWindow(instanceId);
  }
  return false;
}

function closeAllPetWindows() {
  for (const entry of petWindows.values()) {
    if (!entry.window.isDestroyed()) entry.window.close();
  }
  petWindows.clear();
}

function listPetWindows() {
  return Array.from(petWindows, ([instanceId, entry]) => ({
    instanceId,
    petId: entry.petId,
    windowId: entry.window.id,
    bounds: entry.window.getBounds(),
  }));
}

async function getPetWindowPet(instanceId) {
  const entry = petWindows.get(instanceId);
  return entry ? getPetForWindow(entry.petId) : null;
}

function entryForSender(sender) {
  for (const entry of petWindows.values()) {
    if (!entry.window.isDestroyed() && entry.window.webContents.id === sender.id) return entry;
  }
  return null;
}

async function getCurrentPetWindowPet(sender) {
  const entry = entryForSender(sender);
  return entry ? getPetForWindow(entry.petId) : null;
}

function setCurrentPetWindowBounds(sender, input = {}) {
  const entry = entryForSender(sender);
  if (!entry) return false;
  if (Number.isFinite(input.scale)) entry.scale = Math.min(1.2, Math.max(0.18, input.scale));
  entry.window.setBounds(clampBounds({ ...entry.window.getBounds(), ...input }));
  return true;
}

function setCurrentPetWindowClickThrough(sender, clickThrough) {
  const entry = entryForSender(sender);
  if (!entry) return false;
  entry.window.setIgnoreMouseEvents(Boolean(clickThrough), { forward: true });
  return true;
}

async function closeCurrentPetWindow(sender) {
  const entry = entryForSender(sender);
  if (!entry) return null;
  const pet = await setEnabled(entry.petId, false);
  entry.window.close();
  return pet;
}

module.exports = {
  createPetWindow,
  closePetWindow,
  closePetWindowForPet,
  closeAllPetWindows,
  closeCurrentPetWindow,
  getCurrentPetWindowPet,
  getPetWindowPet,
  listPetWindows,
  setCurrentPetWindowBounds,
  setCurrentPetWindowClickThrough,
};
