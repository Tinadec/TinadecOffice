import { defineConfig } from 'vitest/config'
import path from 'node:path'

// TinadecUI tests run standalone (own runner). The `@` alias points at the
// desktop renderer src so engine files that reference app code resolve the
// same way they do under the desktop/web bundles.
export default defineConfig({
  resolve: {
    alias: {
      '@': path.resolve(__dirname, '../desktop/src'),
    },
  },
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
})
