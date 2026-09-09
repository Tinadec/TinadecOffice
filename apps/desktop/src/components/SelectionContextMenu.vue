<script setup lang="ts">
/**
 * SelectionContextMenu — the in-app right-click menu for selected text.
 *
 * Mounted once by App.vue (like NotificationIslandHost) so every window gets it
 * for free. It listens on `document` in the bubble phase and bails out when an
 * element handler already consumed the event, so the three live right-click
 * owners keep working unchanged (all verified in a running window):
 *   - AppSidebar project/session rows → RowContextMenu (重命名/归档/删除);
 *     reached from the `nav` card in the TinadecUI registry, not a route page.
 *   - FileTreePanel rows → inline path menu.
 *   - BrowserTabBar feature tabs → detach (Home tab returns early).
 *
 * Action availability comes from resolveSelectionContext/buildSelectionMenuActions
 * (lib/selectionContext.ts). Terminal copy/select-all go through xterm's own API
 * because it paints to a canvas and never populates the DOM selection.
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  ArrowUpRight, Check, ClipboardPaste, Copy, Eraser, Link2, Scissors, TextSelect,
} from '@lucide/vue'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useTerminal } from '@/composables/useTerminal'
import { useNotifications } from '@/composables/useNotifications'
import { detectLink, resolveSelectionContext, buildSelectionMenuActions, type SelectionMenuAction } from '@/lib/selectionContext'

const { t } = useI18n()
const { getPanelStyle, getPanelDataAttributes } = usePanelStyles()
const { terminals } = useTerminal()
const { notify } = useNotifications()

const open = ref(false)
const x = ref(0)
const y = ref(0)
const link = ref<string | null>(null)
const actions = ref<SelectionMenuAction[]>([])
const activeIndex = ref(-1)
const menuRef = ref<HTMLElement | null>(null)
const position = ref({ left: 0, top: 0 })
const origin = ref({ x: 0, y: 0 })
/** Focused element to restore when the menu closes. */
let restoreFocus: HTMLElement | null = null
/** xterm terminal under the cursor, when the menu was opened over a terminal. */
let terminalTerm: ReturnType<typeof useTerminal>['terminals']['value'][number]['term'] = null

const panelStyle = computed(() => getPanelStyle())
const panelAttrs = computed(() => getPanelDataAttributes())

const ICONS = {
  copy: Copy,
  cut: Scissors,
  paste: ClipboardPaste,
  selectAll: TextSelect,
  copyLink: Link2,
  openLink: ArrowUpRight,
  clearTerminal: Eraser,
} as const

const LABEL_KEYS: Record<SelectionMenuAction['key'], string> = {
  copy: 'contextMenu.copy',
  cut: 'contextMenu.cut',
  paste: 'contextMenu.paste',
  selectAll: 'contextMenu.selectAll',
  copyLink: 'contextMenu.copyLink',
  openLink: 'contextMenu.openLink',
  clearTerminal: 'contextMenu.clearTerminal',
}

const isMac = computed(() => /mac|iphone|ipad/i.test(navigator.platform || navigator.userAgent))
const mod = computed(() => (isMac.value ? '⌘' : 'Ctrl+'))
const SHORTCUTS: Partial<Record<SelectionMenuAction['key'], string>> = {
  copy: `${mod.value}C`,
  cut: `${mod.value}X`,
  paste: `${mod.value}V`,
  selectAll: `${mod.value}A`,
}

function shortcutFor(key: SelectionMenuAction['key']): string {
  return SHORTCUTS[key] ?? ''
}

function close() {
  if (!open.value) return
  open.value = false
  activeIndex.value = -1
  restoreFocus?.focus?.()
  restoreFocus = null
  terminalTerm = null
  detachDocumentListeners()
}

function clampToViewport() {
  const el = menuRef.value
  const width = el?.offsetWidth ?? 220
  const height = el?.offsetHeight ?? 200
  const margin = 8
  const left = Math.max(margin, Math.min(x.value, window.innerWidth - width - margin))
  const top = Math.max(margin, Math.min(y.value, window.innerHeight - height - margin))
  position.value = { left, top }
  // Grow from the click point, not from the menu centre (Emil: origin-aware popovers).
  origin.value = { x: x.value - left, y: y.value - top }
}

