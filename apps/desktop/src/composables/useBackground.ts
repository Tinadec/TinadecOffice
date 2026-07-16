/**
 * Background management composable for TinadecOffice Desktop
 * Handles background settings persistence, file selection, and DOM application
 */

import { useStorage } from '@vueuse/core'
import { ref, type Ref } from 'vue'
import {
  type BackgroundSettings,
  type BackgroundType,
  DEFAULT_BACKGROUND_SETTINGS,
} from '../types/background'

// Storage key for background settings
const STORAGE_KEY = 'tinadec-background'

/**
 * Get stored background settings reference (lazy initialization)
 */
let stored: Ref<BackgroundSettings> | null = null

function getStoredBackground(): Ref<BackgroundSettings> {
  if (!stored) {
    stored = useStorage<BackgroundSettings>(STORAGE_KEY, { ...DEFAULT_BACKGROUND_SETTINGS })
  }
  return stored
}

/**
 * Normalize a file source path to a valid URL for CSS and HTML use.
 *
 * Windows paths returned by Electron dialog (e.g. `C:\Users\image.jpg`)
 * contain backslashes which are CSS escape characters in `url()`.
 * This function converts them to `file:///` URLs so they work correctly
 * in CSS `url()`, `<img src>`, and `<video src>`.
 *
 * - Already-URL strings (http://, https://, file://, data:) are returned as-is.
 * - Unix absolute paths (/home/...) are converted to file:// URLs.
 * - Windows drive-letter paths (C:\...) are converted to file:/// URLs.
 */
export function normalizeFileSource(source: string): string {
  if (!source) return source

  const trimmed = source.trim()

  // Already a URL — return as-is
  if (
    trimmed.startsWith('http://') ||
    trimmed.startsWith('https://') ||
    trimmed.startsWith('file://') ||
    trimmed.startsWith('data:') ||
    trimmed.startsWith('blob:')
  ) {
    return trimmed
  }

  // Windows drive-letter path (e.g. C:\Users\..., D:/photos/img.png)
  if (/^[a-zA-Z]:[\\/]/.test(trimmed)) {
    // Convert backslashes to forward slashes, then encode for file:// URL
    const forwardSlashes = trimmed.replace(/\\/g, '/')
    // file:///C:/Users/image.jpg
    return `file:///${forwardSlashes}`
  }

  // Unix absolute path
  if (trimmed.startsWith('/')) {
    return `file://${trimmed}`
  }

  // Relative path or anything else — return as-is (may work in dev server context)
  return trimmed
}

/**
 * Select background file using Electron dialog
 */
async function selectBackgroundFile(type: BackgroundType): Promise<string | null> {
  // Check if Electron API is available
  const tinadec = (window as any).tinadec
  if (!tinadec?.selectBackgroundFile) {
    console.warn('Electron file dialog API not available')
    return null
  }

  try {
    const result = await tinadec.selectBackgroundFile(type)
    return result || null
  } catch (error) {
    console.error('Failed to select background file:', error)
    return null
  }
}

export function useBackground() {
  const backgroundSettings = getStoredBackground()
  const isApplying = ref(false)

  /**
   * Apply current background settings to DOM.
   * Sets a data attribute on the root element for CSS targeting.
   * The actual visual rendering is handled by the template in HomePage.vue
   * using reactive inline styles.
   */
  function applyBackground(): void {
    isApplying.value = true
    const root = document.documentElement
    root.setAttribute('data-bg-type', backgroundSettings.value.type)
    isApplying.value = false
  }

  /**
   * Update background type
   */
  function setBackgroundType(type: BackgroundType): void {
    backgroundSettings.value = {
      ...backgroundSettings.value,
      type,
      // Reset source when changing type
      source: type === 'none' ? '' : backgroundSettings.value.source,
    }
  }

  /**
   * Update background source (URL or file path).
   * Automatically normalizes Windows file paths to file:/// URLs.
   * For HTML backgrounds, the source is raw HTML content and is not normalized.
   */
  function setBackgroundSource(source: string): void {
    const currentType = backgroundSettings.value.type
    backgroundSettings.value = {
      ...backgroundSettings.value,
      source: currentType === 'html' ? source : normalizeFileSource(source),
    }
  }

  /**
   * Update background opacity (0-100)
   */
  function setBackgroundOpacity(opacity: number): void {
    backgroundSettings.value = {
      ...backgroundSettings.value,
      opacity: Math.max(0, Math.min(100, opacity)),
    }
  }

  /**
   * Update background blur (0-20px)
   */
  function setBackgroundBlur(blur: number): void {
    backgroundSettings.value = {
      ...backgroundSettings.value,
      blur: Math.max(0, Math.min(20, blur)),
    }
  }

  /**
   * Update background size
   */
  function setBackgroundSize(size: 'cover' | 'contain' | 'auto'): void {
    backgroundSettings.value = {
      ...backgroundSettings.value,
      size,
    }
  }

  /**
   * Update background position
   */
  function setBackgroundPosition(position: 'center' | 'top' | 'bottom' | 'left' | 'right'): void {
    backgroundSettings.value = {
      ...backgroundSettings.value,
      position,
    }
  }

  /**
   * Update background repeat
   */
  function setBackgroundRepeat(repeat: 'no-repeat' | 'repeat' | 'repeat-x' | 'repeat-y'): void {
    backgroundSettings.value = {
      ...backgroundSettings.value,
      repeat,
    }
  }

  /**
   * Select file and update source.
   * Uses Electron dialog to pick a local file, then normalizes the path.
   */
  async function selectFile(): Promise<void> {
    const type = backgroundSettings.value.type
    if (type === 'none' || type === 'html') {
      return
    }

    const filePath = await selectBackgroundFile(type)
    if (filePath) {
      setBackgroundSource(filePath)
    }
  }

  /**
   * Reset background to default settings
   */
  function resetBackground(): void {
    backgroundSettings.value = { ...DEFAULT_BACKGROUND_SETTINGS }
  }

  return {
    // State
    settings: backgroundSettings,
    isApplying,

    // Methods
    applyBackground,
    setBackgroundType,
    setBackgroundSource,
    setBackgroundOpacity,
    setBackgroundBlur,
    setBackgroundSize,
    setBackgroundPosition,
    setBackgroundRepeat,
    selectFile,
    resetBackground,
  }
}
