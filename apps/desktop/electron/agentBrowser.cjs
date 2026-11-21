/**
 * Electron side of the agent browser: one satellite window per agent session, one
 * `WebContentsView` per tab, and a debugger session attached to each tab.
 *
 * Nothing here knows what a snapshot or a ref is. It exposes the CDP commands the .NET
 * tool process may send (through browserBridge.cjs), the window/tab lifecycle, the
 * agent/user control state, and the hard-stop facts Electron learns first (popups,
 * permission prompts, downloads, file choosers, dialogs).
 */

const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');

const PARTITION = 'persist:tinadec-agent-browser';
const TOOLBAR_HEIGHT = 48;
const MAX_TABS = 8;
const MAX_NOTICES = 20;

function codedError(code, message, permanent = true) {
  const error = new Error(message ?? code);
  error.code = code;
  error.permanent = permanent;
  return error;
}

function parseHttpUrl(raw) {
  if (typeof raw !== 'string' || raw.length > 4096) return null;
  let url;
  try { url = new URL(raw); } catch { return null; }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return null;
  if (url.username || url.password || !url.hostname) return null;
  return url;
}

function isHttpUrl(raw) {
  return parseHttpUrl(raw) !== null;
}

function createAgentBrowserManager({
  BrowserWindow,
  WebContentsView,
  shell,
  app,
  shellUrl,
  preloadPath,
  stateDir,
  stringsForLocale,
  logger = console,
  now = () => new Date(),
  randomUUID = crypto.randomUUID,
  platform = process.platform,
  nativeImage = null,
}) {
  if (!BrowserWindow || !WebContentsView) throw new TypeError('createAgentBrowserManager requires Electron constructors');
  if (typeof shellUrl !== 'function') throw new TypeError('createAgentBrowserManager requires shellUrl()');
  if (typeof preloadPath !== 'string') throw new TypeError('createAgentBrowserManager requires preloadPath');

  const sessions = new Map();
  const shellSenders = new Map(); // shell webContents.id -> sessionId
  const wiredSessions = new WeakSet();
  let locale = app?.getLocale?.() ?? 'zh-CN';

  function labels() {
    return typeof stringsForLocale === 'function'
      ? stringsForLocale(locale)
      : { title: 'Agent Browser', back: 'Back', forward: 'Forward', reload: 'Reload' };
  }

  function notice(session, code, data = {}) {
    const entry = { seq: ++session.noticeSeq, code, at: now().toISOString(), ...data };
    session.notices.push(entry);
    if (session.notices.length > MAX_NOTICES) session.notices.splice(0, session.notices.length - MAX_NOTICES);
    pushState(session);
  }

  function tabInfo(tab) {
    return {
      id: tab.id,
      url: tab.url,
      title: tab.title,
      loading: tab.loading,
      active: Boolean(tab.active),
    };
  }

  function publicState(session) {
    const tabs = [...session.tabs.values()].map((tab) => ({ ...tabInfo(tab), active: tab.id === session.activeTabId }));
    return {
      session_id: session.sessionId,
      control: session.control,
      tabs,
      active_tab: session.activeTabId,
      dialog: session.dialog,
      notices: session.notices.slice(-10),
      notice_seq: session.noticeSeq,
      labels: labels(),
    };
  }

  function pushState(session) {
    try {
      if (session.win?.isDestroyed?.()) return;
      session.win?.webContents?.send?.('browserShell:state-changed', publicState(session));
    } catch (error) {
      logger.warn?.('[agent-browser] shell state push failed:', error?.message ?? error);
    }
  }

  function layout(session) {
    const win = session.win;
    if (!win || win.isDestroyed?.()) return;
    let width = 1280;
    let height = 860;
    try {
      const size = win.getContentSize?.();
      if (Array.isArray(size) && size.length === 2) [width, height] = size;
    } catch { /* keep defaults */ }
    for (const tab of session.tabs.values()) {
      try {
        tab.view.setBounds?.({
          x: 0,
          y: TOOLBAR_HEIGHT,
          width: Math.max(0, width),
          height: Math.max(0, height - TOOLBAR_HEIGHT),
        });
        tab.view.setVisible?.(tab.id === session.activeTabId);
      } catch (error) {
        logger.warn?.('[agent-browser] view layout failed:', error?.message ?? error);
      }
    }
  }

  async function send(tab, method, params = {}) {
    if (!tab.attached) await attachTab(tab);
    return tab.view.webContents.debugger.sendCommand(method, params);
  }

  async function attachTab(tab) {
    if (tab.attached) return;
    logger.debug?.('[agent-browser] attaching debugger', tab.id);
    try {
      tab.view.webContents.debugger.attach('1.3');
    } catch (error) {
      const message = String(error?.message ?? error);
      if (/already attached/i.test(message)) throw codedError('browser_debugger_unavailable', message);
      throw codedError('browser_debugger_unavailable', message);
    }
    tab.attached = true;
    for (const method of ['Page.enable', 'DOM.enable', 'Runtime.enable', 'Accessibility.enable']) {
      try { await send(tab, method); } catch (error) {
        logger.warn?.(`[agent-browser] ${method} failed:`, error?.message ?? error);
      }
    }
    try { await send(tab, 'Page.setInterceptFileChooserDialog', { enabled: true }); } catch { /* optional */ }
    logger.debug?.('[agent-browser] debugger ready', tab.id);
  }

  function ownerSessionForWebContents(wc) {
    for (const session of sessions.values()) {
      for (const tab of session.tabs.values()) {
        if (tab.view.webContents === wc) return session;
      }
    }
    return null;
  }

  function wireSession(session) {
    const shared = session.tabs.values().next().value?.view?.webContents?.session;
    if (!shared || wiredSessions.has(shared)) return;
    wiredSessions.add(shared);
    try {
      shared.setPermissionRequestHandler?.((wc, permission, callback) => {
        const owner = ownerSessionForWebContents(wc);
        if (owner) notice(owner, 'browser_permission_denied', { permission });
        callback(false);
      });
    } catch (error) { logger.warn?.('[agent-browser] permission handler failed:', error?.message ?? error); }
    try {
      shared.on?.('will-download', (event, item) => {
        event.preventDefault?.();
        const initiator = typeof item?.getWebContents === 'function' ? item.getWebContents() : null;
        const owner = ownerSessionForWebContents(initiator)
          ?? (sessions.size === 1 ? sessions.values().next().value : null);
        if (owner) {
          notice(owner, 'browser_download_started', {
            url: typeof item?.getURL === 'function' ? item.getURL() : undefined,
            filename: typeof item?.getFilename === 'function' ? item.getFilename() : undefined,
          });
        }
      });
    } catch (error) { logger.warn?.('[agent-browser] download handler failed:', error?.message ?? error); }
  }

  function wireTab(session, tab) {
    const wc = tab.view.webContents;
    try {
      wc.on?.('page-title-updated', (_event, title) => { tab.title = String(title ?? ''); pushState(session); });
      wc.on?.('did-navigate', (_event, url) => { tab.url = String(url ?? tab.url); pushState(session); });
      wc.on?.('did-navigate-in-page', (_event, url) => { tab.url = String(url ?? tab.url); pushState(session); });
      wc.on?.('did-start-loading', () => { tab.loading = true; pushState(session); });
      wc.on?.('did-stop-loading', () => { tab.loading = false; pushState(session); });
      wc.on?.('will-navigate', (event, url) => {
        if (!isHttpUrl(url)) {
          event.preventDefault?.();
          notice(session, 'browser_navigation_blocked', { url: String(url ?? '') });
        }
      });
      wc.setWindowOpenHandler?.(({ url }) => {
        notice(session, 'browser_popup_blocked', { url: String(url ?? '') });
        return { action: 'deny' };
      });
      wc.on?.('render-process-gone', (_event, details) => {
        tab.loading = false;
        notice(session, 'browser_renderer_gone', { reason: details?.reason });
      });
      wc.debugger?.on?.('message', (_event, method, params) => {
        if (method === 'Page.javascriptDialogOpening') {
          session.dialog = {
            type: params?.type,
            message: params?.message,
            url: params?.url,
            tabId: tab.id,
          };
          pushState(session);
        } else if (method === 'Page.fileChooserOpened') {
          notice(session, 'browser_file_chooser_opened', { mode: params?.mode });
        }
      });
      wc.debugger?.on?.('detach', () => { tab.attached = false; });
    } catch (error) {
      logger.warn?.('[agent-browser] tab wiring failed:', error?.message ?? error);
    }
  }

  async function ensureSession(sessionId) {
    const existing = sessions.get(sessionId);
    if (existing) return existing;

    const strings = labels();
    const win = new BrowserWindow({
      width: 1280,
      height: 860,
      minWidth: 720,
      minHeight: 400,
      show: false,
      backgroundColor: '#15121a',
      title: strings.title ?? 'Agent Browser',
      autoHideMenuBar: true,
      webPreferences: {
        preload: preloadPath,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
      },
    });
    const session = {
      sessionId,
      win,
      tabs: new Map(),
      activeTabId: null,
      control: 'agent',
      dialog: null,
      notices: [],
      noticeSeq: 0,
      createdAt: now().getTime(),
      shown: false,
    };
    sessions.set(sessionId, session);
    shellSenders.set(win.webContents.id, sessionId);

    const show = () => {
      if (session.shown || win.isDestroyed?.()) return;
      session.shown = true;
      try { win.showInactive?.(); } catch { /* show best effort */ }
    };
    win.once?.('ready-to-show', show);
    win.webContents.once?.('did-finish-load', show);
    win.on?.('resize', () => layout(session));
    win.on?.('closed', () => {
      sessions.delete(sessionId);
      shellSenders.delete(win.webContents.id);
      for (const tab of session.tabs.values()) {
        try { tab.view.webContents.debugger?.detach?.(); } catch { /* already gone */ }
      }
      session.tabs.clear();
    });
    win.webContents.on?.('will-navigate', (event, url) => {
      // The shell is local; it may only stay on its own URL.
      if (url !== shellUrl()) event.preventDefault?.();
    });
    win.webContents.setWindowOpenHandler?.(() => ({ action: 'deny' }));

    try {
      logger.debug?.('[agent-browser] loading shell', shellUrl());
      await win.loadURL(shellUrl());
      logger.debug?.('[agent-browser] shell loaded');
    } catch (error) {
      logger.warn?.('[agent-browser] shell load failed:', error?.message ?? error);
      show();
    }
    return session;
  }

  async function createTab(session, url) {
    if (session.tabs.size >= MAX_TABS) throw codedError('browser_tab_limit');
    const view = new WebContentsView({
      webPreferences: {
        partition: PARTITION,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
      },
    });
    const id = `tab-${session.tabs.size + 1}-${randomUUID().slice(0, 8)}`;
    const tab = { id, view, url: 'about:blank', title: '', loading: false, attached: false };
    session.tabs.set(id, tab);
    session.activeTabId = id;
    try {
      session.win.contentView.addChildView(view);
    } catch (error) {
      session.tabs.delete(id);
      throw codedError('browser_view_failed', error?.message ?? String(error));
    }
    layout(session);
    wireTab(session, tab);
    // Load before attaching: the P0 probe attached to a document that already had a page, and a
    // fresh blank WebContentsView has no renderer document to enable. Keep the proven order.
    try {
      await view.webContents.loadURL(url ?? 'about:blank');
      tab.url = url ?? 'about:blank';
    } catch (error) {
      logger.warn?.('[agent-browser] initial tab load failed:', error?.message ?? error);
    }
    await attachTab(tab);
    wireSession(session);
    pushState(session);
    return tab;
  }

  async function navigateTab(session, tab, rawUrl) {
    const url = parseHttpUrl(rawUrl);
    if (!url) throw codedError('browser_navigation_blocked');
    await send(tab, 'Page.navigate', { url: url.toString() });
    tab.url = url.toString();
    pushState(session);
    return tab;
  }

  function resolveTab(session, tabId) {
    const tab = session.tabs.get(tabId);
    if (!tab) throw codedError('browser_tab_not_found');
    return tab;
  }

  function stateFor(sessionId) {
    const session = sessions.get(sessionId);
    if (!session) {
      return {
        session_id: sessionId,
        control: 'agent',
        tabs: [],
        active_tab: null,
        dialog: null,
        notices: [],
        labels: labels(),
      };
    }
    return publicState(session);
  }

  async function openTab(sessionId, url) {
    const session = await ensureSession(sessionId);
    if (url) {
      const parsed = parseHttpUrl(url);
      if (!parsed) throw codedError('browser_navigation_blocked');
    }
    const tab = await createTab(session, url ?? null);
    return { tab: tabInfo({ ...tab, active: true }), state: publicState(session) };
  }

  function selectTab(sessionId, tabId) {
    const session = sessions.get(sessionId);
    if (!session) throw codedError('browser_not_started');
    const tab = resolveTab(session, tabId);
    session.activeTabId = tab.id;
    layout(session);
    pushState(session);
    return { tab: tabInfo({ ...tab, active: true }), state: publicState(session) };
  }

  function closeTab(sessionId, tabId) {
    const session = sessions.get(sessionId);
    if (!session) throw codedError('browser_not_started');
    const tab = resolveTab(session, tabId);
    session.tabs.delete(tabId);
    try { session.win.contentView.removeChildView(tab.view); } catch { /* best effort */ }
    try { tab.view.webContents.close(); } catch { /* best effort */ }
    try { tab.view.webContents.debugger?.detach?.(); } catch { /* best effort */ }
    if (session.activeTabId === tabId) {
      session.activeTabId = [...session.tabs.keys()][0] ?? null;
    }
    if (session.tabs.size === 0) {
      try { session.win.close(); } catch { /* closed */ }
    } else {
      layout(session);
      pushState(session);
    }
    return { closed: tabId, state: publicState(session) };
  }

  async function cdp(sessionId, tabId, method, params) {
    const session = sessions.get(sessionId);
    if (!session) throw codedError('browser_not_started');
    if (session.control === 'user') throw codedError('browser_user_in_control');
    if (session.dialog && method !== 'Page.handleJavaScriptDialog') throw codedError('browser_dialog_open');
    const tab = resolveTab(session, tabId);
    try {
      return await send(tab, method, params ?? {});
    } catch (error) {
      const message = String(error?.message ?? error);
      if (/already attached/i.test(message)) throw codedError('browser_debugger_unavailable', message);
      throw codedError('browser_cdp_error', message);
    }
  }

  // Long-edge cap for the model-facing context copy of a capture; mirrors the desktop tools'
  // MaxContextImageEdge so both capture families deliver the same budget shape.
  const MAX_CONTEXT_IMAGE_EDGE = 1568;

  async function screenshot(sessionId, tabId, fullPage, shotsRoot) {
    const result = await cdp(sessionId, tabId, 'Page.captureScreenshot', {
      format: 'png',
      captureBeyondViewport: fullPage === true,
    });
    const base64 = typeof result?.data === 'string' ? result.data : '';
    if (!base64) throw codedError('browser_screenshot_failed');
    const bytes = Buffer.from(base64, 'base64');
    const dir = path.join(shotsRoot, sessionId);
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
    const file = path.join(dir, `shot-${randomUUID()}.png`);
    fs.writeFileSync(file, bytes);

    // Dimensions and the downscaled context copy need a real decoder; without nativeImage the
    // result keeps the old shape (path/bytes only) and the .NET side falls back to the full file.
    let decoded = null;
    if (nativeImage) {
      try { decoded = nativeImage.createFromBuffer(bytes); } catch { decoded = null; }
    }
    if (!decoded) return { path: file, bytes: bytes.length };

    const size = decoded.getSize();
    const shot = { path: file, bytes: bytes.length, width: size.width, height: size.height };
    const longEdge = Math.max(size.width, size.height);
    if (longEdge <= MAX_CONTEXT_IMAGE_EDGE) {
      shot.context_path = file;
      shot.context_bytes = bytes.length;
      shot.context_width = size.width;
      shot.context_height = size.height;
      return shot;
    }
    const scale = MAX_CONTEXT_IMAGE_EDGE / longEdge;
    const width = Math.max(1, Math.round(size.width * scale));
    const height = Math.max(1, Math.round(size.height * scale));
    let resized;
    try { resized = decoded.resize({ width, height, quality: 'good' }); } catch { return shot; }
    const contextBytes = resized.toPNG();
    if (!contextBytes || !contextBytes.length) return shot;
    const contextFile = file.replace(/\.png$/, '-ctx.png');
    fs.writeFileSync(contextFile, contextBytes);
    shot.context_path = contextFile;
    shot.context_bytes = contextBytes.length;
    shot.context_width = width;
    shot.context_height = height;
    return shot;
  }

  function setControl(sessionId, mode) {
    const session = sessions.get(sessionId);
    if (!session) throw codedError('browser_not_started');
    if (mode !== 'agent' && mode !== 'user') throw codedError('browser_control_invalid');
    session.control = mode;
    pushState(session);
    return publicState(session);
  }

  async function handleDialog(sessionId, accept, promptText) {
    const session = sessions.get(sessionId);
    if (!session) throw codedError('browser_not_started');
    if (!session.dialog) throw codedError('browser_no_dialog');
    const tab = resolveTab(session, session.dialog.tabId ?? session.activeTabId);
    await send(tab, 'Page.handleJavaScriptDialog', {
      accept: accept === true,
      ...(promptText !== undefined ? { promptText: String(promptText) } : {}),
    });
    session.dialog = null;
    pushState(session);
    return publicState(session);
  }

  async function history(session, direction) {
    const tab = resolveTab(session, session.activeTabId);
    const historyState = await send(tab, 'Page.getNavigationHistory');
    const entries = Array.isArray(historyState?.entries) ? historyState.entries : [];
    const current = Number(historyState?.currentIndex ?? -1);
    const targetIndex = direction === 'back' ? current - 1 : current + 1;
    if (targetIndex < 0 || targetIndex >= entries.length) throw codedError('browser_history_unavailable', undefined, false);
    await send(tab, 'Page.navigateToHistoryEntry', { entryId: entries[targetIndex].id });
    return tab;
  }

  async function shellCommand(sender, payload) {
    const sessionId = shellSenders.get(sender?.id);
    if (!sessionId) return { ok: false, code: 'browser_shell_sender_invalid' };
    const session = sessions.get(sessionId);
    if (!session) return { ok: false, code: 'browser_session_unknown' };
    const type = payload?.type;
    try {
      if (type === 'navigate') {
        const tab = resolveTab(session, session.activeTabId);
        setControl(sessionId, 'user');
        await navigateTab(session, tab, payload.url);
        return { ok: true, data: publicState(session) };
      }
      if (type === 'back' || type === 'forward') {
        setControl(sessionId, 'user');
        await history(session, type);
        return { ok: true, data: publicState(session) };
      }
      if (type === 'reload') {
        const tab = resolveTab(session, session.activeTabId);
        setControl(sessionId, 'user');
        await send(tab, 'Page.reload');
        return { ok: true, data: publicState(session) };
      }
      if (type === 'takeover') {
        setControl(sessionId, 'user');
        return { ok: true, data: publicState(session) };
      }
      if (type === 'handback') {
        setControl(sessionId, 'agent');
        return { ok: true, data: publicState(session) };
      }
      if (type === 'select-tab') {
        selectTab(sessionId, String(payload.tab_id ?? ''));
        return { ok: true, data: publicState(session) };
      }
      if (type === 'close-tab') {
        closeTab(sessionId, String(payload.tab_id ?? ''));
        return { ok: true, data: stateFor(sessionId) };
      }
      if (type === 'open-external') {
        const tab = resolveTab(session, session.activeTabId);
        if (!isHttpUrl(tab.url)) throw codedError('browser_navigation_blocked');
        await shell?.openExternal?.(tab.url);
        return { ok: true, data: publicState(session) };
      }
      if (type === 'close') {
        try { session.win.close(); } catch { /* closed */ }
        return { ok: true, data: null };
      }
      return { ok: false, code: 'browser_shell_command_invalid' };
    } catch (error) {
      return { ok: false, code: error?.code ?? 'browser_shell_command_failed', message: error?.message };
    }
  }

  function shellState(sender) {
    const sessionId = shellSenders.get(sender?.id);
    if (!sessionId) return { ok: false, code: 'browser_shell_sender_invalid' };
    return { ok: true, data: stateFor(sessionId) };
  }

  function setLocale(next) {
    locale = typeof next === 'string' && next ? next : locale;
    for (const session of sessions.values()) pushState(session);
  }

  function closeAll() {
    for (const session of [...sessions.values()]) {
      try { session.win.destroy?.(); } catch { /* best effort */ }
    }
    sessions.clear();
    shellSenders.clear();
  }

  return {
    ensureSession,
    openTab,
    selectTab,
    closeTab,
    cdp,
    screenshot,
    handleDialog,
    setControl,
    getState: async (sessionId) => stateFor(sessionId),
    shellState,
    shellCommand,
    setLocale,
    closeAll,
    isShellSender: (sender) => shellSenders.has(sender?.id),
    // Exposed for the node tests.
    _layout: layout,
    _stateFor: stateFor,
    PARTITION,
    TOOLBAR_HEIGHT,
  };
}

module.exports = {
  MAX_TABS,
  PARTITION,
  TOOLBAR_HEIGHT,
  createAgentBrowserManager,
};
