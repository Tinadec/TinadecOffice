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
  id: string;
  shell: string;
  title: string;
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
    imageDataUrl?: string;
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
    fetchCatalog: (force?: boolean) => Promise<PetdexCatalogPet[]>;
    download: (slug: string) => Promise<DownloadedPet>;
    listDownloaded: () => Promise<DownloadedPet[]>;
    setEnabled: (slug: string, enabled: boolean) => Promise<DownloadedPet>;
    openFolder: (slug: string) => Promise<boolean>;
    remove: (slug: string) => Promise<boolean>;
  }

  interface Window {
    tinadec: {
      gatewayUrl: () => string;
      openProjectDialog: () => Promise<string | null>;
      minimizeWindow: () => void;
      maximizeWindow: () => void;
      closeWindow: () => void;
      openDebugStudio: () => Promise<boolean>;
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
