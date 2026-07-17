import vue from '@vitejs/plugin-vue';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';
import path from 'path';

export default defineConfig({
  plugins: [vue(), tailwindcss()],
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
    css: true,
    environment: 'node',
    include: ['src/**/*.test.ts']
  }
});
