/**
 * Tests for normalizeFileSource — the core fix for background functionality.
 *
 * Two separate problems are pinned here:
 *  1. Windows file paths from Electron dialogs (e.g. `C:\Users\image.jpg`) contain
 *     backslashes, which are CSS escape characters inside `url()`.
 *  2. Local files can no longer be addressed as `file:///…`: the packaged window is served
 *     from `app://bundle` with same-origin policy enabled, so a `file://` subresource is
 *     refused. They travel through the `tinadec-media://` scheme instead
 *     (see electron/localMedia.cjs, which decodes exactly this token).
 */

import { describe, it, expect } from 'vitest'
import { createRequire } from 'node:module'
import {
  MEDIA_URL_PREFIX,
  encodeMediaPathToken,
  normalizeBackgroundSettings,
  normalizeFileSource,
} from './useBackground'
import type { BackgroundSettings } from '../types/background'

/** Inverse of the encoder, so a case can assert the path that will be served. */
function decodeMediaUrl(url: string): string {
  const token = url.slice(MEDIA_URL_PREFIX.length)
  const base64 = token.replace(/-/g, '+').replace(/_/g, '/')
  const binary = atob(base64 + '='.repeat((4 - (base64.length % 4)) % 4))
  const bytes = Uint8Array.from(binary, (ch) => ch.charCodeAt(0))
  return new TextDecoder().decode(bytes)
}

describe('normalizeFileSource', () => {
  // --- Windows paths (the primary bug) ---

  it('addresses a Windows backslash path through the media scheme', () => {
    const result = normalizeFileSource('C:\\Users\\test\\image.jpg')
    expect(result).toBe(`${MEDIA_URL_PREFIX}${encodeMediaPathToken('C:/Users/test/image.jpg')}`)
    expect(decodeMediaUrl(result)).toBe('C:/Users/test/image.jpg')
  })

  it('handles Windows paths with spaces', () => {
    const result = normalizeFileSource('C:\\Users\\My User\\background image.png')
    expect(decodeMediaUrl(result)).toBe('C:/Users/My User/background image.png')
  })

  it('handles Windows UNC paths', () => {
    // UNC paths like \\server\share\file.jpg start with backslashes
    // but don't match the drive-letter pattern, so they fall through
    // to the "starts with /" case after trimming doesn't apply.
    // Actually \\ doesn't start with /, so it returns as-is.
    const result = normalizeFileSource('\\\\server\\share\\file.jpg')
    // This doesn't match drive-letter or Unix path, returns as-is
    expect(result).toBe('\\\\server\\share\\file.jpg')
  })

  // --- Already-URL strings (should pass through) ---

  it('passes through http:// URLs unchanged', () => {
    const url = 'http://example.com/image.jpg'
    expect(normalizeFileSource(url)).toBe(url)
  })

  it('passes through an already-resolved media URL unchanged', () => {
    const url = `${MEDIA_URL_PREFIX}${encodeMediaPathToken('C:/Users/image.jpg')}`
    expect(normalizeFileSource(url)).toBe(url)
  })

  it('re-addresses a legacy file:// URL, which the window can no longer load', () => {
    // Older builds persisted file:///…; the value has to be migrated on read or the
    // background silently disappears after the upgrade.
    expect(normalizeFileSource('file:///C:/Users/image.jpg'))
      .toBe(`${MEDIA_URL_PREFIX}${encodeMediaPathToken('C:/Users/image.jpg')}`)
  })

  it('passes through data: URLs unchanged', () => {
    const url = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUg=='
    expect(normalizeFileSource(url)).toBe(url)
  })

  it('passes through blob: URLs unchanged', () => {
    const url = 'blob:http://127.0.0.1:5173/abc-123-def'
    expect(normalizeFileSource(url)).toBe(url)
  })

  // --- Unix paths ---

  // --- Edge cases ---

  it('returns empty string unchanged', () => {
    expect(normalizeFileSource('')).toBe('')
  })

  it('trims whitespace before processing', () => {
    const result = normalizeFileSource('  C:\\Users\\image.jpg  ')
    expect(decodeMediaUrl(result)).toBe('C:/Users/image.jpg')
  })

  it('returns relative paths unchanged', () => {
    const result = normalizeFileSource('images/background.jpg')
    expect(result).toBe('images/background.jpg')
  })

  it('does not corrupt HTML content (used for html background type)', () => {
    const html = '<div style="background: linear-gradient(135deg, #667eea, #764ba2); width: 100%; height: 100%;"></div>'
    expect(normalizeFileSource(html)).toBe(html)
  })
})

/**
 * The renderer builds the media URL and the main process decodes it, in two different
 * languages' base64 implementations. If they disagree the background stops loading with no
 * error anywhere, so the wire format is pinned against the real decoder.
 */
describe('media URL wire format (renderer encoder ↔ main-process decoder)', () => {
  const require = createRequire(import.meta.url)
  const { mediaPathFromUrl } = require('../../electron/localMedia.cjs') as {
    mediaPathFromUrl: (url: string) => string | null
  }

  it('decodes back to the exact path the renderer meant', () => {
    for (const filePath of [
      'C:/Users/test/image.jpg',
      'D:/photos/background image.png',
      '/home/user/视频/webm clip.webm',
      'C:/Users/测试/壁纸 图.png',
    ]) {
      const url = normalizeFileSource(filePath)
      expect(mediaPathFromUrl(url), filePath).toBe(filePath)
    }
  })

  it('refuses a path the scheme will not serve', () => {
    // The extension allowlist lives in the main process; nothing here should imply one.
    expect(mediaPathFromUrl(normalizeFileSource('C:/Users/notes.txt'))).toBeNull()
  })
})

