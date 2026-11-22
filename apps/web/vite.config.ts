import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';
import path from 'path';

export default defineConfig({
  base: '/',
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(import.meta.dirname, '../desktop/src'),
      // TinadecUI — same barrel as desktop so the shared renderer resolves '@tinadec/ui'.
      '@tinadec/ui': path.resolve(import.meta.dirname, '../TinadecUI/src/index.ts'),
    },
  },
  server: {
    host: '127.0.0.1',
    port: 5174,
    strictPort: true,
    proxy: {
      '/api': 'http://127.0.0.1:48730',
      '/auth': 'http://127.0.0.1:48730',
      '/docs': 'http://127.0.0.1:48730',
      '/ws': {
        target: 'http://127.0.0.1:48730',
        ws: true,
      },
    },
    fs: {
      // Repo root covers this package, ../desktop, and the hoisted root
      // node_modules that desktop deps (fonts, monaco) are served from.
      allow: [path.resolve(import.meta.dirname, '../..')],
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  publicDir: path.resolve(import.meta.dirname, '../desktop/public'),
});
