<script setup lang="ts">
/**
 * TerminalPanel — Multi-terminal management panel.
 *
 * Features (VS Code / JetBrains style):
 * - Tab bar for multiple terminal instances
 * - New terminal button with shell profile selector dropdown
 * - Close terminal tabs
 * - Switch between terminals
 * - Restart exited terminals
 * - Keyboard shortcuts: Ctrl+Shift+T (new), Ctrl+W (close), Ctrl+Tab (next)
 * - Auto-fit terminal to panel size
 * - Status indicators (running / exited)
 */
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Plus,
  TerminalSquare,
  X,
  RotateCcw,
  ChevronDown,
  Circle,
  Square,
  type LucideIcon,
} from '@lucide/vue'
import TerminalView from './TerminalView.vue'
import { useTerminal, type TerminalInstance } from '@/composables/useTerminal'
import { useNotifications } from '@/composables/useNotifications'

const { t } = useI18n()
const { notify } = useNotifications()

const props = defineProps<{
  /** Working directory for new terminals */
  cwd?: string
  /** Whether the panel is visible (for auto-fit on show) */
  visible?: boolean
}>()

const {
  terminals,
  activeTerminalId,
  availableShells,
  shellsLoaded,
  loadShells,
  createTerminal,
  closeTerminal,
  setActiveTerminal,
  fitTerminal,
  focusTerminal,
  fitAllTerminals,
  isTerminalAvailable,
} = useTerminal()

// ---- Shell selector dropdown ----
const showShellMenu = ref(false)
const shellMenuRef = ref<HTMLElement | null>(null)

// ---- Terminal view refs for focus management ----
const terminalViewRefs = ref<Record<string, InstanceType<typeof TerminalView>> | null>({})

// ---- Computed ----

const activeTerminal = computed<TerminalInstance | null>(() =>
  terminals.value.find((t) => t.id === activeTerminalId.value) ?? null,
)

const hasTerminals = computed(() => terminals.value.length > 0)
const terminalAvailable = computed(() => isTerminalAvailable())

// ---- Actions ----

/**
 * Create a new terminal with the given shell profile.
 */
async function handleNewTerminal(shellId?: string): Promise<void> {
  showShellMenu.value = false
  
  try {
    const result = await createTerminal({
      shellId: shellId || (availableShells.value[0]?.id ?? 'default'),
      cwd: props.cwd,
      title: availableShells.value.find((s) => s.id === shellId)?.label ?? 'Terminal',
    })
    
    if (!result) {
      notify.error(t('terminal.createFailed', 'Failed to create terminal'))
      return
    }
    
    // Fit after creation
    await nextTick()
    if (activeTerminalId.value) {
      fitTerminal(activeTerminalId.value)
      setTimeout(() => focusTerminal(activeTerminalId.value!), 100)
    }
  } catch (err) {
    notify.error(err, { title: t('terminal.createFailed', 'Failed to create terminal') })
  }
}

/**
 * Restart a terminal that has exited.
 */
async function handleRestart(id: string): Promise<void> {
  const oldInstance = terminals.value.find((t) => t.id === id)
  const shellId = oldInstance?.shellId
  const cwd = oldInstance?.cwd || props.cwd
  try {
    closeTerminal(id)
    await nextTick()
    const result = await createTerminal({
      shellId: shellId || 'default',
      cwd,
      title: oldInstance?.title ?? 'Terminal',
    })
    if (!result) {
      notify.error(t('terminal.createFailed', 'Failed to create terminal'))
      return
    }
    await nextTick()
    if (activeTerminalId.value) {
      fitTerminal(activeTerminalId.value)
    }
  } catch (err) {
    notify.error(err, { title: t('terminal.createFailed', 'Failed to create terminal') })
  }
}

/**
 * Close a terminal tab.
 */
function handleCloseTerminal(id: string, event?: MouseEvent): void {
  event?.stopPropagation()
  closeTerminal(id)
}

/**
 * Switch to a terminal tab.
 */
function handleSelectTerminal(id: string): void {
  setActiveTerminal(id)
  nextTick(() => {
    fitTerminal(id)
    setTimeout(() => focusTerminal(id), 50)
  })
}

