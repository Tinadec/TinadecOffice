import path from 'path'
import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
      vue: path.resolve(__dirname, '../../node_modules/vue/dist/vue.runtime-with-vapor.esm-browser.js'),
    },
  },
  test: {
    css: true,
    environment: 'node',
    include: ['src/workbench/WorkbenchShell.test.ts'],
    server: {
      deps: { inline: true },
    },
  },
})
