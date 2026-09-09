/**
 * Tests for normalizeFileSource — the core fix for background functionality.
 *
 * Before this fix, Windows file paths from Electron dialogs (e.g. `C:\Users\image.jpg`)
 * were used directly in CSS `url('C:\Users\image.jpg')`, where backslashes act as
 * CSS escape characters, causing background images to fail loading.
 */

import { describe, it, expect } from 'vitest'
import { normalizeBackgroundSettings, normalizeFileSource } from './useBackground'

describe('normalizeFileSource', () => {
  // --- Windows paths (the primary bug) ---

  it('converts Windows backslash path to file:/// URL', () => {
    const result = normalizeFileSource('C:\\Users\\test\\image.jpg')
    expect(result).toBe('file:///C:/Users/test/image.jpg')
  })

  it('converts Windows forward-slash path to file:/// URL', () => {
    const result = normalizeFileSource('D:/photos/video.mp4')
    expect(result).toBe('file:///D:/photos/video.mp4')
  })

  it('handles Windows paths with spaces', () => {
    const result = normalizeFileSource('C:\\Users\\My User\\background image.png')
    expect(result).toBe('file:///C:/Users/My User/background image.png')
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

  it('passes through https:// URLs unchanged', () => {
    const url = 'https://example.com/background.png'
    expect(normalizeFileSource(url)).toBe(url)
  })

  it('passes through file:// URLs unchanged', () => {
    const url = 'file:///C:/Users/image.jpg'
    expect(normalizeFileSource(url)).toBe(url)
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

  it('converts Unix absolute path to file:// URL', () => {
    const result = normalizeFileSource('/home/user/image.jpg')
    expect(result).toBe('file:///home/user/image.jpg')
  })

  // --- Edge cases ---

  it('returns empty string unchanged', () => {
    expect(normalizeFileSource('')).toBe('')
  })

  it('trims whitespace before processing', () => {
    const result = normalizeFileSource('  C:\\Users\\image.jpg  ')
    expect(result).toBe('file:///C:/Users/image.jpg')
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
})
