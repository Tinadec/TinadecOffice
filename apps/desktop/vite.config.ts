import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import { VueMcp } from 'vite-plugin-vue-mcp';
import { defineConfig } from 'vite';
import path from 'path';

// Vite 插件顺序即执行顺序；vue-mcp 只为浏览器端 MCP 调试服务，默认关闭：
// 它给每次启动加一个本地 SSE 服务，并往 index.html 注入一个 overlay 模块（会拉起
// @vue/devtools-kit 一整套）。日常开发不需要它，TINADEC_VUE_MCP=1 时再开。
const vueMcpEnabled = ['1', 'true', 'yes'].includes((process.env.TINADEC_VUE_MCP ?? '').trim().toLowerCase());

export default defineConfig({
  base: './',
  plugins: [vue(), tailwindcss(), ...(vueMcpEnabled ? [VueMcp()] : [])],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
      // TinadecUI — the UI engineering suite (Engine = layout). Points at the package
      // barrel so consumers import '@tinadec/ui' regardless of the file's physical home.
      '@tinadec/ui': path.resolve(__dirname, '../TinadecUI/src/index.ts'),
      // Vapor SFCs compile to `import { defineVaporComponent } from 'vue'`, which the
      // CJS `vue` entry used under Node (vitest) does not export. Point `vue` at a
      // shim re-exporting both runtime-dom and runtime-vapor so the emitted import
      // resolves under both build and vitest.
      vue: path.resolve(__dirname, './src/lib/vue-shim.ts'),
      // ponytail: dedupe vue reactivity — desktop nested 3.5.41 vs root 3.6.0-rc.7 caused
      // dual @vue/reactivity Ref brands (TS2345). Force all @vue/* to the single
      // root install so vue-tsc sees one Ref identity.
      '@vue/reactivity': path.resolve(__dirname, '../../node_modules/@vue/reactivity/dist/reactivity.esm-bundler.js'),
      '@vue/runtime-dom': path.resolve(__dirname, '../../node_modules/@vue/runtime-dom/dist/runtime-dom.esm-bundler.js'),
      '@vue/runtime-vapor': path.resolve(__dirname, '../../node_modules/@vue/runtime-vapor/dist/runtime-vapor.esm-bundler.js'),
    },
    dedupe: ['vue', '@vue/reactivity', '@vue/runtime-dom', '@vue/runtime-vapor'],
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true,
    watch: {
      // release/（约 2.8GB）与 runtime/（约 419MB，内含 PortableGit）都是打包产物，不是源。
      // 它们默认落在 project root 之内，chokidar 启动时会把 36467 个文件全爬一遍，
      // 和首次依赖扫描抢同一块 IO：实测冷启动时仅这一步就占 30-110s（见
      // .tinadec_dev/evidence/2026-10-18-perf-dev/README.zh-CN.md）。
      ignored: ['**/release/**', '**/runtime/**', '**/dist/**'],
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    rollupOptions: {
      // The agent browser is a separate satellite window with its own tiny shell page.
      // Keeping it out of the main renderer bundle also keeps the full app bridge away
      // from the window that embeds arbitrary web content.
      input: {
        main: path.resolve(__dirname, 'index.html'),
        agentBrowser: path.resolve(__dirname, 'agent-browser.html'),
      },
    },
  },
  optimizeDeps: {
    // The source graph contains intentional cyclic barrel exports (UIE components ↔
    // components/index) that can wedge the cold-start dependency crawler. Declare the
    // initial browser dependencies instead of crawling every source file. New eager
    // bare imports must be added here; lazy-heavy libraries stay excluded below and
    // are optimized on first use.
    noDiscovery: true,
    holdUntilCrawlEnd: false,
    ignoreOutdatedRequests: true,
    include: [
      '@lucide/vue',
      '@material/material-color-utilities',
      '@monaco-editor/loader',
      '@vue-flow/background',
      '@vue-flow/core',
      '@vue-flow/node-resizer',
      '@vueuse/core',
      '@xterm/xterm',
      '@xterm/addon-fit',
      '@xterm/addon-web-links',
      'class-variance-authority',
      'clsx',
      'dompurify',
      'highlight.js',
      'highlight.js/lib/core',
      'katex',
      'marked',
      'marked-footnote',
      'marked-katex-extension',
      'pinia',
      'reka-ui',
      'tailwind-merge',
      'vue',
      'vue-i18n',
      'vue-router',
    ],
    exclude: [
      '@lobehub/icons-static-svg',
      'mermaid',
      'monaco-editor',
      'pele',
      'pele/lazy',
      'recharts',
      'simple-icons',
    ],
  },
  test: {
    css: true,
    environment: 'node',
    include: ['src/**/*.test.ts'],
    setupFiles: ['./src/testSetup.ts'],
    // The 5s default is vitest's guess, not a measurement of this suite: it has claimed a
    // different victim almost every run (SettingsPage.agentPack, AppearanceSection, and on
    // 2026-09-22 the source-tree scan in toolPresentation plus a ChatroomPanel mount, both
    // while a dotnet solution gate shared the machine). Those tests do real filesystem walks
    // and happy-dom mounts, so their cost tracks machine load, and a red that depends on what
    // else is running is noise that trains people to rerun the gate.
    testTimeout: 20_000,
  }
});