async function openAt(event: MouseEvent) {
  // An element-level handler that already called preventDefault owns this
  // right-click; do not double-open (AppSidebar rows, FileTreePanel rows,
  // BrowserTabBar feature tabs).
  if (event.defaultPrevented) return

  const target = event.target as Element | null
  const context = resolveSelectionContext(target, window.getSelection())
  const menuActions = buildSelectionMenuActions(context)

  // Chrome with nothing selected keeps its old right-click behaviour (inert),
  // so this menu never appears as pure noise.
  if (context.surface === 'chrome' && !context.hasSelection) return
  if (menuActions.length === 0) return

  const term = target?.closest('.xterm')
    ? terminals.value.find((entry) => entry.term?.element?.contains(target as Node))?.term ?? null
    : null

  event.preventDefault()

  restoreFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null
  terminalTerm = term
  link.value = context.link
  actions.value = menuActions
  activeIndex.value = -1
  x.value = event.clientX
  y.value = event.clientY
  open.value = true
  position.value = { left: event.clientX, top: event.clientY }
  attachDocumentListeners()
  await nextTick()
  clampToViewport()
  menuRef.value?.querySelector<HTMLElement>('[role="menuitem"]')?.focus()
}

// ---- Document-level dismissal ----
function onDocumentMouseDown(event: MouseEvent) {
  if (menuRef.value && !menuRef.value.contains(event.target as Node)) close()
}
function onDocumentKeyDown(event: KeyboardEvent) {
  if (!open.value) return
  const enabled = actions.value
  if (event.key === 'Escape') { event.preventDefault(); close(); return }
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    const step = event.key === 'ArrowDown' ? 1 : -1
    activeIndex.value = (activeIndex.value + step + enabled.length) % enabled.length
    focusActive()
    return
  }
  if (event.key === 'Home' || event.key === 'End') {
    event.preventDefault()
    activeIndex.value = event.key === 'Home' ? 0 : enabled.length - 1
    focusActive()
    return
  }
  if (event.key === 'Enter' || event.key === ' ') {
    const action = enabled[activeIndex.value]
    if (action) { event.preventDefault(); void invoke(action.key) }
  }
}

function focusActive() {
  const items = menuRef.value?.querySelectorAll<HTMLElement>('[role="menuitem"]')
  items?.[activeIndex.value]?.focus()
}

function onDocumentScroll() { close() }
function onWindowBlur() { close() }

function attachDocumentListeners() {
  document.addEventListener('mousedown', onDocumentMouseDown, true)
  document.addEventListener('keydown', onDocumentKeyDown)
  document.addEventListener('scroll', onDocumentScroll, true)
  window.addEventListener('blur', onWindowBlur)
  window.addEventListener('resize', close)
}
function detachDocumentListeners() {
  document.removeEventListener('mousedown', onDocumentMouseDown, true)
  document.removeEventListener('keydown', onDocumentKeyDown)
  document.removeEventListener('scroll', onDocumentScroll, true)
  window.removeEventListener('blur', onWindowBlur)
  window.removeEventListener('resize', close)
}

// ---- Actions ----
async function readClipboard(): Promise<string> {
  const bridge = window.tinadec as { clipboardReadText?: () => Promise<string> } | undefined
  if (bridge?.clipboardReadText) return (await bridge.clipboardReadText()) ?? ''
  return await navigator.clipboard.readText()
}

async function writeClipboard(text: string): Promise<void> {
  const bridge = window.tinadec as { clipboardWriteText?: (value: string) => Promise<boolean> } | undefined
  if (bridge?.clipboardWriteText) { await bridge.clipboardWriteText(text); return }
  await navigator.clipboard.writeText(text)
}

/** Insert text at the caret of an editable field, replacing the current selection. */
function insertIntoField(text: string) {
  const el = document.activeElement
  if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
    const start = el.selectionStart ?? el.value.length
    const end = el.selectionEnd ?? start
    el.setRangeText(text, start, end, 'end')
    el.dispatchEvent(new Event('input', { bubbles: true }))
    return
  }
  // contenteditable: execCommand keeps the browser's own undo stack intact.
  document.execCommand('insertText', false, text)
}

