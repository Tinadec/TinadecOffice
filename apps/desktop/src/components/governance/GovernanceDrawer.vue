<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { X } from '@lucide/vue'
import GovernanceBoardContent, { type GovernanceBoardTab } from './GovernanceBoardContent.vue'

const props = withDefaults(defineProps<{
  open?: boolean
  initialTab?: GovernanceBoardTab
}>(), {
  open: undefined,
  initialTab: 'decisions',
})

const emit = defineEmits<{
  'update:open': [value: boolean]
  close: []
}>()

const { t } = useI18n()
const internalOpen = ref(false)
const activeTab = ref<GovernanceBoardTab>(props.initialTab)

const isOpen = computed(() => (props.open !== undefined ? props.open : internalOpen.value))

watch(() => props.initialTab, (tab) => {
  activeTab.value = tab
})

function setOpen(next: boolean, tab?: GovernanceBoardTab) {
  if (tab) activeTab.value = tab
  internalOpen.value = next
  emit('update:open', next)
  if (!next) emit('close')
}

function onOpenDrawerEvent(event: Event) {
  const custom = event as CustomEvent<{ tab?: GovernanceBoardTab }>
  const nextTab = custom.detail?.tab === 'runtime' ? 'runtime' : 'decisions'
  setOpen(true, nextTab)
}

function onKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && isOpen.value) {
    event.preventDefault()
    setOpen(false)
  }
}

onMounted(() => {
  window.addEventListener('tinadec:open-governance-drawer', onOpenDrawerEvent)
  window.addEventListener('keydown', onKeydown)
})

onBeforeUnmount(() => {
  window.removeEventListener('tinadec:open-governance-drawer', onOpenDrawerEvent)
  window.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <Teleport to="body">
    <div
      v-if="isOpen"
      class="governance-drawer-backdrop"
      data-testid="governance-drawer-backdrop"
      @click.self="setOpen(false)"
    >
      <aside
        class="governance-drawer"
        role="dialog"
        aria-modal="true"
        :aria-label="t('governance.boardTitle')"
        data-testid="governance-drawer"
      >
        <header class="governance-drawer__head">
          <h2>{{ t('governance.boardTitle') }}</h2>
          <button
            type="button"
            class="governance-drawer__close"
            :title="t('common.close')"
            :aria-label="t('common.close')"
            data-testid="governance-drawer-close"
            @click="setOpen(false)"
          >
            <X :size="16" aria-hidden="true" />
          </button>
        </header>
        <div class="governance-drawer__body">
          <GovernanceBoardContent :initial-tab="activeTab" />
        </div>
      </aside>
    </div>
  </Teleport>
</template>

<style scoped>
.governance-drawer-backdrop {
  position: fixed;
  inset: 0;
  z-index: 60;
  display: flex;
  justify-content: flex-end;
  background: rgba(0, 0, 0, 0.36);
  backdrop-filter: blur(2px);
}

.governance-drawer {
  display: flex;
  flex-direction: column;
  width: min(920px, 94vw);
  height: 100%;
  background: var(--surface-panel, var(--surface-section));
  border-left: 1px solid var(--border-muted);
  box-shadow: var(--shadow-panel);
  overflow: hidden;
}

.governance-drawer__head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 14px 18px;
  border-bottom: 1px solid var(--border-muted);
}

.governance-drawer__head h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 700;
  color: var(--text-primary);
}

.governance-drawer__close {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
}

.governance-drawer__close:hover {
  background: var(--bg-hover);
  color: var(--text-primary);
}

.governance-drawer__body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 16px 18px 24px;
}
</style>
