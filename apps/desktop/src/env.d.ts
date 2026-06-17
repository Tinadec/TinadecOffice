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

declare global {
  interface Window {
    tinadec: {
      gatewayUrl: () => string;
      openProjectDialog: () => Promise<string | null>;
      minimizeWindow: () => void;
      maximizeWindow: () => void;
      closeWindow: () => void;
      openDebugStudio: () => Promise<boolean>;
    };
  }
}

export {};