/**
 * Toggle the shell selector menu.
 */
function toggleShellMenu(event: MouseEvent): void {
  event.stopPropagation()
  showShellMenu.value = !showShellMenu.value
}

/**
 * Close shell menu when clicking outside.
 */
function handleDocumentClick(event: MouseEvent): void {
  if (shellMenuRef.value && !shellMenuRef.value.contains(event.target as Node)) {
    showShellMenu.value = false
  }
}

// ---- Keyboard shortcuts ----

function handleKeydown(event: KeyboardEvent): void {
  // Ctrl+Shift+T: New terminal (default shell)
  if (event.ctrlKey && event.shiftKey && event.key === 'T') {
    event.preventDefault()
    void handleNewTerminal()
    return
  }

  // Ctrl+W: Close active terminal
  if (event.ctrlKey && !event.shiftKey && (event.key === 'w' || event.key === 'W')) {
    if (activeTerminalId.value) {
      event.preventDefault()
      closeTerminal(activeTerminalId.value)
    }
    return
  }

  // Ctrl+Tab: Next terminal
  if (event.ctrlKey && event.key === 'Tab') {
    if (terminals.value.length > 1) {
      event.preventDefault()
      const idx = terminals.value.findIndex((t) => t.id === activeTerminalId.value)
      const nextIdx = event.shiftKey
        ? (idx - 1 + terminals.value.length) % terminals.value.length
        : (idx + 1) % terminals.value.length
      handleSelectTerminal(terminals.value[nextIdx].id)
    }
    return
  }

  // Ctrl+` (backtick): Focus terminal (if any)
  if (event.ctrlKey && event.key === '`') {
    if (activeTerminalId.value) {
      event.preventDefault()
      focusTerminal(activeTerminalId.value)
    }
    return
  }
}

// ---- Watchers ----

// Auto-fit when panel becomes visible
watch(() => props.visible, (visible) => {
  if (visible) {
    const activeId = activeTerminalId.value
    nextTick(() => {
      fitAllTerminals()
      if (activeId) {
        setTimeout(() => focusTerminal(activeId), 50)
      }
    })
  }
})

// Auto-fit when active terminal changes
watch(activeTerminalId, (id) => {
  if (id) {
    nextTick(() => {
      fitTerminal(id)
      setTimeout(() => focusTerminal(id), 50)
    })
  }
})

// ---- Lifecycle ----

onMounted(() => {
  void loadShells()
  document.addEventListener('click', handleDocumentClick)
  document.addEventListener('keydown', handleKeydown)

  // Auto-create a terminal if none exist
  if (terminalAvailable.value && terminals.value.length === 0) {
    void handleNewTerminal()
  }
})

onUnmounted(() => {
  document.removeEventListener('click', handleDocumentClick)
  document.removeEventListener('keydown', handleKeydown)
})

// Set terminal view ref
function setTerminalViewRef(id: string, el: InstanceType<typeof TerminalView> | null): void {
  if (!terminalViewRefs.value) terminalViewRefs.value = {}
  if (el) {
    terminalViewRefs.value[id] = el
  } else {
    delete terminalViewRefs.value[id]
  }
}
</script>

