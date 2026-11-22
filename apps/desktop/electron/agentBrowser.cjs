/**
 * Electron side of the agent browser: one satellite window per TaskSpace
 * `(sessionId / taskSpace)`, one `WebContentsView` per tab, and a debugger
 * session attached to each tab.
 *
 * Nothing here knows what a snapshot or a ref is. It exposes the CDP commands the .NET
 * tool process may send (through browserBridge.cjs), the window/tab/TaskSpace lifecycle,
 * the agent/user/finished control state machine, live mirror previews, console error
 * diagnostics, and the hard-stop facts Electron learns first (popups, permission prompts,
 * downloads, file choosers, dialogs).
 */

const path = require('node:path');
const fs = require('node:fs');
const crypto = require('node:crypto');

const PARTITION = 'persist:tinadec-agent-browser';
const TOOLBAR_HEIGHT = 48;
const MAX_TABS = 8;
const MAX_NOTICES = 20;
const MAX_CONSOLE_ERRORS = 50;
const MAX_FINISHED_SPACES = 500;

function codedError(code, message, permanent = true) {
  const error = new Error(message ?? code);
  error.code = code;
  error.permanent = permanent;
  return error;
}

/**
 * Mirror of ego-lite's DIALOG_SAFE_METHOD guard: while a modal JavaScript dialog is open,
 * renderer-bound CDP commands stall until transport timeout. Only browser-process methods
 * and dialog/download handlers may proceed while a dialog is open.
 */
