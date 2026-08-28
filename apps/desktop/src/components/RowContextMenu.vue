<script setup lang="ts">
import { nextTick, onBeforeUnmount, ref, watch, type Component } from 'vue'

export interface RowMenuItem {
  key: string
  label: string
  icon?: Component
  danger?: boolean
}

interface Props {
  visible: boolean
  x: number
  y: number
  items: RowMenuItem[]
}

const props = defineProps<Props>()

const emit = defineEmits<{
  select: [key: string]
  close: []
}>()

const menuRef = ref<HTMLElement | null>(null)
const position = ref({ left: 0, top: 0 })

function clamp() {
  const el = menuRef.value
  const width = el?.offsetWidth ?? 180
  const height = el?.offsetHeight ?? 120
  const margin = 8
  position.value = {
    left: Math.max(margin, Math.min(props.x, window.innerWidth - width - margin)),
    top: Math.max(margin, Math.min(props.y, window.innerHeight - height - margin)),
  }
}

function onDocumentMouseDown(event: MouseEvent) {
  if (menuRef.value && !menuRef.value.contains(event.target as Node)) emit('close')
}

function onKeyDown(event: KeyboardEvent) {
  if (event.key === 'Escape') emit('close')
}

watch(() => [props.visible, props.x, props.y], async () => {
  if (!props.visible) return
  position.value = { left: props.x, top: props.y }
  document.addEventListener('mousedown', onDocumentMouseDown)
  document.addEventListener('keydown', onKeyDown)
  await nextTick()
  clamp()
}, { immediate: true })

watch(() => props.visible, (open) => {
  if (open) return
  document.removeEventListener('mousedown', onDocumentMouseDown)
  document.removeEventListener('keydown', onKeyDown)
})

onBeforeUnmount(() => {
  document.removeEventListener('mousedown', onDocumentMouseDown)
  document.removeEventListener('keydown', onKeyDown)
})

function choose(key: string) {
  emit('select', key)
  emit('close')
}
</script>

<template>
  <Teleport to="body">
    <div
      v-if="visible"
      ref="menuRef"
      class="row-context-menu"
      :style="{ left: `${position.left}px`, top: `${position.top}px` }"
      role="menu"
      @contextmenu.prevent
    >
      <button
        v-for="item in items"
        :key="item.key"
        type="button"
        role="menuitem"
        class="row-context-menu-item"
        :class="{ danger: item.danger }"
        @click="choose(item.key)"
      >
        <component :is="item.icon" v-if="item.icon" :size="14" class="row-context-menu-icon" />
        <span>{{ item.label }}</span>
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.row-context-menu {
  position: fixed;
  z-index: 1000;
  min-width: 10rem;
  padding: 4px;
  border-radius: 8px;
  border: 1px solid var(--border-secondary, rgba(128, 128, 128, 0.2));
  background: var(--surface-raised, rgba(30, 32, 38, 0.96));
  color: var(--text-primary, #e6e8ee);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.28);
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.row-context-menu-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 6px 8px;
  border: none;
  border-radius: 6px;
  background: transparent;
  color: inherit;
  font-size: 12px;
  line-height: 1.4;
  text-align: left;
  cursor: pointer;
}

.row-context-menu-item:hover {
  background: var(--surface-hover, rgba(128, 132, 145, 0.18));
}

.row-context-menu-item.danger {
  color: var(--status-error, #f0616d);
}

.row-context-menu-icon {
  flex: none;
}
</style>