<template>
  <div class="terminal-panel">
    <!-- Terminal not available (e.g. running in browser) -->
    <div v-if="!terminalAvailable" class="terminal-unavailable">
      <TerminalSquare :size="32" />
      <p>{{ t('terminal.notAvailable') }}</p>
      <p class="terminal-unavailable-hint">{{ t('terminal.notAvailableHint') }}</p>
    </div>

    <template v-else>
      <!-- Tab bar -->
      <div class="terminal-tab-bar">
        <div class="terminal-tabs-scroll">
          <div
            v-for="instance in terminals"
            :key="instance.id"
            class="terminal-tab"
            :class="{
              active: activeTerminalId === instance.id,
              exited: instance.exited,
            }"
          >
            <button class="terminal-tab-select" :title="instance.title" @click="handleSelectTerminal(instance.id)">
              <component
                :is="instance.exited ? Square : Circle"
                :size="8"
                class="terminal-tab-status"
                :class="instance.exited ? 'status-exited' : 'status-running'"
              />
              <span class="terminal-tab-label">{{ instance.title }}</span>
            </button>
            <button
              class="terminal-tab-restart"
              :title="t('terminal.restart')"
              @click.stop="handleRestart(instance.id)"
            >
              <RotateCcw :size="11" />
            </button>
            <button
              class="terminal-tab-close"
              :title="t('terminal.close')"
              @click.stop="handleCloseTerminal(instance.id, $event)"
            >
              <X :size="11" />
            </button>
          </div>
        </div>

        <!-- New terminal button with shell selector -->
        <div ref="shellMenuRef" class="terminal-new-wrapper">
          <button
            class="terminal-new-btn"
            :title="t('terminal.newTerminal')"
            @click="toggleShellMenu"
          >
            <Plus :size="14" />
            <ChevronDown :size="10" />
          </button>

          <!-- Shell selector dropdown -->
          <div v-if="showShellMenu" class="terminal-shell-menu">
            <button
              class="terminal-shell-option"
              @click="handleNewTerminal()"
            >
              <TerminalSquare :size="14" />
              <span>{{ t('terminal.defaultShell') }}</span>
            </button>
            <div v-if="availableShells.length > 0" class="terminal-shell-divider" />
            <button
              v-for="shell in availableShells"
              :key="shell.id"
              class="terminal-shell-option"
              @click="handleNewTerminal(shell.id)"
            >
              <TerminalSquare :size="14" />
              <span>{{ shell.label }}</span>
            </button>
          </div>
        </div>
      </div>

      <!-- Terminal content area -->
      <div class="terminal-content">
        <!-- Empty state -->
        <div v-if="!hasTerminals" class="terminal-empty">
          <TerminalSquare :size="32" />
          <p>{{ t('terminal.emptyHint') }}</p>
          <button class="terminal-empty-btn" @click="handleNewTerminal()">
            <Plus :size="14" />
            <span>{{ t('terminal.newTerminal') }}</span>
          </button>
        </div>

        <!-- Terminal views (only render active one for performance, but keep others alive) -->
        <div
          v-for="instance in terminals"
          :key="instance.id"
          v-show="activeTerminalId === instance.id"
          class="terminal-instance"
        >
          <TerminalView
            :ref="(el) => setTerminalViewRef(instance.id, el as InstanceType<typeof TerminalView> | null)"
            :terminal-id="instance.id"
            @exited="() => {}"
          />
        </div>
      </div>

      <!-- Status bar -->
      <div v-if="hasTerminals && activeTerminal" class="terminal-status-bar">
        <span class="terminal-status-info">
          {{ activeTerminal.title }}
          <span v-if="activeTerminal.exited" class="terminal-status-exited">
            ({{ t('terminal.exited') }})
          </span>
        </span>
        <span class="terminal-status-hint">{{ t('terminal.shortcutHint') }}</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.terminal-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  background: var(--bg-primary);
  overflow: hidden;
}

/* ---- Tab bar ---- */
.terminal-tab-bar {
  display: flex;
  align-items: center;
  gap: 0;
  padding: 0 4px;
  height: 34px;
  min-height: 34px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border-default);
  flex-shrink: 0;
}

.terminal-tabs-scroll {
  display: flex;
  align-items: center;
  gap: 1px;
  flex: 1;
  overflow-x: auto;
  overflow-y: hidden;
  height: 100%;
  scrollbar-width: none;
}

.terminal-tabs-scroll::-webkit-scrollbar {
  display: none;
}

.terminal-tab {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 0 8px;
  height: 28px;
  font-size: 12px;
  color: var(--text-secondary);
  background: transparent;
  border: 0;
  border-radius: 5px 5px 0 0;
  cursor: pointer;
  white-space: nowrap;
  position: relative;
  transition: background 0.12s ease, color 0.12s ease;
  margin-top: 3px;
}

.terminal-tab:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.terminal-tab.active {
  color: var(--text-primary);
  background: var(--bg-primary);
  border-bottom: 2px solid var(--accent-primary);
}

.terminal-tab.exited {
  opacity: 0.6;
}

.terminal-tab-select {
  min-width: 0;
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 0;
  border: 0;
  background: transparent;
  color: inherit;
  font: inherit;
  cursor: pointer;
}

