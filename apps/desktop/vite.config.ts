import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import { VueMcp } from 'vite-plugin-vue-mcp';
import { defineConfig } from 'vite';
import path from 'path';

export default defineConfig({
  base: './',
  plugins: [vue(), tailwindcss(), VueMcp()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  server: {
    host: '127.0.0.1',
    port: 5173,
    strictPort: true
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true
  },
  optimizeDeps: {
    include: [
      '@xterm/xterm',
      '@xterm/addon-fit',
      '@xterm/addon-web-links',
    ],
  },
  test: {
    alias: [
      {
        find: /^vue$/,
        replacement: path.resolve(__dirname, '../../node_modules/vue/dist/vue.runtime.esm-bundler.js'),
      },
      {
        find: /^@vue\/test-utils$/,
        replacement: path.resolve(__dirname, '../../node_modules/@vue/test-utils/dist/vue-test-utils.esm-bundler.mjs'),
      },
    ],
    css: true,
    environment: 'node',
    include: ['src/**/*.test.ts'],
    exclude: ['src/workbench/WorkbenchShell.test.ts'],
    server: {
      deps: {
        inline: true,
      },
    },
  }
});
