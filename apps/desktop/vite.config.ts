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
      // Vapor SFCs compile to `import { defineVaporComponent } from 'vue'`, which the
      // CJS `vue` entry used under Node (vitest) does not export. Point `vue` at a
      // shim re-exporting both runtime-dom and runtime-vapor so the emitted import
      // resolves under both build and vitest.
      vue: path.resolve(__dirname, './src/lib/vue-shim.ts'),
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
    css: true,
    environment: 'node',
    include: ['src/**/*.test.ts'],
  }
});