.terminal-tab-status {
  flex-shrink: 0;
}

.terminal-tab-status.status-running {
  color: #3fb950;
  fill: #3fb950;
}

.terminal-tab-status.status-exited {
  color: var(--text-muted);
  fill: var(--text-muted);
}

.terminal-tab-label {
  max-width: 100px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.terminal-tab-restart,
.terminal-tab-close {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border: 0;
  border-radius: 3px;
  color: var(--text-muted);
  background: transparent;
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.12s ease, background 0.12s ease, color 0.12s ease;
}

.terminal-tab:hover .terminal-tab-restart,
.terminal-tab:hover .terminal-tab-close {
  opacity: 1;
}

.terminal-tab-restart:hover {
  background: var(--bg-tertiary);
  color: var(--accent-primary);
}

.terminal-tab-close:hover {
  background: var(--bg-error);
  color: var(--text-error);
}

/* ---- New terminal button ---- */
.terminal-new-wrapper {
  position: relative;
  display: flex;
  align-items: center;
  flex-shrink: 0;
  padding: 0 4px;
}

.terminal-new-btn {
  display: flex;
  align-items: center;
  gap: 3px;
  height: 26px;
  padding: 0 6px;
  background: transparent;
  border: 0;
  border-radius: 5px;
  color: var(--text-secondary);
  cursor: pointer;
  transition: background 0.12s ease, color 0.12s ease;
}

.terminal-new-btn:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

/* ---- Shell selector dropdown ---- */
.terminal-shell-menu {
  position: absolute;
  top: 100%;
  right: 4px;
  margin-top: 4px;
  min-width: 180px;
  background: var(--bg-overlay);
  border: 1px solid var(--border-default);
  border-radius: 8px;
  box-shadow: var(--shadow-panel);
  z-index: 100;
  padding: 4px;
  animation: terminal-menu-in 0.12s ease;
}

@keyframes terminal-menu-in {
  from {
    opacity: 0;
    transform: translateY(-4px);
  }
  to {
    opacity: 1;
    transform: translateY(0);
  }
}

.terminal-shell-option {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 7px 10px;
  font-size: 12px;
  color: var(--text-primary);
  background: transparent;
  border: 0;
  border-radius: 5px;
  cursor: pointer;
  text-align: left;
  transition: background 0.1s ease;
}

.terminal-shell-option:hover {
  background: var(--bg-hover);
}

.terminal-shell-divider {
  height: 1px;
  background: var(--border-muted);
  margin: 4px 0;
}

/* ---- Content area ---- */
.terminal-content {
  flex: 1;
  min-height: 0;
  position: relative;
  overflow: hidden;
}

.terminal-instance {
  width: 100%;
  height: 100%;
  position: absolute;
  top: 0;
  left: 0;
}

/* ---- Empty state ---- */
.terminal-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  height: 100%;
  color: var(--text-muted);
}

.terminal-empty p {
  font-size: 13px;
  margin: 0;
}

.terminal-empty-btn {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 14px;
  font-size: 12px;
  color: var(--text-primary);
  background: var(--bg-button);
  border: 1px solid var(--border-default);
  border-radius: 7px;
  cursor: pointer;
  transition: background 0.12s ease;
}

.terminal-empty-btn:hover {
  background: var(--bg-button-hover);
}

/* ---- Unavailable state ---- */
.terminal-unavailable {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  height: 100%;
  color: var(--text-muted);
  padding: 20px;
  text-align: center;
}

.terminal-unavailable p {
  font-size: 13px;
  margin: 0;
}

.terminal-unavailable-hint {
  font-size: 11px;
  color: var(--text-muted);
  max-width: 280px;
  line-height: 1.5;
}

/* ---- Status bar ---- */
.terminal-status-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 2px 8px;
  height: 22px;
  min-height: 22px;
  background: var(--bg-secondary);
  border-top: 1px solid var(--border-default);
  font-size: 10.5px;
  color: var(--text-muted);
  flex-shrink: 0;
}

.terminal-status-exited {
  color: var(--text-error);
}

.terminal-status-hint {
  opacity: 0.7;
}
</style>