/**
 * The opacity slider used to range 0–1 while the contract is 0–100, so a single
 * drag wrote 0.5 into a percent field and App.vue divided it again, leaving the
 * background at 0.5% opacity. These tests pin the unit contract and the
 * migration of already-persisted 0–1 values.
 */
describe('normalizeBackgroundSettings (opacity/blur units)', () => {
  it('keeps percent opacity in the 0–100 domain', () => {
    const out = normalizeBackgroundSettings({ type: 'image', source: 'a.jpg', opacity: 70, blur: 6 })
    expect(out.opacity).toBe(70)
    expect(out.blur).toBe(6)
  })

  it('migrates legacy 0–1 opacity to percent', () => {
    // The old slider wrote 0.5 for "half" — it must become 50%, not 0.5%.
    expect(normalizeBackgroundSettings({ opacity: 0.5 }).opacity).toBe(50)
    expect(normalizeBackgroundSettings({ opacity: 1 }).opacity).toBe(100)
    expect(normalizeBackgroundSettings({ opacity: 0.05 }).opacity).toBe(5)
  })

  it('treats zero opacity as zero in either domain', () => {
    expect(normalizeBackgroundSettings({ opacity: 0 }).opacity).toBe(0)
  })

  it('clamps blur to the 0–20 range the slider offers', () => {
    expect(normalizeBackgroundSettings({ blur: 30 }).blur).toBe(20)
    expect(normalizeBackgroundSettings({ blur: -5 }).blur).toBe(0)
  })

  it('falls back to defaults for corrupt or missing values', () => {
    const out = normalizeBackgroundSettings({ type: 'nonsense', opacity: Number.NaN })
    expect(out.type).toBe('none')
    expect(out.opacity).toBe(100)
    expect(out.blur).toBe(0)
  })

  it('rounds fractional values so the label matches the stored number', () => {
    expect(normalizeBackgroundSettings({ opacity: 62.4 }).opacity).toBe(62)
    expect(normalizeBackgroundSettings({ blur: 7.6 }).blur).toBe(8)
  })

  it('clamps persisted opacity into 0–100 instead of trusting the file', () => {
    // App.vue and BackgroundPreview feed this straight into inline styles, so an
    // out-of-range value would silently become invalid CSS.
    expect(normalizeBackgroundSettings({ opacity: 150 }).opacity).toBe(100)
    expect(normalizeBackgroundSettings({ opacity: -20 }).opacity).toBe(0)
    expect(normalizeBackgroundSettings({ opacity: 9999 }).opacity).toBe(100)
    // 1.5 is above the legacy 0–1 band, so it is read as a percent (2%) rather
    // than scaled — only the ≤1 band is the old slider's unit.
    expect(normalizeBackgroundSettings({ opacity: 1.5 }).opacity).toBe(2)
    expect(normalizeBackgroundSettings({ opacity: -0.5 }).opacity).toBe(0)
  })

  it('rejects non-finite opacity and blur', () => {
    for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, Number.NEGATIVE_INFINITY]) {
      const out = normalizeBackgroundSettings({ opacity: bad, blur: bad })
      expect(out.opacity, String(bad)).toBe(100)
      expect(out.blur, String(bad)).toBe(0)
    }
  })
})

/**
 * size / position / repeat are union-typed and land in inline styles, so a
 * corrupt persisted string (empty, typo, arbitrary CSS) must fall back rather
 * than reach the DOM.
 */
describe('normalizeBackgroundSettings (union fields)', () => {
  it('keeps every legal member of each union', () => {
    for (const size of ['cover', 'contain', 'auto'] as const) {
      expect(normalizeBackgroundSettings({ size }).size).toBe(size)
    }
    for (const position of ['center', 'top', 'bottom', 'left', 'right'] as const) {
      expect(normalizeBackgroundSettings({ position }).position).toBe(position)
    }
    for (const repeat of ['no-repeat', 'repeat', 'repeat-x', 'repeat-y'] as const) {
      expect(normalizeBackgroundSettings({ repeat }).repeat).toBe(repeat)
    }
  })

  it('falls back to defaults for empty, unknown, or wrong-typed values', () => {
    for (const bad of ['', '  ', 'COVER', 'fill', 'middle', 'repeat-z', 'none']) {
      const out = normalizeBackgroundSettings({ size: bad, position: bad, repeat: bad })
      expect(out.size, bad).toBe('cover')
      expect(out.position, bad).toBe('center')
      expect(out.repeat, bad).toBe('no-repeat')
    }
    const typed = normalizeBackgroundSettings({
      size: 1 as unknown as BackgroundSettings['size'],
      position: null as unknown as BackgroundSettings['position'],
      repeat: {} as unknown as BackgroundSettings['repeat'],
    })
    expect(typed.size).toBe('cover')
    expect(typed.position).toBe('center')
    expect(typed.repeat).toBe('no-repeat')
  })

  it('always returns a fully valid object for arbitrary garbage', () => {
    for (const junk of [null, undefined, 42, 'nope', [], { size: 1 }]) {
      const out = normalizeBackgroundSettings(junk)
      expect(['cover', 'contain', 'auto']).toContain(out.size)
      expect(['center', 'top', 'bottom', 'left', 'right']).toContain(out.position)
      expect(['no-repeat', 'repeat', 'repeat-x', 'repeat-y']).toContain(out.repeat)
      expect(out.opacity).toBeGreaterThanOrEqual(0)
      expect(out.opacity).toBeLessThanOrEqual(100)
      expect(out.blur).toBeGreaterThanOrEqual(0)
      expect(out.blur).toBeLessThanOrEqual(20)
    }
  })
})
