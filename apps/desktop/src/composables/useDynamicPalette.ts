/**
 * Dynamic (Monet) palette composable for TinadecOffice Desktop.
 *
 * Watches the image background settings and keeps a cached extraction of
 * the current wallpaper under `tinadec-dynamic-palette`. Extraction is
 * deterministic (see lib/monetExtract.ts), so every renderer window
 * independently computes identical palettes — no cross-window protocol
 * changes needed.
 *
 * Lifecycle rules agreed in settings design:
 * - Selecting an image extracts silently but NEVER switches the user's
 *   accent choice.
 * - Switching to video/html/none freezes the last extracted palette
 *   (cache is kept, CSS vars stay applied).
 * - Re-selecting an image resumes live re-extraction while dynamic is on.
 */

import { StorageSerializers, useStorage } from '@vueuse/core'
import { ref, watch, type Ref } from 'vue'
import type { DynamicPalette } from '@/lib/monetExtract'
import { buildDynamicVars, extractSourceColor } from '@/lib/monetExtract'
import { useBackground } from './useBackground'
import { useNotifications } from './useNotifications'

const STORAGE_KEY = 'tinadec-dynamic-palette'
/** Longest edge of the downscaled sampling bitmap — Monet samples small. */
const SAMPLE_SIZE = 64

let cachedRef: Ref<DynamicPalette | null> | null = null
let watchArmed = false
let extracting = false
/** Source string successfully extracted during this session (dedupe). */
let extractedFor: string | null = null
/** Source string whose failure was already reported (notify once). */
let lastNotifiedFor: string | null = null

export function __resetDynamicPaletteForTests(): void {
  cachedRef = null
  watchArmed = false
  extracting = false
}

/** Lazy singleton ref so useTheme can consume the palette reactively. */
export function getDynamicPaletteRef(): Ref<DynamicPalette | null> {
  if (!cachedRef) {
    // Explicit object serializer: a null default would otherwise select the
    // "any" serializer, which returns the raw JSON string instead of parsing.
    cachedRef = useStorage<DynamicPalette | null>(
      STORAGE_KEY,
      null,
      undefined,
      { serializer: StorageSerializers.object, mergeDefaults: false },
    )
    if (
      cachedRef.value !== null &&
      (typeof cachedRef.value !== 'object' ||
        typeof cachedRef.value.source !== 'string' ||
        typeof cachedRef.value.sourceColor !== 'number' ||
        !cachedRef.value.dark ||
        !cachedRef.value.light)
    ) {
      cachedRef.value = null
    }
  }
  return cachedRef
}

/**
 * Arm the background watcher once (called at startup next to useTheme()).
 * Idempotent; safe to call from every window including pet/panel windows.
 */
export function useDynamicPalette(): Ref<DynamicPalette | null> {
  const palette = getDynamicPaletteRef()

  if (!watchArmed) {
    watchArmed = true
    const { settings } = useBackground()

    watch(
      () => [settings.value.type, settings.value.source] as const,
      ([type, source]) => {
        // Freeze semantics: leaving image backgrounds keeps the cached
        // palette; only ever forward when there is something new to take.
        if (type !== 'image' || !source) return
        if (source === extractedFor) return
        // Startup with an already-cached palette for the same image.
        if (!extractedFor && palette.value?.source === source) {
          extractedFor = source
          return
        }
        void extract(source)
      },
      { immediate: true },
    )
  }

  return palette
}

async function extract(source: string): Promise<void> {
  const palette = getDynamicPaletteRef()
  extracting = true
  try {
    const argbPixels = await loadImagePixels(source)
    const sourceColor = extractSourceColor(argbPixels)
    if (sourceColor === null) {
      throw new Error('no usable colors')
    }
    palette.value = {
      source,
      sourceColor,
      dark: buildDynamicVars(sourceColor, 'dark'),
      light: buildDynamicVars(sourceColor, 'light'),
    }
    extractedFor = source
  } catch (error) {
    // Keep any previous frozen palette; surface a deduped error so a dead
    // option never looks like a silent no-op.
    if (palette.value?.source !== source && lastNotifiedFor !== source) {
      lastNotifiedFor = source
      console.warn('[dynamicPalette] extraction failed:', error)
      useNotifications().notify.error(error, {
        title: 'Background color extraction failed',
        source: 'settings',
      })
    }
  } finally {
    extracting = false
  }
}

/** Decode + downscale an image source into ARGB ints for quantization. */
async function loadImagePixels(source: string): Promise<number[]> {
  const url = await resolveLoadUrl(source)

  const img = new Image()
  if (/^https?:/i.test(url)) img.crossOrigin = 'anonymous'
  await new Promise<void>((resolve, reject) => {
    img.onload = () => resolve()
    img.onerror = () => reject(new Error(`image decode failed: ${source}`))
    img.src = url
  })
  // onload alone races the decoder: drawImage can paint a fully transparent
  // bitmap before pixel data is ready. decode() resolves only when pixels
  // are actually usable.
  if (typeof img.decode === 'function') {
    try {
      await img.decode()
    } catch {
      /* undecodable via decode() — onload already guaranteed the bytes; let drawImage try */
    }
  }

  const scale = Math.min(1, SAMPLE_SIZE / Math.max(img.naturalWidth || 1, img.naturalHeight || 1))
  const w = Math.max(1, Math.round((img.naturalWidth || SAMPLE_SIZE) * scale))
  const h = Math.max(1, Math.round((img.naturalHeight || SAMPLE_SIZE) * scale))

  const canvas = document.createElement('canvas')
  canvas.width = w
  canvas.height = h
  const ctx = canvas.getContext('2d', { willReadFrequently: true })
  if (!ctx) throw new Error('canvas 2d unavailable')
  ctx.drawImage(img, 0, 0, w, h)
  const { data } = ctx.getImageData(0, 0, w, h)

  const pixels: number[] = []
  for (let i = 0; i < data.length; i += 4) {
    // RGBA layout: alpha lives at i+3. Alpha MUST be packed into the value:
    // QuantizerWu skips pixels with alpha < 255, so alpha-less entries yield
    // an empty histogram and a null source color.
    if (data[i + 3]! < 255) continue
    pixels.push(((0xff << 24) | (data[i]! << 16) | (data[i + 1]! << 8) | data[i + 2]!) >>> 0)
  }
  return pixels
}

/**
 * Local paths and file:// URLs go through the main process (the dev
 * renderer's http origin cannot read file:// pixels — tainted canvas).
 * Remote/data URLs load directly; CORS failures reject naturally.
 */
async function resolveLoadUrl(source: string): Promise<string> {
  if (/^https?:/i.test(source) || source.startsWith('data:') || source.startsWith('blob:')) {
    return source
  }
  const tinadec = (globalThis as { tinadec?: { readImageAsDataUrl?: (s: string) => Promise<string | null> } }).tinadec
  if (!tinadec?.readImageAsDataUrl) throw new Error('readImageAsDataUrl bridge unavailable')
  const dataUrl = await tinadec.readImageAsDataUrl(source)
  if (!dataUrl) throw new Error(`unreadable background file: ${source}`)
  return dataUrl
}
