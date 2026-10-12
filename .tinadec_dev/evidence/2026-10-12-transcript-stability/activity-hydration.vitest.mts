import { mergeConfig } from 'vite'
import { fileURLToPath } from 'node:url'
import desktopConfig from '../../../apps/desktop/vite.config.ts'

const config = mergeConfig(desktopConfig, {
  root: fileURLToPath(new URL('.', import.meta.url)),
  test: {
    include: ['activity-hydration.bench.test.ts'],
    setupFiles: [fileURLToPath(new URL('../../../apps/desktop/src/testSetup.ts', import.meta.url))],
  },
})
config.test!.setupFiles = [fileURLToPath(new URL('../../../apps/desktop/src/testSetup.ts', import.meta.url))]
export default config
