<script setup lang="ts">
import { cn } from '@/lib/utils'
import { X } from '@lucide/vue'
import { onBeforeUnmount, ref, watch } from 'vue'
import i18n from '@/i18n'
import { UiTransition } from './ui-transition'

interface Props {
  open?: boolean
  side?: 'top' | 'right' | 'bottom' | 'left'
  class?: string
}

const props = withDefaults(defineProps<Props>(), {
  side: 'right',
})

const emit = defineEmits<{
  'update:open': [value: boolean]
}>()

const isOpen = ref(Boolean(props.open))

watch(() => props.open, (val) => {
  isOpen.value = Boolean(val)
})

function close() {
  if (!isOpen.value) return
  isOpen.value = false
  emit('update:open', false)
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || event.defaultPrevented) return
  event.preventDefault()
  close()
}

watch(
  isOpen,
  (open) => {
    if (open) {
      window.addEventListener('keydown', onKeydown)
    } else {
      window.removeEventListener('keydown', onKeydown)
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKeydown)
})

const sideClasses = {
  top: 'inset-x-0 top-0 border-b border-[var(--border-default)]',
  bottom: 'inset-x-0 bottom-0 border-t border-[var(--border-default)]',
  left: 'inset-y-0 left-0 h-full w-3/4 max-w-sm border-r border-[var(--border-default)]',
  right: 'inset-y-0 right-0 h-full w-3/4 max-w-sm border-l border-[var(--border-default)]',
}
</script>

<template>
  <Teleport to="body">
    <UiTransition name="ui-sheet-backdrop">
      <div
        v-if="isOpen"
        data-slot="sheet-backdrop"
        class="fixed inset-0 z-50 bg-black/50 backdrop-blur-[2px]"
        @click="close"
      />
    </UiTransition>
    <UiTransition :name="`ui-sheet-${side}`">
      <div
        v-if="isOpen"
        role="dialog"
        aria-modal="true"
        data-slot="sheet-content"
        :data-side="side"
        :class="cn(
          'fixed z-50 gap-4 bg-[var(--surface-raised)] p-6 text-[var(--text-primary)] shadow-[var(--shadow-elevated)] outline-none',
          sideClasses[side],
          props.class,
        )"
      >
        <slot />
        <button
          type="button"
          data-slot="sheet-close"
          class="absolute right-4 top-4 inline-flex h-7 w-7 items-center justify-center rounded-[var(--radius-control,6px)] text-[var(--text-secondary)] transition-[background-color,color,transform] duration-150 ease-out hover:bg-[var(--surface-hover)] hover:text-[var(--text-primary)] active:scale-95 motion-reduce:transition-none motion-reduce:active:scale-100 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-[var(--focus-ring-color,var(--accent-primary))] disabled:pointer-events-none"
          @click="close"
        >
          <X class="h-4 w-4" />
          <span class="sr-only">{{ i18n.global.t('common.close') }}</span>
        </button>
      </div>
    </UiTransition>
  </Teleport>
</template>

<style scoped>
.ui-sheet-backdrop-enter-active,
.ui-sheet-backdrop-leave-active {
  transition: opacity 180ms var(--ease-snappy, cubic-bezier(0.2, 0, 0, 1));
}

.ui-sheet-backdrop-enter-from,
.ui-sheet-backdrop-leave-to {
  opacity: 0;
}

.ui-sheet-right-enter-active,
.ui-sheet-left-enter-active,
.ui-sheet-top-enter-active,
.ui-sheet-bottom-enter-active {
  transition: transform 240ms var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1)),
              opacity 240ms var(--ease-spring-soft, cubic-bezier(0.16, 1, 0.3, 1));
}

.ui-sheet-right-leave-active,
.ui-sheet-left-leave-active,
.ui-sheet-top-leave-active,
.ui-sheet-bottom-leave-active {
  transition: transform 200ms var(--ease-exit, cubic-bezier(0.4, 0, 1, 1)),
              opacity 180ms var(--ease-exit, cubic-bezier(0.4, 0, 1, 1));
}

.ui-sheet-right-enter-from,
.ui-sheet-right-leave-to {
  opacity: 0;
  transform: translateX(100%);
}

.ui-sheet-left-enter-from,
.ui-sheet-left-leave-to {
  opacity: 0;
  transform: translateX(-100%);
}

.ui-sheet-top-enter-from,
.ui-sheet-top-leave-to {
  opacity: 0;
  transform: translateY(-100%);
}

.ui-sheet-bottom-enter-from,
.ui-sheet-bottom-leave-to {
  opacity: 0;
  transform: translateY(100%);
}

@media (prefers-reduced-motion: reduce) {
  .ui-sheet-backdrop-enter-active,
  .ui-sheet-backdrop-leave-active,
  .ui-sheet-right-enter-active,
  .ui-sheet-right-leave-active,
  .ui-sheet-left-enter-active,
  .ui-sheet-left-leave-active,
  .ui-sheet-top-enter-active,
  .ui-sheet-top-leave-active,
  .ui-sheet-bottom-enter-active,
  .ui-sheet-bottom-leave-active {
    transition: none;
  }

  .ui-sheet-right-enter-from,
  .ui-sheet-right-leave-to,
  .ui-sheet-left-enter-from,
  .ui-sheet-left-leave-to,
  .ui-sheet-top-enter-from,
  .ui-sheet-top-leave-to,
  .ui-sheet-bottom-enter-from,
  .ui-sheet-bottom-leave-to {
    transform: none;
  }
}
</style>
