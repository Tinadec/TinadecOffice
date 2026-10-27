<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch, type Component } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, Copy, Info, Lightbulb, MessageSquareWarning, OctagonAlert, TriangleAlert } from '@lucide/vue'
import { Marked } from 'marked'
import markedFootnote from 'marked-footnote'
import markedKatex from 'marked-katex-extension'
import DOMPurify from 'dompurify'
import 'katex/dist/katex.min.css'
import { highlightCode } from '@/lib/highlight'
import type { DocumentRef } from '@/lib/documentPath'
import MarkdownDiagram from './MarkdownDiagram.vue'
import UiIslandCard from './ui/island-card.vue'

const props = defineProps<{
  content: string
  /**
   * The content is still being revealed. Parsing then runs at most once per
   * <see cref="PARSE_INTERVAL_MS"/> instead of on every frame: a paced stream changes the text
   * every animation frame, and re-parsing, sanitising and highlighting the whole reply at 60 Hz
   * costs more than the frame it shows up in (measured: 20-37 ms per parse of a 12K reply).
   */
  streaming?: boolean
  /**
   * Optional document-reference resolver. When omitted the renderer behaves exactly as
   * before (space mode and the sub-agent cards pass nothing); when supplied, an inline
   * code span or a link that resolves to a real workspace file becomes a clickable
   * reference.
   */
  docRefs?: ((raw: string) => DocumentRef | null) | null
  /** Changing this re-parses the blocks so late verifications can upgrade plain text. */
  docRefsVersion?: number
}>()

const emit = defineEmits<{ 'open-doc': [ref: DocumentRef] }>()

const { t } = useI18n()

const PARSE_INTERVAL_MS = 80
/** What `blocks` parses: the newest content, or the newest one that is old enough to parse. */
const parsedContent = ref(props.content)
let parseTimer: ReturnType<typeof setTimeout> | null = null
let parsedAt = 0

function parseNow() {
  if (parseTimer !== null) { clearTimeout(parseTimer); parseTimer = null }
  parsedAt = performance.now()
  parsedContent.value = props.content
}

watch([() => props.content, () => props.streaming], () => {
  // Settled content is shown at once: a late throttled parse would make the final reply differ
  // from the one the next render reads.
  if (!props.streaming) { parseNow(); return }
  if (parseTimer !== null) return
  const wait = Math.max(0, PARSE_INTERVAL_MS - (performance.now() - parsedAt))
  if (wait === 0) parseNow()
  else parseTimer = setTimeout(parseNow, wait)
})

/**
 * A reply is continuous prose plus island cards: code, tables, quotes and
 * callouts each reuse the shared card instead of inventing a second surface.
 */
type CalloutKind = 'note' | 'tip' | 'important' | 'warning' | 'caution'

type MarkdownBlock =
  | { kind: 'prose'; html: string }
  | { kind: 'island'; html: string }
  | { kind: 'code'; html: string; code: string; language: string | null }
  | { kind: 'callout'; callout: CalloutKind; title: string; html: string }
  | { kind: 'diagram'; code: string }

const CALLOUT_ICONS: Record<CalloutKind, Component> = {
  note: Info,
  tip: Lightbulb,
  important: MessageSquareWarning,
  warning: TriangleAlert,
  caution: OctagonAlert,
}

/** GitHub-style alert marker: `> [!NOTE]`, with an optional title on the same line. */
const CALLOUT_MARKER = /^\s*\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(.*)$/i

/** Fences whose "language" says nothing useful keep the header free of a label. */
const UNLABELLED_LANGUAGES = new Set(['text', 'txt', 'plain', 'plaintext', 'none', ''])

const marked = new Marked()
marked.use(markedKatex({ throwOnError: false, strict: 'ignore' }))
marked.use(markedFootnote())

/**
 * Document references: an inline code span or a relative link whose text resolves to a
 * real workspace file. The host element keeps `data-doc-*` instead of a Vue handler
 * because the prose arrives through `v-html`; the click is delegated on `bodyRef`.
 */
function markDocumentRefs(fragment: DocumentFragment): void {
  const resolve = props.docRefs
  if (!resolve) return
  for (const code of Array.from(fragment.querySelectorAll('code'))) {
    // Fenced blocks are their own island and are never a reference.
    if (code.closest('pre')) continue
    const raw = (code.textContent ?? '').trim()
    const ref = resolve(raw)
    if (!ref) continue
    wrapReference(code, ref)
  }
  for (const anchor of Array.from(fragment.querySelectorAll('a[href]'))) {
    const href = anchor.getAttribute('href') ?? ''
    // Only relative links can be workspace documents; schemes and in-page anchors cannot.
    if (!href || href.startsWith('#') || /^[A-Za-z][A-Za-z0-9+.-]*:/.test(href)) continue
    const ref = resolve((anchor.textContent ?? '').trim() || href)
    if (!ref) continue
    decorateReference(anchor, ref)
  }
}

