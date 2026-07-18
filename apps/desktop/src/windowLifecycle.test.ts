import { describe, expect, it } from 'vitest'
import app from './App.vue?raw'
import styles from './styles.css?raw'
import indexHtml from '../index.html?raw'
import debugStudioWindow from '../electron/debug-studio.cjs?raw'
import panelWindow from '../electron/panelWindow.cjs?raw'
import viteConfig from '../vite.config.ts?raw'

describe('child window lifecycle', () => {
  it('keeps routed content above the global background', () => {
    expect(styles).toMatch(/\.main-content\s*{[^}]*position:\s*relative;[^}]*z-index:\s*1;/s)
  })

  it('removes main-window size constraints and splash behavior', () => {
    expect(styles).toMatch(/:root\[data-skip-splash][\s\S]*min-width:\s*0;[\s\S]*min-height:\s*0;[\s\S]*overflow:\s*hidden;/)
    expect(indexHtml).toMatch(/:root\[data-skip-splash] \.splash-placeholder\s*{[^}]*display:\s*none;/s)
    expect(app).toContain('if (!isPetWindow && !isChildWindow) startConnection()')
    expect(app).toContain(':css="!isChildWindow"')
    expect(panelWindow).toContain('?splash=0#${hashPath}')
    expect(viteConfig).toContain("base: './'")
  })

  it('discards failed Debug Studio renderers so reopening can rebuild them', () => {
    expect(debugStudioWindow).toContain("win.webContents.on('render-process-gone'")
    expect(debugStudioWindow).toContain("win.webContents.on('did-fail-load'")
    expect(debugStudioWindow).toContain('if (!win.isDestroyed()) win.destroy()')
    expect(debugStudioWindow).toContain('return win.isDestroyed() ? null : win')
  })
})