function isDialogSafeMethod(method) {
  if (typeof method !== 'string') return false;
  return method.startsWith('Target.')
    || method.startsWith('Browser.')
    || method === 'Page.handleJavaScriptDialog'
    || method === 'Page.setDownloadBehavior';
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

function resolveSpaceDescriptor(rawSessionId, rawTaskSpace) {
  const sessionId = typeof rawSessionId === 'string' ? rawSessionId.trim() : '';
  const explicitSpace = typeof rawTaskSpace === 'string' ? rawTaskSpace.trim() : '';
  if (explicitSpace) {
    return {
      spaceKey: `${sessionId}::${explicitSpace}`,
      baseSessionId: sessionId,
      taskSpace: explicitSpace,
    };
  }
  const sep = sessionId.indexOf('::');
  if (sep > 0) {
    const baseSessionId = sessionId.slice(0, sep);
    const taskSpace = sessionId.slice(sep + 2) || 'default';
    return { spaceKey: sessionId, baseSessionId, taskSpace };
  }
  return {
    spaceKey: sessionId,
    baseSessionId: sessionId,
    taskSpace: 'default',
  };
}

function formatConsoleArg(arg) {
  if (!arg || typeof arg !== 'object') return '';
  if (arg.value !== undefined && arg.value !== null) return String(arg.value);
  if (typeof arg.description === 'string' && arg.description) return arg.description;
  if (typeof arg.unserializableValue === 'string') return arg.unserializableValue;
  return arg.type ? `[${arg.type}]` : '';
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
  onStateChange,
  logger = console,
  now = () => new Date(),
  randomUUID = crypto.randomUUID,
  platform = process.platform,
}) {
  if (!BrowserWindow || !WebContentsView) throw new TypeError('createAgentBrowserManager requires Electron constructors');
  if (typeof shellUrl !== 'function') throw new TypeError('createAgentBrowserManager requires shellUrl()');
  if (typeof preloadPath !== 'string') throw new TypeError('createAgentBrowserManager requires preloadPath');

  const sessions = new Map(); // spaceKey -> session
  const finishedSpaces = new Map(); // spaceKey -> { finishedAt, baseSessionId, taskSpace }
  const shellSenders = new Map(); // shell webContents.id -> spaceKey
  const wiredSessions = new WeakSet();
  const stateListeners = new Set();
  if (typeof onStateChange === 'function') stateListeners.add(onStateChange);
  let locale = app?.getLocale?.() ?? 'zh-CN';

  function labels() {
    return typeof stringsForLocale === 'function'
      ? stringsForLocale(locale)
      : { title: 'Agent Browser', back: 'Back', forward: 'Forward', reload: 'Reload' };
  }

  function rememberFinished(spaceKey, meta) {
    finishedSpaces.set(spaceKey, meta);
    if (finishedSpaces.size > MAX_FINISHED_SPACES) {
      const oldest = finishedSpaces.keys().next().value;
      if (oldest !== undefined) finishedSpaces.delete(oldest);
    }
  }

  function assertNotFinished(spaceKey) {
    const active = sessions.get(spaceKey);
    if (active?.control === 'finished' || finishedSpaces.has(spaceKey)) {
      throw codedError('browser_task_space_finished', 'Task space has finished and cannot be resumed.', true);
    }
  }

  function recordConsoleError(session, tab, entry) {
    const item = {
      seq: ++session.consoleErrorSeq,
      at: now().toISOString(),
      tab_id: tab?.id ?? null,
      level: entry.level ?? 'error',
      source: entry.source ?? 'console',
      message: String(entry.message ?? '').slice(0, 1000),
      ...(entry.url ? { url: String(entry.url).slice(0, 500) } : {}),
      ...(Number.isFinite(entry.line) ? { line: entry.line } : {}),
    };
    session.consoleErrors.push(item);
    if (session.consoleErrors.length > MAX_CONSOLE_ERRORS) {
      session.consoleErrors.splice(0, session.consoleErrors.length - MAX_CONSOLE_ERRORS);
    }
    pushState(session);
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
    const activeTab = session.activeTabId ? session.tabs.get(session.activeTabId) ?? null : null;
    return {
      session_id: session.sessionId,
      space_id: session.sessionId,
      base_session_id: session.baseSessionId,
      task_space: session.taskSpace,
      profile_id: session.profileId,
      control: session.control,
      handoff_reason: session.handoffReason,
      finished: session.control === 'finished',
      finished_at: session.finishedAt,
      tabs,
      active_tab: session.activeTabId,
      url: activeTab?.url ?? '',
      title: activeTab?.title ?? '',
      loading: Boolean(activeTab?.loading),
      dialog: session.dialog,
      notices: session.notices.slice(-10),
      notice_seq: session.noticeSeq,
      console_errors: session.consoleErrors.slice(-20),
      console_error_seq: session.consoleErrorSeq,
      labels: labels(),
    };
  }

  function spaceSummary(session) {
    const state = publicState(session);
    return {
      space_id: state.space_id,
      session_id: state.session_id,
      base_session_id: state.base_session_id,
      task_space: state.task_space,
      profile_id: state.profile_id,
      control: state.control,
      handoff_reason: state.handoff_reason,
      finished: state.finished,
      finished_at: state.finished_at,
      active_tab: state.active_tab,
      url: state.url,
      title: state.title,
      loading: state.loading,
      tab_count: state.tabs.length,
      tabs: state.tabs,
      dialog: state.dialog,
      console_error_count: session.consoleErrors.length,
      console_errors: state.console_errors.slice(-10),
      created_at: session.createdAt,
    };
  }

  function listSpaces() {
    return [...sessions.values()].map((session) => spaceSummary(session));
  }

  function emitStateChange(spaceState) {
    if (stateListeners.size === 0) return;
    const payload = { space: spaceState, spaces: listSpaces() };
    for (const listener of [...stateListeners]) {
      try {
        listener(payload);
      } catch (error) {
        logger.warn?.('[agent-browser] state listener failed:', error?.message ?? error);
      }
    }
  }

  function pushState(session) {
    const snapshot = publicState(session);
    try {
      if (!session.win?.isDestroyed?.()) {
        session.win?.webContents?.send?.('browserShell:state-changed', snapshot);
      }
    } catch (error) {
      logger.warn?.('[agent-browser] shell state push failed:', error?.message ?? error);
    }
    emitStateChange(snapshot);
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

  function abortInflightCdpForDialog(tab, dialog) {
    if (!tab?.inflightCdp || tab.inflightCdp.size === 0) return;
    const error = codedError(
      'browser_dialog_open',
      `JavaScript ${dialog?.type ?? 'modal'} dialog opened while CDP command was running`,
      true,
    );
    error.dialog = dialog;
    for (const entry of [...tab.inflightCdp]) {
      if (!isDialogSafeMethod(entry.method)) {
        tab.inflightCdp.delete(entry);
        entry.reject(error);
      }
    }
  }

  async function send(tab, method, params = {}, cdpSessionId = undefined) {
    if (!tab.attached) await attachTab(tab);
    const invoke = () => (cdpSessionId
      ? tab.view.webContents.debugger.sendCommand(method, params, cdpSessionId)
      : tab.view.webContents.debugger.sendCommand(method, params));
    if (isDialogSafeMethod(method)) {
      return invoke();
    }
    return new Promise((resolve, reject) => {
      let settled = false;
      const entry = {
        method,
        reject: (err) => {
          if (settled) return;
          settled = true;
          reject(err);
        },
      };
      tab.inflightCdp.add(entry);
      Promise.resolve(invoke()).then(
        (result) => {
          tab.inflightCdp.delete(entry);
          if (!settled) {
            settled = true;
            resolve(result);
          }
        },
        (error) => {
          tab.inflightCdp.delete(entry);
          if (!settled) {
            settled = true;
            reject(error);
          }
        },
      );
    });
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
      wc.on?.('console-message', (_event, level, message, line, sourceId) => {
        if (Number(level) >= 3) {
          recordConsoleError(session, tab, {
            source: 'console',
            level: 'error',
            message,
            line,
            url: sourceId || tab.url,
          });
        }
      });
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
            defaultPrompt: params?.defaultPrompt,
            tabId: tab.id,
          };
          abortInflightCdpForDialog(tab, session.dialog);
          pushState(session);
        } else if (method === 'Page.javascriptDialogClosed') {
          if (session.dialog) {
            session.dialog = null;
            pushState(session);
          }
        } else if (method === 'Page.fileChooserOpened') {
          notice(session, 'browser_file_chooser_opened', {
            mode: params?.mode,
            backendNodeId: params?.backendNodeId,
            frameId: params?.frameId,
          });
        } else if (method === 'Runtime.exceptionThrown') {
          const details = params?.exceptionDetails ?? {};
          const message = details?.exception?.description
            || details?.text
            || 'Uncaught runtime exception';
          recordConsoleError(session, tab, {
            source: 'exception',
            level: 'error',
            message,
            line: details?.lineNumber,
            url: details?.url || tab.url,
          });
        } else if (method === 'Runtime.consoleAPICalled') {
          if (params?.type === 'error' || params?.type === 'assert') {
            const parts = Array.isArray(params?.args)
              ? params.args.map(formatConsoleArg).filter(Boolean)
              : [];
            recordConsoleError(session, tab, {
              source: 'console',
              level: params.type,
              message: parts.join(' ') || `console.${params.type}`,
              url: tab.url,
            });
          }
        }
      });
      wc.debugger?.on?.('detach', () => { tab.attached = false; });
    } catch (error) {
      logger.warn?.('[agent-browser] tab wiring failed:', error?.message ?? error);
    }
  }

  async function ensureSession(sessionId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    assertNotFinished(desc.spaceKey);
    const existing = sessions.get(desc.spaceKey);
    if (existing) return existing;

    const strings = labels();
    const titlePrefix = desc.taskSpace && desc.taskSpace !== 'default'
      ? `${strings.title ?? 'Agent Browser'} · ${desc.taskSpace}`
      : (strings.title ?? 'Agent Browser');

    const win = new BrowserWindow({
      width: 1280,
      height: 860,
      minWidth: 720,
      minHeight: 400,
      show: false,
      backgroundColor: '#15121a',
      title: titlePrefix,
      autoHideMenuBar: true,
      webPreferences: {
        preload: preloadPath,
        contextIsolation: true,
        nodeIntegration: false,
        sandbox: true,
      },
    });
    const session = {
      sessionId: desc.spaceKey,
      baseSessionId: desc.baseSessionId,
      taskSpace: desc.taskSpace,
      profileId: typeof options.profileId === 'string' && options.profileId.trim() ? options.profileId.trim() : 'default',
      win,
      tabs: new Map(),
      activeTabId: null,
      control: 'agent',
      handoffReason: null,
      finishedAt: null,
      dialog: null,
      notices: [],
      noticeSeq: 0,
      consoleErrors: [],
      consoleErrorSeq: 0,
      createdAt: now().getTime(),
      shown: false,
    };
    sessions.set(desc.spaceKey, session);
    shellSenders.set(win.webContents.id, desc.spaceKey);

    const show = () => {
      if (session.shown || win.isDestroyed?.()) return;
      session.shown = true;
      try { win.showInactive?.(); } catch { /* show best effort */ }
    };
    win.once?.('ready-to-show', show);
    win.webContents.once?.('did-finish-load', show);
    win.on?.('resize', () => layout(session));
    win.on?.('closed', () => {
      sessions.delete(desc.spaceKey);
      shellSenders.delete(win.webContents.id);
      for (const tab of session.tabs.values()) {
        try { tab.view.webContents.debugger?.detach?.(); } catch { /* already gone */ }
      }
      session.tabs.clear();
      emitStateChange(stateFor(desc.spaceKey));
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
    const tab = {
      id,
      view,
      url: 'about:blank',
      title: '',
      loading: false,
      attached: false,
      inflightCdp: new Set(),
    };
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

  function stateFor(sessionId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    const session = sessions.get(desc.spaceKey);
    if (!session) {
      const finished = finishedSpaces.get(desc.spaceKey);
      return {
        session_id: desc.spaceKey,
        space_id: desc.spaceKey,
        base_session_id: finished?.baseSessionId ?? desc.baseSessionId,
        task_space: finished?.taskSpace ?? desc.taskSpace,
        profile_id: 'default',
        control: finished ? 'finished' : 'agent',
        handoff_reason: null,
        finished: Boolean(finished),
        finished_at: finished?.finishedAt ?? null,
        tabs: [],
        active_tab: null,
        url: '',
        title: '',
        loading: false,
        dialog: null,
        notices: [],
        notice_seq: 0,
        console_errors: [],
        console_error_seq: 0,
        labels: labels(),
      };
    }
    return publicState(session);
  }

  async function openTab(sessionId, url, options = {}) {
    const session = await ensureSession(sessionId, options);
    if (session.control === 'user') throw codedError('browser_user_in_control');
    if (url) {
      const parsed = parseHttpUrl(url);
      if (!parsed) throw codedError('browser_navigation_blocked');
    }
    const tab = await createTab(session, url ?? null);
    return { tab: tabInfo({ ...tab, active: true }), state: publicState(session) };
  }

  function selectTab(sessionId, tabId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    const tab = resolveTab(session, tabId);
    session.activeTabId = tab.id;
    layout(session);
    pushState(session);
    return { tab: tabInfo({ ...tab, active: true }), state: publicState(session) };
  }

  function destroyTab(session, tabId) {
    const tab = session.tabs.get(tabId);
    if (!tab) return false;
    session.tabs.delete(tabId);
    try { session.win.contentView.removeChildView(tab.view); } catch { /* best effort */ }
    try { tab.view.webContents.close(); } catch { /* best effort */ }
    try { tab.view.webContents.debugger?.detach?.(); } catch { /* best effort */ }
    if (session.dialog?.tabId === tabId) session.dialog = null;
    if (session.activeTabId === tabId) {
      session.activeTabId = [...session.tabs.keys()][0] ?? null;
    }
    return true;
  }

  function closeTab(sessionId, tabId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    resolveTab(session, tabId);
    destroyTab(session, tabId);
    if (session.tabs.size === 0) {
      try { session.win.close(); } catch { /* closed */ }
    } else {
      layout(session);
      pushState(session);
    }
    return { closed: tabId, state: stateFor(desc.spaceKey) };
  }

  async function cdp(sessionId, tabId, method, params, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    assertNotFinished(desc.spaceKey);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    if (session.control === 'user') throw codedError('browser_user_in_control');
    if (session.dialog && !isDialogSafeMethod(method)) {
      const error = codedError(
        'browser_dialog_open',
        `CDP method ${method} blocked while JavaScript ${session.dialog.type ?? 'modal'} dialog is open`,
        true,
      );
      error.dialog = session.dialog;
      throw error;
    }
    const tab = resolveTab(session, tabId);
    try {
      const result = await send(tab, method, params ?? {}, options.cdpSessionId);
      if (method === 'Page.handleJavaScriptDialog' && session.dialog) {
        session.dialog = null;
        pushState(session);
      }
      return result;
    } catch (error) {
      if (error?.code === 'browser_dialog_open') throw error;
      const message = String(error?.message ?? error);
      if (/already attached/i.test(message)) throw codedError('browser_debugger_unavailable', message);
      throw codedError('browser_cdp_error', message);
    }
  }

  async function screenshot(sessionId, tabId, fullPage, shotsRoot, options = {}) {
    const result = await cdp(sessionId, tabId, 'Page.captureScreenshot', {
      format: 'png',
      captureBeyondViewport: fullPage === true,
    }, options);
    const base64 = typeof result?.data === 'string' ? result.data : '';
    if (!base64) throw codedError('browser_screenshot_failed');
    const bytes = Buffer.from(base64, 'base64');
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    const safeDirName = desc.spaceKey.replace(/[^A-Za-z0-9._-]/g, '_');
    const dir = path.join(shotsRoot, safeDirName);
    fs.mkdirSync(dir, { recursive: true, mode: 0o700 });
    const file = path.join(dir, `shot-${randomUUID()}.png`);
    fs.writeFileSync(file, bytes);
    return { path: file, bytes: bytes.length };
  }

  async function capturePreview(sessionId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    const tabId = options.tabId ?? session.activeTabId;
    const tab = tabId ? session.tabs.get(tabId) ?? null : null;
    let dataUrl = null;
    if (tab) {
      try {
        if (typeof tab.view.webContents.capturePage === 'function') {
          const nativeImage = await tab.view.webContents.capturePage();
          const url = typeof nativeImage?.toDataURL === 'function' ? nativeImage.toDataURL() : null;
          if (typeof url === 'string' && url.startsWith('data:image/')) dataUrl = url;
        }
      } catch (error) {
        logger.warn?.('[agent-browser] capturePage preview failed:', error?.message ?? error);
      }
      if (!dataUrl && !session.dialog) {
        try {
          const shot = await send(tab, 'Page.captureScreenshot', {
            format: 'png',
            captureBeyondViewport: false,
          });
          if (typeof shot?.data === 'string' && shot.data) {
            dataUrl = `data:image/png;base64,${shot.data}`;
          }
        } catch (error) {
          logger.warn?.('[agent-browser] CDP preview fallback failed:', error?.message ?? error);
        }
      }
    }
    return {
      space_id: session.sessionId,
      session_id: session.sessionId,
      base_session_id: session.baseSessionId,
      task_space: session.taskSpace,
      control: session.control,
      handoff_reason: session.handoffReason,
      finished: session.control === 'finished',
      tab_id: tab?.id ?? null,
      url: tab?.url ?? '',
      title: tab?.title ?? '',
      loading: Boolean(tab?.loading),
      dialog: session.dialog,
      data_url: dataUrl,
      captured_at: now().toISOString(),
      console_errors: session.consoleErrors.slice(-10),
    };
  }

  function openWindow(sessionId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    if (!session.win.isDestroyed?.()) {
      try {
        if (session.win.isMinimized?.()) session.win.restore?.();
        if (typeof session.win.show === 'function') session.win.show();
        else session.win.showInactive?.();
        session.win.focus?.();
        session.shown = true;
      } catch (error) {
        logger.warn?.('[agent-browser] openWindow failed:', error?.message ?? error);
      }
    }
    return publicState(session);
  }

  function setControl(sessionId, mode, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    assertNotFinished(desc.spaceKey);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    if (mode !== 'agent' && mode !== 'user') throw codedError('browser_control_invalid');
    session.control = mode;
    if (mode === 'agent') session.handoffReason = null;
    pushState(session);
    return publicState(session);
  }

  function handOff(sessionId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    assertNotFinished(desc.spaceKey);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    session.control = 'user';
    session.handoffReason = typeof options.reason === 'string' && options.reason.trim()
      ? options.reason.trim().slice(0, 200)
      : 'manual_takeover';
    notice(session, 'browser_handoff_requested', { reason: session.handoffReason });
    return publicState(session);
  }

  function takeOver(sessionId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    assertNotFinished(desc.spaceKey);
    const session = sessions.get(desc.spaceKey);
    if (!session) throw codedError('browser_not_started');
    session.control = 'agent';
    session.handoffReason = null;
    pushState(session);
    return publicState(session);
  }

  function finish(sessionId, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    const session = sessions.get(desc.spaceKey);
    if (!session) {
      if (finishedSpaces.has(desc.spaceKey)) {
        return {
          finished: true,
          control: 'finished',
          kept_tabs: [],
          closed_tabs: [],
          state: stateFor(desc.spaceKey),
        };
      }
      throw codedError('browser_not_started');
    }

    const keepAll = options.keep === true;
    const keepSet = keepAll
      ? new Set(session.tabs.keys())
      : new Set(Array.isArray(options.keep) ? options.keep.map((item) => String(item)) : []);

    session.control = 'finished';
    session.handoffReason = null;
    session.finishedAt = now().toISOString();
    rememberFinished(desc.spaceKey, {
      finishedAt: session.finishedAt,
      baseSessionId: session.baseSessionId,
      taskSpace: session.taskSpace,
    });

    const closedTabs = [];
    const keptTabs = [];
    for (const tabId of [...session.tabs.keys()]) {
      if (keepSet.has(tabId)) {
        keptTabs.push(tabId);
      } else {
        destroyTab(session, tabId);
        closedTabs.push(tabId);
      }
    }

    if (session.tabs.size === 0) {
      try { session.win.close(); } catch { /* closed */ }
    } else {
      layout(session);
      notice(session, 'browser_task_finished', { kept_tabs: keptTabs.length, closed_tabs: closedTabs.length });
    }

    return {
      finished: true,
      control: 'finished',
      kept_tabs: keptTabs,
      closed_tabs: closedTabs,
      state: stateFor(desc.spaceKey),
    };
  }

  async function handleDialog(sessionId, accept, promptText, options = {}) {
    const desc = resolveSpaceDescriptor(sessionId, options.taskSpace);
    assertNotFinished(desc.spaceKey);
    const session = sessions.get(desc.spaceKey);
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
        if (session.control !== 'finished') setControl(sessionId, 'user');
        await navigateTab(session, tab, payload.url);
        return { ok: true, data: publicState(session) };
      }
      if (type === 'back' || type === 'forward') {
        if (session.control !== 'finished') setControl(sessionId, 'user');
        await history(session, type);
        return { ok: true, data: publicState(session) };
      }
      if (type === 'reload') {
        const tab = resolveTab(session, session.activeTabId);
        if (session.control !== 'finished') setControl(sessionId, 'user');
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

  function subscribeState(listener) {
    if (typeof listener !== 'function') return () => {};
    stateListeners.add(listener);
    return () => { stateListeners.delete(listener); };
  }

  function closeAll() {
    for (const session of [...sessions.values()]) {
      try { session.win.destroy?.(); } catch { /* best effort */ }
    }
    sessions.clear();
    finishedSpaces.clear();
    shellSenders.clear();
  }

  return {
    ensureSession,
    openTab,
    selectTab,
    closeTab,
    cdp,
    screenshot,
    capturePreview,
    openWindow,
    listSpaces,
    handleDialog,
    setControl,
    handOff,
    takeOver,
    finish,
    getState: async (sessionId, options) => stateFor(sessionId, options),
    shellState,
    shellCommand,
    setLocale,
    onStateChange: subscribeState,
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
  MAX_CONSOLE_ERRORS,
  MAX_TABS,
  PARTITION,
  TOOLBAR_HEIGHT,
  createAgentBrowserManager,
  isDialogSafeMethod,
  resolveSpaceDescriptor,
};
