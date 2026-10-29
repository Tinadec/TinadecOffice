/// <reference types="vite/client" />

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<object, object, unknown>
  export default component
}

declare module 'monaco-editor/esm/vs/basic-languages/*' {
  const contribution: unknown
  export default contribution
}

interface PanelWindowInfo {
  windowId: number;
  tabId: string;
  type: string;
  title: string;
}

interface WindowBounds {
  x: number;
  y: number;
  width: number;
  height: number;
}

interface DetachResult {
  windowId: number;
  tabId: string;
}

/** One file the main process copied into a workspace source folder. */
interface ImportedWorkspaceFile {
  name: string;
  path: string;
  bytes: number;
}

/** One file the main process refused to copy. `reason` is a stable code, worded by the renderer. */
interface SkippedWorkspaceFile {
  path: string;
  reason: 'too_many' | 'too_large' | 'relative_path' | 'missing' | 'link' | 'not_a_file' | 'duplicate' | 'exists' | 'copy_failed';
}

interface ReattachData {
  tabId: string;
  type: string;
  title: string;
  state: Record<string, unknown>;
}

interface PanelClosedData {
  windowId: number;
  tabId: string;
  type: string;
  title: string;
  state: Record<string, unknown>;
}

interface PanelDetachedData {
  windowId: number;
  tabId: string;
  type: string;
  title: string;
}

interface ThemeChangedData {
  theme: string;
  accentColor: string;
}

interface LoginIdentity {
  sub: string;
  nickname: string;
  avatar_url: string | null;
  email: string | null;
}

type AuthState = { loggedIn: false } | ({ loggedIn: true } & LoginIdentity);

type StatusBroadcast =
  | { op: 'raise'; key: string; level: 'info' | 'success' | 'warning' | 'error'; title?: string; message: string; details?: string; source?: string; createdAt: number }
  | { op: 'clear'; key: string };

interface ShellProfile {
  id: string;
  label: string;
  shell: string;
  args: string[];
}

interface TerminalCreateOptions {
  id?: string;
  shell?: string;
  args?: string[];
  cwd?: string;
  cols?: number;
  rows?: number;
  title?: string;
}

interface TerminalCreateResult {
  /** Null when no process could be started; `error` then explains why. */
  id: string | null;
  shell?: string;
  title?: string;
  /** Which backend actually started. `'pty'` is a real pseudo-terminal. */
  backend: 'pty' | null;
  error?: string;
}

interface TerminalSnapshot {
  /** Everything the terminal has printed so far, bounded by the main process. */
  replay: string;
  exited: boolean;
  exitCode: number | null;
}

interface TerminalInfo {
  id: string;
  shell: string;
  title: string;
  exited: boolean;
}

interface TerminalApi {
  create: (options?: TerminalCreateOptions) => Promise<TerminalCreateResult>;
  write: (id: string, data: string) => void;
  resize: (id: string, cols: number, rows: number) => void;
  destroy: (id: string) => void;
  snapshot: (id: string) => Promise<TerminalSnapshot | null>;
  getShells: () => Promise<ShellProfile[]>;
  list: () => Promise<TerminalInfo[]>;
  onData: (id: string, callback: (data: string) => void) => () => void;
  onExit: (id: string, callback: (exitCode: number, signal?: number) => void) => () => void;
}

