// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest'
import settingsCss from '../settings/settings.css?raw'
import stylesCss from '../styles.css?raw'
import {
  SELECTABLE_CONTENT_SELECTORS,
  buildSelectionMenuActions,
  detectLink,
  resolveSelectionContext,
  type SelectionContext,
} from './selectionContext'

function context(patch: Partial<SelectionContext> = {}): SelectionContext {
  return {
    surface: 'content',
    text: 'hello',
    hasSelection: true,
    editable: false,
    readOnly: false,
    link: null,
    ...patch,
  }
}

describe('selection context resolution', () => {
  it('classifies a selectable content surface', () => {
    const el = document.createElement('div')
    el.className = 'markdown-body'
    const result = resolveSelectionContext(el, null)
    expect(result.surface).toBe('content')
  })

  it('classifies editable and read-only fields distinctly', () => {
    const input = document.createElement('input')
    expect(resolveSelectionContext(input, null)).toMatchObject({ surface: 'field', editable: true, readOnly: false })

    const readonly = document.createElement('input')
    readonly.readOnly = true
    expect(resolveSelectionContext(readonly, null)).toMatchObject({ surface: 'field', editable: false, readOnly: true })

    const area = document.createElement('textarea')
    expect(resolveSelectionContext(area, null)).toMatchObject({ surface: 'field', editable: true })
  })

  it('treats a terminal as its own surface even without a DOM selection', () => {
    const xterm = document.createElement('div')
    xterm.className = 'xterm'
    const inner = document.createElement('div')
    xterm.appendChild(inner)
    // xterm paints to canvas: no DOM selection exists, but the surface must still be terminal.
    expect(resolveSelectionContext(inner, null)).toMatchObject({ surface: 'terminal', hasSelection: false })
  })

  it('falls back to chrome for plain UI with no selection', () => {
    const button = document.createElement('button')
    expect(resolveSelectionContext(button, null)).toMatchObject({ surface: 'chrome', hasSelection: false })
  })

  it('detects an http link from an ancestor anchor and ignores unsafe hrefs', () => {
    const anchor = document.createElement('a')
    anchor.href = 'https://example.com/docs'
    const span = document.createElement('span')
    anchor.appendChild(span)
    expect(detectLink(span, '')).toBe('https://example.com/docs')

    const unsafe = document.createElement('a')
    unsafe.setAttribute('href', 'javascript:alert(1)')
    expect(detectLink(unsafe, '')).toBeNull()
  })

  it('detects a bare URL selection', () => {
    expect(detectLink(null, '  https://tinadec.dev/x  ')).toBe('https://tinadec.dev/x')
    expect(detectLink(null, 'not a url')).toBeNull()
  })
})

describe('selection menu action matrix', () => {
  it('offers copy/select-all for a content selection, no cut or paste', () => {
    const keys = buildSelectionMenuActions(context()).map((a) => a.key)
    expect(keys).toEqual(['copy', 'selectAll'])
  })

  it('adds cut and paste for an editable field with a selection', () => {
    const keys = buildSelectionMenuActions(context({ surface: 'field', editable: true })).map((a) => a.key)
    expect(keys).toEqual(['copy', 'cut', 'paste', 'selectAll'])
  })

  it('omits cut for an editable field with a collapsed caret but keeps paste', () => {
    const keys = buildSelectionMenuActions(context({ surface: 'field', editable: true, hasSelection: false, text: '' })).map((a) => a.key)
    expect(keys).toEqual(['paste', 'selectAll'])
  })

  it('never offers cut or paste in a read-only field', () => {
    const keys = buildSelectionMenuActions(context({ surface: 'field', editable: false, readOnly: true })).map((a) => a.key)
    expect(keys).toEqual(['copy', 'selectAll'])
  })

  it('gives the terminal copy/paste/select-all plus a clear action', () => {
    const keys = buildSelectionMenuActions(context({ surface: 'terminal', hasSelection: false, text: '' })).map((a) => a.key)
    expect(keys).toEqual(['copy', 'paste', 'selectAll', 'clearTerminal'])
  })

  it('appends link actions when the target or selection is a URL', () => {
    const keys = buildSelectionMenuActions(context({ link: 'https://example.com' })).map((a) => a.key)
    expect(keys).toEqual(['copy', 'selectAll', 'copyLink', 'openLink'])
  })

  it('returns nothing for plain chrome, so the menu stays out of the way', () => {
    expect(buildSelectionMenuActions(context({ surface: 'chrome', hasSelection: false, text: '' }))).toEqual([])
  })
})

describe('selectable selector contract', () => {
  it('matches the user-select opt-in list in styles.css', () => {
    // A surface that is selectable but unknown to the menu would silently lose
    // its context actions, so both lists must stay in lockstep.
    const optIn = stylesCss.match(/(input,\s*\ntextarea,[^{]*)\{([^}]+)\}/)
    expect(optIn).not.toBeNull()
    const cssSelectors = optIn![1]
      .split(/[,\n]/)
      .map((s) => s.trim())
      .filter((s) => s && s !== 'input' && s !== 'textarea')
    for (const selector of cssSelectors) {
      expect(SELECTABLE_CONTENT_SELECTORS).toContain(selector as (typeof SELECTABLE_CONTENT_SELECTORS)[number])
    }
  })

  it('does not rely on settings.css for the selection policy', () => {
    // The policy is global (styles.css), not settings-page scoped.
    expect(settingsCss).not.toMatch(/\.message-content\s*\{[^}]*user-select/)
  })
})