function decorateReference(element: Element, ref: DocumentRef): void {
  element.classList.add('markdown-doc-ref')
  element.setAttribute('data-doc-path', ref.path)
  element.setAttribute('role', 'button')
  element.setAttribute('tabindex', '0')
  if (ref.line) element.setAttribute('data-doc-line', String(ref.line))
  element.setAttribute(
    'title',
    ref.line
      ? t('chat.markdownDocRefLine', { path: ref.path, line: ref.line })
      : t('chat.markdownDocRef', { path: ref.path }),
  )
}

/**
 * The reference keeps its `<code>` so the inline-code typography still applies; the
 * wrapper is the click target.
 */
function wrapReference(code: Element, ref: DocumentRef): void {
  const anchor = document.createElement('a')
  anchor.setAttribute('href', '#')
  decorateReference(anchor, ref)
  code.replaceWith(anchor)
  anchor.appendChild(code)
}

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, character => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
  })[character] as string)
}

/**
 * The html goes back through `v-html`, so a text node is escaped: a model that
 * types `&lt;img onerror=…&gt;` must still see text, not markup.
 */
function outerHtml(node: ChildNode): string {
  if (node.nodeType === Node.TEXT_NODE) return escapeHtml(node.textContent ?? '')
  return (node as Element).outerHTML
}

function plainText(html: string): string {
  const holder = document.createElement('div')
  holder.innerHTML = html
  return (holder.textContent ?? '').trim()
}

function slugify(text: string): string {
  return text
    .trim()
    .toLowerCase()
    .replace(/[\s\u3000]+/gu, '-')
    .replace(/[^\p{L}\p{N}-]+/gu, '')
    .replace(/-{2,}/g, '-')
    .replace(/^-|-$/g, '')
    .slice(0, 64)
}

/**
 * Headings get a `md-` prefixed id plus a hover anchor. The prefix keeps a
 * heading called "app" from colliding with the application root element, and the
 * click is intercepted in this component because the app routes on the URL hash.
 */
function applyHeadingAnchors(fragment: DocumentFragment, label: string): void {
  const taken = new Map<string, number>()
  let index = 0
  fragment.querySelectorAll('h1, h2, h3, h4, h5, h6').forEach(heading => {
    index += 1
    const base = slugify(heading.textContent ?? '') || `section-${index}`
    const seen = taken.get(base) ?? 0
    taken.set(base, seen + 1)
    const id = `md-${base}${seen === 0 ? '' : `-${seen + 1}`}`
    heading.id = id
    const anchor = document.createElement('a')
    anchor.className = 'markdown-anchor'
    anchor.setAttribute('href', `#${id}`)
    anchor.setAttribute('aria-label', label)
    anchor.setAttribute('title', label)
    anchor.textContent = '#'
    heading.appendChild(anchor)
  })
}

/** Footnote labels come from the plugin in English; the section and backrefs are localised here. */
function localizeFootnotes(fragment: DocumentFragment): void {
  const section = fragment.querySelector('.footnotes')
  if (!section) return
  const heading = section.querySelector('h2, .sr-only')
  if (heading) heading.textContent = t('chat.markdownFootnotes')
  section.querySelectorAll('a[data-footnote-backref]').forEach(anchor => {
    const index = anchor.closest('li')?.id.match(/(\d+)$/)?.[1] ?? '1'
    anchor.setAttribute('aria-label', t('chat.markdownBackReference', { n: index }))
  })
}

function fenceLanguage(code: Element): string | null {
  const match = /(?:^|\s)language-([^\s]+)/i.exec(code.className)
  return match?.[1] ? match[1].toLowerCase() : null
}

function codeBlock(pre: Element): MarkdownBlock | null {
  const code = pre.querySelector('code')
  if (!code) return null
  const language = fenceLanguage(code)
  const text = code.textContent ?? ''
  if (language === 'mermaid') return { kind: 'diagram', code: text }
  code.classList.add('hljs')
  code.innerHTML = highlightCode(text, language)
  return { kind: 'code', html: pre.outerHTML, code: text, language }
}

