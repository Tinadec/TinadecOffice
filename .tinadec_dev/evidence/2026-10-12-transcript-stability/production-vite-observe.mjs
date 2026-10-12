import fs from 'node:fs'
import base from '../../../apps/desktop/vite.config.ts'

let transformed = 0
const stateFile = new URL('../../tmp/transcript-vite-progress.json', import.meta.url)
export default {
  ...base,
  root: new URL('../../../apps/desktop/', import.meta.url).pathname.replace(/^\/(\w:)/, '$1'),
  plugins: [...base.plugins, {
    name: 'transcript-build-observation',
    enforce: 'post',
    transform(_code, id) {
      transformed++
      if (transformed % 100 === 0) fs.writeFileSync(stateFile, JSON.stringify({ transformed, last: id, at: new Date().toISOString() }))
    },
  }],
}
