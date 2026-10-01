<script setup lang="ts">
import { Search } from '@lucide/vue'
import { useI18n } from 'vue-i18n'
import { computed, nextTick, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { homeController } from '@/controllers/HomeController'
import { closePalette, useCommandPalette } from '@/composables/useCommandPalette'
import {
  availableCommands,
  filterCommands,
  formatGroup,
  haystackOf,
  implicitArgument,
  type AppCommand,
  type CommandHost,
  type RankedCommand,
} from '@/lib/appCommands'

const { t } = useI18n()
const router = useRouter()
const { open, comboLabel } = useCommandPalette()

const query = ref('')
const activeIndex = ref(0)
const dialogRef = ref<HTMLDialogElement | null>(null)
const inputRef = ref<HTMLInputElement | null>(null)
const listId = 'command-palette-list'

/**
 * The palette's half of `CommandHost`. It owns none of the composer's local state, so
 * it reads what the controller holds and does its own navigation - which is the whole
 * reason the command table takes a host instead of calling into a component.
 */
const host: CommandHost = {
  canStop: () => Boolean(homeController.stoppableRunId.value),
  draft: () => homeController.draft.value,
  setDraft: (value) => homeController.updateDraft(value),
  send: (text, dispatch) => {
    homeController.updateDraft(text)
    void homeController.sendMessage({
      dispatch_mode: dispatch,
      target_run_id: null,
      mode_version_id: null,
      meeting_model_override: null,
    })
  },
  stopRun: () => {
    void homeController.stopRun()
  },
  newSession: () => {
    void homeController.createSession(homeController.selectedProjectId.value ?? null)
  },
  navigate: (routeName) => {
    void router.push({ name: routeName })
  },
  routeName: () => String(router.currentRoute.value.name ?? ''),
}

/** Labels resolve here, because only the caller knows the language being read. */
const ranked = computed<RankedCommand[]>(() =>
  availableCommands(host).map((command) => ({
    command,
    haystack: haystackOf(
      command,
      t(command.labelKey),
      (command.keywordKeys ?? []).map((key) => t(key)),
    ),
  })),
)

const visible = computed(() => filterCommands(query.value, ranked.value))

const activeId = computed(() =>
  visible.value.length ? `command-palette-option-${activeIndex.value}` : undefined,
)

// A new result set can be shorter than the cursor, and a row that is no longer rendered
// must not stay highlighted.
watch(visible, (list) => {
  if (activeIndex.value >= list.length) activeIndex.value = Math.max(0, list.length - 1)
})

watch(open, async (isOpen) => {
  const element = dialogRef.value
  if (!element) return
  if (!isOpen) {
    if (element.open) element.close()
    return
  }
  query.value = ''
  activeIndex.value = 0
  element.showModal()
  await nextTick()
  inputRef.value?.focus()
})

// The list scrolls, so the highlighted row has to stay inside it: a cursor that has
// scrolled out of view reads as a keyboard that stopped working.
watch(activeIndex, () => {
  if (!activeId.value) return
  document.getElementById(activeId.value)?.scrollIntoView({ block: 'nearest' })
})

function move(delta: number) {
  const count = visible.value.length
  if (!count) return
  activeIndex.value = (activeIndex.value + delta + count) % count
}

function run(command: AppCommand) {
  const argument = implicitArgument(command, host)
  closePalette()
  // Closed before running: a navigation command replaces the page underneath, and a
  // send clears the draft the row was about to read, so the list is stale either way.
  command.run(host, argument)
}

function onBackdropClick(event: MouseEvent) {
  // A native dialog has no backdrop click handler; the clicks that land on the dialog
  // element itself (not on its content) are the backdrop.
  if (event.target === dialogRef.value) closePalette()
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    move(1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    move(-1)
  } else if (event.key === 'Enter') {
    const command = visible.value[activeIndex.value]
    if (!command) return
    event.preventDefault()
    run(command)
  }
}
</script>

<template>
  <!-- A native <dialog>, like the notification detail dialog: showModal() brings the
       focus trap, the top layer, the Escape handling and the caret's return home.
       Re-implementing any of those here is how a palette becomes a keyboard trap. -->
  <dialog
    ref="dialogRef"
    class="command-palette no-drag"
    :aria-label="t('palette.title')"
    @close="closePalette()"
    @click="onBackdropClick"
    @keydown="onKeydown"
  >
    <div class="command-palette-search">
      <Search :size="14" class="command-palette-search-icon" aria-hidden="true" />
      <input
        ref="inputRef"
        v-model="query"
        class="command-palette-input"
        type="text"
        role="combobox"
        :placeholder="t('palette.placeholder')"
        :aria-label="t('palette.title')"
        aria-expanded="true"
        aria-autocomplete="list"
        :aria-controls="listId"
        :aria-activedescendant="activeId"
        autocapitalize="off"
        autocomplete="off"
        spellcheck="false"
        data-testid="palette-input"
      />
      <kbd class="command-palette-accelerator">{{ comboLabel }}</kbd>
    </div>

    <ul
      v-if="visible.length"
      :id="listId"
      class="command-palette-list"
      role="listbox"
      :aria-label="t('palette.title')"
      data-testid="palette-list"
    >
      <li
        v-for="(command, index) in visible"
        :key="command.id"
        :id="`command-palette-option-${index}`"
        class="command-palette-row"
        :class="{ 'is-active': index === activeIndex }"
        role="option"
        :aria-selected="index === activeIndex"
        :data-testid="`palette-row-${command.id}`"
        @mouseenter="activeIndex = index"
        @click="run(command)"
      >
        <span class="command-palette-label">{{ t(command.labelKey) }}</span>
        <code v-if="command.slash" class="command-palette-syntax">/{{ command.slash }}</code>
        <span class="command-palette-group">{{ t(formatGroup(command.group)) }}</span>
      </li>
    </ul>

    <p v-else class="command-palette-empty" role="status" data-testid="palette-empty">
      {{ t('palette.empty') }}
    </p>

    <div class="command-palette-footer">
      <span>{{ t('palette.hintNavigate') }}</span>
      <span>{{ t('palette.hintRun') }}</span>
      <span>{{ t('palette.hintClose') }}</span>
    </div>
  </dialog>
</template>

<style scoped>
.command-palette {
  width: min(calc(100vw - 32px), 520px);
  margin: auto;
  padding: 0;
  border: 1px solid var(--border-muted);
  border-radius: 10px;
  background: var(--surface-section);
  color: var(--text-primary);
  box-shadow: var(--shadow-panel);
  backdrop-filter: blur(22px) saturate(120%);
  -webkit-backdrop-filter: blur(22px) saturate(120%);
  -webkit-app-region: no-drag;
}

.command-palette::backdrop {
  background: rgb(3 6 10 / 52%);
}

.command-palette-search {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 14px;
  border-bottom: 1px solid var(--border-muted);
}

.command-palette-search-icon {
  color: var(--text-muted);
  flex-shrink: 0;
}

.command-palette-input {
  flex: 1;
  min-width: 0;
  border: none;
  background: transparent;
  color: var(--text-primary);
  font-size: 13px;
  outline: none;
}

.command-palette-input::placeholder {
  color: var(--text-muted);
}

.command-palette-accelerator {
  flex-shrink: 0;
  padding: 2px 6px;
  border: 1px solid var(--border-muted);
  border-radius: 5px;
  background: var(--surface-input);
  color: var(--text-secondary);
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
}

.command-palette-list {
  display: flex;
  flex-direction: column;
  gap: 1px;
  margin: 0;
  padding: 4px;
  list-style: none;
  max-height: 320px;
  overflow-y: auto;
  overscroll-behavior: contain;
}

.command-palette-row {
  display: grid;
  grid-template-columns: 1fr auto auto;
  align-items: center;
  gap: 10px;
  padding: 7px 8px;
  border-radius: 6px;
  cursor: pointer;
}

.command-palette-row.is-active {
  background: var(--bg-hover);
}

.command-palette-label {
  font-size: 12px;
  color: var(--text-primary);
}

.command-palette-syntax {
  font-family: 'Geist Mono', ui-monospace, monospace;
  font-size: 11px;
  color: var(--text-secondary);
}

.command-palette-group {
  font-size: 10px;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}

.command-palette-empty {
  margin: 0;
  padding: 18px 14px;
  color: var(--text-muted);
  font-size: 12px;
}

.command-palette-footer {
  display: flex;
  gap: 14px;
  padding: 8px 14px;
  border-top: 1px solid var(--border-muted);
  color: var(--text-muted);
  font-size: 11px;
}
</style>
