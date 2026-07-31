import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';
import path from 'path';

export default defineConfig({
  base: '/',
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, '../desktop/src'),
    },
  },
  server: {
    host: '127.0.0.1',
    port: 5174,
    strictPort: true,
    proxy: {
      '/api': 'http://127.0.0.1:48730',
      '/docs': 'http://127.0.0.1:48730',
      '/ws': {
        target: 'http://127.0.0.1:48730',
        ws: true,
      },
    },
    fs: {
      // Repo root covers this package, ../desktop, and the hoisted root
      // node_modules that desktop deps (fonts, monaco) are served from.
      allow: [path.resolve(__dirname, '../..')],
    },
  },
  build: {
    outDir: 'dist',
    emptyOutDir: true,
  },
  publicDir: path.resolve(__dirname, '../desktop/public'),
});
