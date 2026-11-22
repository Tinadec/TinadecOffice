<script setup lang="ts">
import { cn } from '@/lib/utils'
import { onBeforeUnmount, ref, watch } from 'vue'
import { UiTransition } from './ui-transition'

interface Props {
  open?: boolean
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:open': [value: boolean]
}>()

const isOpen = ref(Boolean(props.open))
const rootRef = ref<HTMLElement | null>(null)
const triggerRef = ref<HTMLElement | null>(null)

watch(() => props.open, (val) => {
  isOpen.value = Boolean(val)
})

function close(restoreFocus = false) {
  if (!isOpen.value) return
  isOpen.value = false
  emit('update:open', false)
  if (restoreFocus) {
    const focusable = triggerRef.value?.querySelector<HTMLElement>('button, [tabindex]:not([tabindex="-1"]), a[href]')
    focusable?.focus()
  }
}

function toggle() {
  isOpen.value = !isOpen.value
  emit('update:open', isOpen.value)
}

function onPointerDownOutside(event: PointerEvent) {
  const target = event.target as Node | null
  if (!target || rootRef.value?.contains(target)) return
  close()
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || event.defaultPrevented) return
  event.preventDefault()
  close(true)
}

watch(
  isOpen,
  (open) => {
    if (open) {
      window.addEventListener('pointerdown', onPointerDownOutside, true)
      window.addEventListener('keydown', onKeydown)
    } else {
      window.removeEventListener('pointerdown', onPointerDownOutside, true)
      window.removeEventListener('keydown', onKeydown)
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', onPointerDownOutside, true)
  window.removeEventListener('keydown', onKeydown)
})
</script>

<template>
  <div ref="rootRef" class="relative">
    <div ref="triggerRef" @click="toggle">
      <slot name="trigger" :open="isOpen" />
    </div>
    <UiTransition name="ui-pop">
      <div
        v-if="isOpen"
        data-slot="popover-content"
        :data-state="isOpen ? 'open' : 'closed'"
        :class="cn(
          'absolute z-50 mt-1.5 w-72 rounded-[var(--radius-control-md,8px)] border border-[var(--border-default)] bg-[var(--surface-raised)] p-4 text-[var(--text-primary)] shadow-[var(--shadow-panel)] outline-none',
          props.class,
        )"
      >
        <slot :close="close" />
      </div>
    </UiTransition>
  </div>
</template>

<style scoped>
.ui-pop-enter-active {
  transition: opacity 160ms var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1)),
              transform 160ms var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1));
  transform-origin: top center;
}

.ui-pop-leave-active {
  transition: opacity 110ms var(--ease-exit, cubic-bezier(0.4, 0, 1, 1)),
              transform 110ms var(--ease-exit, cubic-bezier(0.4, 0, 1, 1));
  transform-origin: top center;
}

.ui-pop-enter-from,
.ui-pop-leave-to {
  opacity: 0;
  transform: translateY(-3px) scale(0.96);
}

@media (prefers-reduced-motion: reduce) {
  .ui-pop-enter-active,
  .ui-pop-leave-active {
    transition: none;
  }

  .ui-pop-enter-from,
  .ui-pop-leave-to {
    transform: none;
  }
}
</style>