async function invoke(key: SelectionMenuAction['key']) {
  const term = terminalTerm
  close()
  try {
    switch (key) {
      case 'copy': {
        const text = term ? term.getSelection() : window.getSelection()?.toString() ?? ''
        if (text) await writeClipboard(text)
        return
      }
      case 'cut': {
        const el = document.activeElement
        const text = window.getSelection()?.toString() ?? ''
        if (text) await writeClipboard(text)
        if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) {
          const start = el.selectionStart ?? 0
          const end = el.selectionEnd ?? start
          el.setRangeText('', start, end, 'end')
          el.dispatchEvent(new Event('input', { bubbles: true }))
        } else {
          document.execCommand('delete')
        }
        return
      }
      case 'paste': {
        const text = await readClipboard()
        if (!text) return
        if (term) { term.paste(text); return }
        insertIntoField(text)
        return
      }
      case 'selectAll': {
        if (term) { term.selectAll(); return }
        const el = document.activeElement
        if (el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement) { el.select(); return }
        document.execCommand('selectAll')
        return
      }
      case 'copyLink': {
        if (link.value) await writeClipboard(link.value)
        return
      }
      case 'openLink': {
        if (link.value) window.open(link.value, '_blank', 'noopener,noreferrer')
        return
      }
      case 'clearTerminal': {
        term?.clear()
        return
      }
    }
  } catch (error) {
    notify.error(error, { title: t(`contextMenu.${key}`) })
  }
}

// Keep the highlighted row in sync with pointer hover.
function setActive(index: number) { activeIndex.value = index }

onMounted(() => document.addEventListener('contextmenu', openAt))
onBeforeUnmount(() => {
  document.removeEventListener('contextmenu', openAt)
  detachDocumentListeners()
})

watch(open, (value) => { if (!value) detachDocumentListeners() })
</script>

<template>
  <Teleport to="body">
    <div
      v-if="open"
      ref="menuRef"
      class="selection-menu"
      :style="[
        panelStyle,
        {
          left: `${position.left}px`,
          top: `${position.top}px`,
          transformOrigin: `${origin.x}px ${origin.y}px`,
        },
      ]"
      v-bind="panelAttrs"
      role="menu"
      :aria-label="t('contextMenu.label')"
      @contextmenu.prevent
    >
      <template v-for="(action, index) in actions" :key="action.key">
        <div v-if="action.group && index > 0" class="selection-menu__separator" role="separator" />
        <button
          type="button"
          role="menuitem"
          class="selection-menu__item"
          :class="{ 'is-active': activeIndex === index }"
          @mousemove="setActive(index)"
          @click="invoke(action.key)"
        >
          <component :is="ICONS[action.key]" :size="14" class="selection-menu__icon" aria-hidden="true" />
          <span class="selection-menu__label">{{ t(LABEL_KEYS[action.key]) }}</span>
          <span v-if="shortcutFor(action.key)" class="selection-menu__shortcut">{{ shortcutFor(action.key) }}</span>
        </button>
      </template>
      <div class="selection-menu__hint" aria-hidden="true">
        <Check :size="11" />
        <span>{{ t('contextMenu.hint') }}</span>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
/* Material-aware floating surface: consumes the same --surface-* tokens as
   every other popover, so it follows the global panel material. */
.selection-menu {
  position: fixed;
  z-index: 1200;
  min-width: 196px;
  max-width: 320px;
  padding: 4px;
  border: 1px solid var(--border-default);
  border-radius: 8px;
  background: var(--surface-raised);
  box-shadow: var(--shadow-panel);
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.selection-menu__item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: var(--text-primary);
  font-size: 13px;
  line-height: 1.4;
  text-align: left;
  cursor: pointer;
  transition: background 120ms cubic-bezier(0.23, 1, 0.32, 1);
}

.selection-menu__item.is-active,
.selection-menu__item:hover,
.selection-menu__item:focus-visible {
  background: var(--surface-hover);
  outline: none;
}

.selection-menu__item:active {
  transform: scale(0.98);
}

.selection-menu__icon {
  flex: none;
  color: var(--text-secondary);
}

.selection-menu__label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.selection-menu__shortcut {
  flex: none;
  font-family: 'Geist Mono Variable', ui-monospace, monospace;
  font-size: 11px;
  color: var(--text-muted);
}

.selection-menu__separator {
  height: 1px;
  margin: 4px 6px;
  background: var(--border-muted);
}

/* One line of orientation — the menu explains where it acts. */
.selection-menu__hint {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 4px 8px 3px;
  font-size: 11px;
  color: var(--text-muted);
  border-top: 1px solid var(--border-muted);
  margin-top: 3px;
}
</style>