function calloutBlock(quote: Element): MarkdownBlock | null {
  const paragraph = quote.firstElementChild
  if (!paragraph || paragraph.tagName.toLowerCase() !== 'p') return null
  const inner = paragraph.innerHTML
  const lineBreak = inner.search(/<br\s*\/?>|\n/i)
  const firstLine = lineBreak === -1 ? inner : inner.slice(0, lineBreak)
  const marker = CALLOUT_MARKER.exec(firstLine)
  if (!marker) return null
  const callout = marker[1]!.toLowerCase() as CalloutKind
  const customTitle = plainText(marker[2] ?? '')
  const rest = lineBreak === -1 ? '' : inner.slice(lineBreak).replace(/^<br\s*\/?>/i, '')
  if (plainText(rest)) paragraph.innerHTML = rest
  else paragraph.remove()
  quote.classList.add('markdown-callout-body')
  return {
    kind: 'callout',
    callout,
    title: customTitle || t(`chat.callout.${callout}`),
    html: quote.outerHTML,
  }
}

const blocks = computed<MarkdownBlock[]>(() => {
  // Late reference verifications bump this counter; reading it here re-parses the reply
  // exactly once per settled batch instead of once per streaming delta.
  void props.docRefsVersion
  const raw = marked.parse(parsedContent.value, {
    breaks: true,
    gfm: true,
    async: false,
  }) as string
  // Sanitize the complete document first so reference links, footnotes and nested
  // Markdown retain their meaning. Only trusted wrappers are added afterwards.
  const fragment = DOMPurify.sanitize(raw, { RETURN_DOM_FRAGMENT: true })
  applyHeadingAnchors(fragment, t('chat.markdownAnchor'))
  localizeFootnotes(fragment)
  markDocumentRefs(fragment)
  for (const table of fragment.querySelectorAll('table')) {
    const scroll = document.createElement('div')
    scroll.className = 'markdown-table-scroll'
    scroll.tabIndex = 0
    scroll.setAttribute('role', 'region')
    scroll.setAttribute('aria-label', t('chat.markdownTable'))
    table.replaceWith(scroll)
    scroll.appendChild(table)
  }
  const blocks: MarkdownBlock[] = []
  for (const node of Array.from(fragment.childNodes)) {
    if (node.nodeType === Node.COMMENT_NODE) continue
    if (node.nodeType === Node.TEXT_NODE && !node.textContent?.trim()) continue
    const element = node.nodeType === Node.ELEMENT_NODE ? node as Element : null
    const tag = element?.tagName.toLowerCase()
    if (element?.tagName.toLowerCase() === 'pre') {
      blocks.push(codeBlock(element) ?? { kind: 'prose', html: outerHtml(node) })
      continue
    }
    if (tag === 'blockquote') {
      blocks.push(calloutBlock(element!) ?? { kind: 'island', html: outerHtml(node) })
      continue
    }
    const island = Boolean(element?.classList.contains('markdown-table-scroll'))
    blocks.push({ kind: island ? 'island' : 'prose', html: outerHtml(node) })
  }
  return blocks
})

const bodyRef = ref<HTMLElement | null>(null)
let anchorTimer: ReturnType<typeof setTimeout> | null = null

function prefersReducedMotion(): boolean {
  return window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false
}

function flashAnchor(target: Element) {
  target.classList.add('is-anchor-target')
  if (anchorTimer) clearTimeout(anchorTimer)
  anchorTimer = setTimeout(() => target.classList.remove('is-anchor-target'), 900)
}

/**
 * In-page anchors are handled here: this app routes on the URL hash, so letting
 * the browser jump would navigate the router instead of scrolling the reply.
 * Footnote references and heading anchors both arrive through this path.
 */