declare global {
  interface PetdexCatalogPet {
    slug: string;
    displayName: string;
    kind: string;
    submittedBy: string;
    previewUrl: string;
  }

  interface DownloadedPet {
    slug: string;
    displayName: string;
    kind: string;
    submittedBy: string;
    enabled: boolean;
    installedAt: number;
    scale: number;
    window?: WindowBounds;
    imageDataUrl?: string;
    /** Bundled fallback pet: always installed, never removable. */
    builtin?: boolean;
    source?: 'builtin' | 'petdex' | string;
  }

  /** One entry of the pool agent avatars are drawn from; `url` is served by `tinadec-pet://`. */
  interface AvatarPetRecord {
    slug: string;
    displayName: string;
    kind: string;
    builtin: boolean;
    url: string;
  }

  type AgentAvatarPreference = { mode: 'random' } | { mode: 'fixed'; petSlug: string };

  interface AgentAvatarInstanceRecord {
    petSlug: string;
    agentId: string;
    assignedAt: number;
    source: 'random' | 'fixed';
  }

  interface AgentAvatarState {
    version: number;
    preferences: Record<string, AgentAvatarPreference>;
    instances: Record<string, AgentAvatarInstanceRecord>;
  }

  interface PetWindowInfo {
    instanceId: string;
    petId: string;
    windowId: number;
    bounds: WindowBounds;
  }

  interface PetWindowApi {
    create: (petId: string) => Promise<{ instanceId: string; windowId: number }>;
    close: (instanceId: string) => Promise<boolean>;
    list: () => Promise<PetWindowInfo[]>;
    getWindowPet: (instanceId: string) => Promise<DownloadedPet | null>;
    getCurrent: () => Promise<DownloadedPet | null>;
    setCurrentBounds: (bounds: Partial<WindowBounds> & { scale?: number }) => Promise<boolean>;
    setCurrentClickThrough: (enabled: boolean) => Promise<boolean>;
    closeCurrent: () => Promise<boolean>;
    onChanged: (callback: (pet: Pick<DownloadedPet, 'slug' | 'enabled'>) => void) => () => void;
    fetchCatalog: (force?: boolean) => Promise<PetdexCatalogPet[]>;
    download: (slug: string) => Promise<DownloadedPet>;
    listDownloaded: () => Promise<DownloadedPet[]>;
    setEnabled: (slug: string, enabled: boolean) => Promise<DownloadedPet>;
    openFolder: (slug: string) => Promise<boolean>;
    remove: (slug: string) => Promise<boolean>;
    listAvatars: () => Promise<AvatarPetRecord[]>;
    loadAvatarState: () => Promise<AgentAvatarState>;
    setAgentAvatar: (agentId: string, preference: AgentAvatarPreference) => Promise<AgentAvatarPreference>;
    saveInstanceAvatar: (input: { instanceKey: string; petSlug: string; agentId?: string; source?: 'random' | 'fixed' }) => Promise<AgentAvatarInstanceRecord>;
    releaseInstanceAvatars: (instanceKeys: string[]) => Promise<number>;
    onAvatarsChanged: (callback: () => void) => () => void;
  }

  /** Collapsed or expanded, the companion window's own view of itself; the main process owns it. */
  interface CompanionState {
    open: boolean;
    expanded: boolean;
    side: 'left' | 'right';
  }

  /** Result of one whitelisted companion proxy call; never carries the host credential. */
  interface CompanionProxyResult {
    status: number;
    body: unknown;
  }

  interface CompanionApi {
    /** Companion domain read/speak/invite/pause paths, proxied by the main process. */
    api: (method: 'GET' | 'POST' | 'PATCH', path: string, body?: unknown) => Promise<CompanionProxyResult>;
    getState: () => Promise<CompanionState>;
    expand: () => Promise<CompanionState>;
    collapse: () => Promise<CompanionState>;
    /** Move the window so its top edge sits at `top` in screen coordinates (clamped to the work area). */
    moveTo: (top: number) => Promise<CompanionState>;
    setSide: (side: 'left' | 'right') => Promise<CompanionState>;
    onStateChanged: (callback: (state: CompanionState) => void) => () => void;
  }

  interface DiscoveredService {
    url: string;
    service: 'gateway' | 'core';
    core_status: 'ready' | 'unreachable';
    mode?: string;
    version?: string;
    current: boolean;
  }

  interface Window {
    tinadec: {
      gatewayUrl: () => string;
      getHostStatus: () => Promise<import('./lib/hostConnection').HostConnectionStatus>;
      retryHostConnection: () => Promise<import('./lib/hostConnection').HostConnectionStatus>;
      onHostStatusChanged: (callback: (status: import('./lib/hostConnection').HostConnectionStatus) => void) => () => void;
      getAppConfig: () => Promise<{ gateway_url: string; source: 'default' | 'user' | 'environment'; managed: boolean; debug_studio_enabled?: boolean; close_to_tray?: boolean; companion_mode_enabled?: boolean; path?: string; storage?: { root: string; source: 'default' | 'desktop' | 'environment'; bootstrap_config: string; managed: boolean; local_services: boolean } }>;
      saveDebugStudioEnabled: (enabled: boolean) => Promise<{ debug_studio_enabled: boolean }>;
      onDebugStudioEnabledChanged: (callback: () => void) => () => void;
      /** Keep the app running in the tray after the main window is closed. */
      saveCloseToTray: (enabled: boolean) => Promise<{ close_to_tray: boolean }>;
      onCloseToTrayChanged: (callback: (value: { close_to_tray: boolean }) => void) => () => void;
      /** Report the interface language so the main process can draw its tray menu in it. */
      setInterfaceLocale: (locale: string) => void;
      /** Show or hide the docked companion capsule (desktop.toml `[companion] enabled`). */
      saveCompanionMode: (enabled: boolean) => Promise<{ companion_mode_enabled: boolean }>;
      /** The companion window's own geometry gestures; other windows are rejected by the main process. */
      companion: CompanionApi;
      saveGatewayUrl: (gatewayUrl: string) => Promise<{ gateway_url: string; source: 'user'; managed: false }>;
      resetGatewayUrl: () => Promise<{ gateway_url: string; source: 'default' | 'environment'; managed: boolean }>;
      discoverServices: () => Promise<DiscoveredService[]>;
      restartApp: () => Promise<void>;
      selectWorkspaceFolders: (title?: string) => Promise<string[]>;
      /** Copies dropped files into a workspace source folder; never overwrites an existing file. */
      importWorkspaceFiles: (input: { destDir: string; files: string[] }) => Promise<{ imported: ImportedWorkspaceFile[]; skipped: SkippedWorkspaceFile[] }>;
      /** The absolute path behind a dropped `File`; empty when the host cannot resolve one. */
      pathForFile: (file: File) => string;
      setStorageWritePolicy: (storageId: string, allow: boolean) => Promise<{ storage_id: string; allow_storage_write: boolean }>;
      storageAction: (storageId: string, action: 'configure' | 'cleanup' | 'content-collect' | 'storage-delete' | 'unregister', input?: Record<string, unknown>) => Promise<unknown>;
      saveUserStorageRoot: (root: string) => Promise<{ user_root: string; path: string; restart_required: boolean }>;
      minimizeWindow: () => void;
      maximizeWindow: () => void;
      closeWindow: () => void;
      openDebugStudio: () => Promise<boolean>;
      /** Open the Logto login flow in the system browser; resolves with the synced identity or null. */
      openLogin: () => Promise<LoginIdentity | null>;
      /** Revoke the current login session. */
      logout: () => Promise<{ loggedIn: false }>;
      /** Current login session, if any. */
      getAuthState: () => Promise<AuthState>;
      /** Write nickname/avatar to the Logto account; rejects when the session is gone. */
      updateProfile: (patch: { nickname?: string; avatarUrl?: string }) => Promise<LoginIdentity>;
      /** Local-only transparent pet windows. */
      pets: PetWindowApi;
      /** Terminal management API */
      terminal: TerminalApi;
      /** Detach a tab into a new floating BrowserWindow */
      detachPanel: (tabId: string, type: string, title: string, state: Record<string, unknown>) => Promise<DetachResult | null>;
      /** Reattach a panel window back to the main window */
      reattachPanel: (tabId: string, type: string, title: string, state: Record<string, unknown>) => Promise<boolean>;
      /** Close a specific panel window by windowId */
      closePanelWindow: (windowId: number) => void;
      /** Focus a specific panel window by windowId */
      focusPanelWindow: (windowId: number) => void;
      /** Get list of all open panel windows */
      getPanelWindows: () => Promise<PanelWindowInfo[]>;
      /** Get current cursor screen position */
      getCursorScreen: () => Promise<{ x: number; y: number }>;
      /** Get the main window bounds (for drag-out detection) */
      getMainBounds: () => Promise<WindowBounds | null>;
      /** Broadcast theme change to all panel windows */
      broadcastTheme: (theme: string, accentColor: string) => void;
      /** Read a local background image as a data URL (Monet color extraction; file:// is canvas-tainted in dev) */
      readImageAsDataUrl?: (source: string) => Promise<string | null>;
      /** Read the system clipboard as text (selection context menu; needs no browser permission) */
      clipboardReadText?: () => Promise<string>;
      /** Write text to the system clipboard (selection context menu) */
      clipboardWriteText?: (text: string) => Promise<boolean>;
      /** Broadcast a status notification to all windows (main, panels, debug studio) */
      broadcastStatusNotification: (payload: StatusBroadcast) => void;
      /** Listen for status notification broadcasts from any window */
      onStatusNotification: (callback: (payload: StatusBroadcast) => void) => () => void;
      /** Listen for panel detached events (main window side) */
      onPanelDetached: (callback: (data: PanelDetachedData) => void) => () => void;
      /** Listen for panel reattach events (main window side) */
      onPanelReattach: (callback: (data: ReattachData) => void) => () => void;
      /** Listen for panel closed events (main window side) */
      onPanelClosed: (callback: (data: PanelClosedData) => void) => () => void;
      /** Listen for theme change broadcasts (panel window side) */
      onPanelThemeChanged: (callback: (data: ThemeChangedData) => void) => () => void;
    };
  }
}

export {};
