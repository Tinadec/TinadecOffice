type ShellTab = {
  id: string;
  url: string;
  title: string;
  loading: boolean;
  active: boolean;
};

type ShellNotice = {
  code: string;
  at?: string;
  permission?: string;
  filename?: string;
  mode?: string;
};

type ShellState = {
  session_id: string;
  control: 'agent' | 'user';
  tabs: ShellTab[];
  active_tab: string | null;
  dialog: { type?: string; message?: string } | null;
  notices: ShellNotice[];
  labels: Record<string, string>;
};

type ShellResult = { ok: boolean; data?: ShellState | null; code?: string; message?: string };

type BrowserBridge = {
  getState: () => Promise<ShellResult>;
  command: (payload: Record<string, unknown>) => Promise<ShellResult>;
  onState: (callback: (state: ShellState) => void) => () => void;
};

declare global {
  interface Window {
    tinadecBrowser?: BrowserBridge;
  }
}

export {};

const api = window.tinadecBrowser;
let current: ShellState | null = null;

function element<T extends HTMLElement>(id: string): T {
  const node = document.getElementById(id);
  if (!node) throw new Error(`agent browser shell is missing #${id}`);
  return node as T;
}

const back = element<HTMLButtonElement>('back');
const forward = element<HTMLButtonElement>('forward');
const reload = element<HTMLButtonElement>('reload');
const urlInput = element<HTMLInputElement>('url');
const tabsSelect = element<HTMLSelectElement>('tabs');
const stateLabel = element<HTMLSpanElement>('state');
const takeOver = element<HTMLButtonElement>('takeover');
const handBack = element<HTMLButtonElement>('handback');
const external = element<HTMLButtonElement>('external');
const close = element<HTMLButtonElement>('close');
const noticeEl = element<HTMLDivElement>('notice');

const NOTICE_KEYS: Record<string, string> = {
  browser_popup_blocked: 'noticePopup',
  browser_permission_denied: 'noticePermission',
  browser_download_started: 'noticeDownload',
  browser_file_chooser_opened: 'noticeFileChooser',
  browser_renderer_gone: 'noticeRenderer',
  browser_navigation_blocked: 'noticeNavigation',
};

function label(state: ShellState, key: string): string {
  return state.labels?.[key] || key;
}

function format(template: string, values: Record<string, string | undefined>): string {
  return template.replace(/\{(\w+)\}/g, (_match, key: string) => values[key] ?? '');
}

function activeTab(state: ShellState): ShellTab | null {
  return state.tabs.find((tab) => tab.active) ?? state.tabs[0] ?? null;
}

function showNotice(text: string): void {
  noticeEl.textContent = text;
  noticeEl.classList.add('visible');
}

function renderShell(state: ShellState): void {
  current = state;
  const tab = activeTab(state);

  back.title = label(state, 'back');
  back.setAttribute('aria-label', label(state, 'back'));
  forward.title = label(state, 'forward');
  forward.setAttribute('aria-label', label(state, 'forward'));
  reload.title = label(state, 'reload');
  reload.setAttribute('aria-label', label(state, 'reload'));
  takeOver.textContent = label(state, 'takeOver');
  takeOver.title = label(state, 'takeOver');
  handBack.textContent = label(state, 'handBack');
  handBack.title = label(state, 'handBack');
  external.textContent = label(state, 'openExternal');
  external.title = label(state, 'openExternal');
  close.textContent = label(state, 'close');
  close.title = label(state, 'close');
  urlInput.placeholder = label(state, 'urlPlaceholder');

  if (document.activeElement !== urlInput) urlInput.value = tab?.url ?? '';

  tabsSelect.replaceChildren();
  for (const item of state.tabs) {
    const option = document.createElement('option');
    option.value = item.id;
    option.textContent = item.title || item.url || item.id;
    option.selected = item.active;
    tabsSelect.append(option);
  }
  tabsSelect.disabled = state.tabs.length === 0;
  tabsSelect.title = label(state, 'tabs');

  const badge = state.dialog
    ? { text: label(state, 'dialogOpen'), className: 'dialog' }
    : state.control === 'user'
      ? { text: label(state, 'userControl'), className: 'user' }
      : { text: label(state, 'agentControl'), className: 'agent' };
  stateLabel.textContent = tab?.loading ? `${badge.text} · ${label(state, 'loading')}` : badge.text;
  stateLabel.className = badge.className;

  takeOver.hidden = state.control === 'user';
  handBack.hidden = state.control !== 'user';

  const last = state.notices?.[state.notices.length - 1];
  if (last) {
    const key = NOTICE_KEYS[last.code];
    showNotice(key
      ? format(label(state, key), { permission: last.permission, filename: last.filename })
      : last.code);
  } else {
    noticeEl.classList.remove('visible');
    noticeEl.textContent = '';
  }

  document.title = tab?.title ? `${tab.title} — ${label(state, 'title')}` : label(state, 'title');
}

async function send(payload: Record<string, unknown>): Promise<void> {
  if (!api) return;
  const result = await api.command(payload);
  if (!result?.ok) {
    showNotice(result?.message || result?.code || 'error');
    return;
  }
  if (result.data) renderShell(result.data);
}

function disabled(): void {
  for (const button of [back, forward, reload, takeOver, handBack, external, close]) button.disabled = true;
  urlInput.disabled = true;
  tabsSelect.disabled = true;
  stateLabel.textContent = 'agent browser bridge unavailable';
}

if (!api) {
  disabled();
} else {
  back.addEventListener('click', () => { void send({ type: 'back' }); });
  forward.addEventListener('click', () => { void send({ type: 'forward' }); });
  reload.addEventListener('click', () => { void send({ type: 'reload' }); });
  takeOver.addEventListener('click', () => { void send({ type: 'takeover' }); });
  handBack.addEventListener('click', () => { void send({ type: 'handback' }); });
  external.addEventListener('click', () => { void send({ type: 'open-external' }); });
  close.addEventListener('click', () => { void send({ type: 'close' }); });
  tabsSelect.addEventListener('change', () => { void send({ type: 'select-tab', tab_id: tabsSelect.value }); });
  urlInput.addEventListener('keydown', (event) => {
    if (event.key === 'Enter') {
      event.preventDefault();
      void send({ type: 'navigate', url: urlInput.value });
    } else if (event.key === 'Escape') {
      urlInput.value = current ? (activeTab(current)?.url ?? '') : '';
      urlInput.blur();
    }
  });
  api.onState(renderShell);
  void api.getState().then((result) => {
    if (result?.ok && result.data) renderShell(result.data);
    else showNotice(result?.code || 'agent browser state unavailable');
  });
}