function onAnchorClick(event: MouseEvent) {
  const anchor = (event.target as HTMLElement | null)?.closest?.('a[href^="#"]') as HTMLAnchorElement | null
  if (!anchor || event.defaultPrevented) return
  // Modified clicks keep their default so the link can be opened or copied.
  if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
  const id = decodeURIComponent((anchor.getAttribute('href') ?? '').slice(1))
  if (!id) return
  const quoted = id.replace(/["\\]/g, '\\$&')
  const target = bodyRef.value?.querySelector(`[id="${quoted}"]`) ?? document.getElementById(id)
  if (!target) return
  event.preventDefault()
  target.scrollIntoView({ block: 'center', behavior: prefersReducedMotion() ? 'auto' : 'smooth' })
  flashAnchor(target)
}

/** The document reference sits inside `v-html`, so its activation is delegated here. */
function documentRefTarget(event: Event): HTMLElement | null {
  const element = (event.target as HTMLElement | null)?.closest?.('[data-doc-path]')
  return element instanceof HTMLElement && element.hasAttribute('data-doc-path') ? element : null
}

function openDocumentRef(element: HTMLElement): void {
  const path = element.getAttribute('data-doc-path') ?? ''
  if (!path) return
  const line = Number(element.getAttribute('data-doc-line') ?? '')
  emit('open-doc', Number.isFinite(line) && line > 0 ? { path, line } : { path })
}

function onBodyClick(event: MouseEvent) {
  const element = documentRefTarget(event)
  if (element) {
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return
    event.preventDefault()
    openDocumentRef(element)
    return
  }
  onAnchorClick(event)
}

/** A reference announces itself as a button, so Enter and Space must work too. */
function onBodyKeydown(event: KeyboardEvent) {
  if (event.key !== 'Enter' && event.key !== ' ') return
  const element = documentRefTarget(event)
  if (!element) return
  event.preventDefault()
  openDocumentRef(element)
}

const copiedIndex = ref<number | null>(null)
const failedIndex = ref<number | null>(null)
let feedbackTimer: ReturnType<typeof setTimeout> | null = null

function resetFeedback() {
  if (feedbackTimer) clearTimeout(feedbackTimer)
  feedbackTimer = setTimeout(() => {
    copiedIndex.value = null
    failedIndex.value = null
  }, 1600)
}

/**
 * Electron loads the built page from disk, and the async clipboard also refuses
 * to write while the window is not focused. Both cases fall back to the legacy
 * path rather than telling the user the copy failed.
 */
async function writeClipboard(text: string): Promise<void> {
  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text)
      return
    } catch { /* fall through to the legacy path */ }
  }
  const area = document.createElement('textarea')
  area.value = text
  area.setAttribute('readonly', '')
  area.style.position = 'fixed'
  area.style.top = '-1000px'
  document.body.appendChild(area)
  try {
    area.select()
    if (!document.execCommand('copy')) throw new Error('copy rejected')
  } finally {
    area.remove()
  }
}

async function copyCode(block: Extract<MarkdownBlock, { kind: 'code' }>, index: number) {
  try {
    await writeClipboard(block.code)
    copiedIndex.value = index
    failedIndex.value = null
  } catch {
    copiedIndex.value = null
    failedIndex.value = index
  }
  resetFeedback()
}

onBeforeUnmount(() => {
  if (feedbackTimer) clearTimeout(feedbackTimer)
  if (anchorTimer) clearTimeout(anchorTimer)
  if (parseTimer !== null) clearTimeout(parseTimer)
})
</script>

<template>
  <div ref="bodyRef" class="markdown-body" @click="onBodyClick" @keydown="onBodyKeydown">
    <template v-for="(block, index) in blocks" :key="index">
      <UiIslandCard v-if="block.kind === 'code'" class="markdown-island markdown-code" padding="none">
        <div class="markdown-code-header">
          <span class="markdown-code-lang">{{ block.language && !UNLABELLED_LANGUAGES.has(block.language) ? block.language : '' }}</span>
          <button
            type="button"
            class="markdown-copy"
            :class="{ 'is-copied': copiedIndex === index, 'is-failed': failedIndex === index }"
            :aria-label="t('chat.markdownCopy')"
            :title="t('chat.markdownCopy')"
            @click="copyCode(block, index)"
          >
            <Check v-if="copiedIndex === index" :size="12" aria-hidden="true" />
            <Copy v-else :size="12" aria-hidden="true" />
            <span>{{ copiedIndex === index ? t('chat.markdownCopied') : failedIndex === index ? t('chat.markdownCopyFailed') : t('chat.markdownCopy') }}</span>
          </button>
        </div>
        <div class="markdown-island-content" v-html="block.html" />
      </UiIslandCard>
      <UiIslandCard
        v-else-if="block.kind === 'callout'"
        class="markdown-island markdown-callout"
        :class="`is-${block.callout}`"
        padding="none"
      >
        <div class="markdown-callout-head">
          <component :is="CALLOUT_ICONS[block.callout]" :size="14" aria-hidden="true" />
          <span class="markdown-callout-title" v-html="block.title" />
        </div>
        <div class="markdown-island-content" v-html="block.html" />
      </UiIslandCard>
      <UiIslandCard v-else-if="block.kind === 'diagram'" class="markdown-island markdown-diagram" padding="none">
        <MarkdownDiagram :code="block.code" />
      </UiIslandCard>
      <UiIslandCard v-else-if="block.kind === 'island'" class="markdown-island" padding="none">
        <div class="markdown-island-content" v-html="block.html" />
      </UiIslandCard>
      <div v-else class="markdown-prose" v-html="block.html" />
    </template>
  </div>
</template>
