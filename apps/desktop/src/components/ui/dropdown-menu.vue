<script setup lang="ts">
import { cn } from '@/lib/utils'
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { UiTransition } from './ui-transition'

interface Props {
  open?: boolean
  class?: string
  placement?: 'top' | 'bottom'
}

const props = withDefaults(defineProps<Props>(), {
  placement: 'bottom',
})

const emit = defineEmits<{
  'update:open': [value: boolean]
}>()

const isOpen = ref(Boolean(props.open))
const rootRef = ref<HTMLElement | null>(null)
const triggerRef = ref<HTMLElement | null>(null)
const menuRef = ref<HTMLElement | null>(null)

watch(() => props.open, (val) => {
  isOpen.value = Boolean(val)
})

function getFocusableItems(): HTMLElement[] {
  if (!menuRef.value) return []
  return Array.from(
    menuRef.value.querySelectorAll<HTMLElement>(
      'button:not(:disabled), [role="menuitem"]:not([aria-disabled="true"]), a[href]',
    ),
  )
}

function focusItem(index: number) {
  const items = getFocusableItems()
  if (!items.length) return
  const normalized = ((index % items.length) + items.length) % items.length
  items[normalized]?.focus()
}

function close(restoreFocus = false) {
  if (!isOpen.value) return
  isOpen.value = false
  emit('update:open', false)
  if (restoreFocus) {
    const focusable = triggerRef.value?.querySelector<HTMLElement>('button, [tabindex]:not([tabindex="-1"]), a[href]')
    focusable?.focus()
  }
}

function open(focusTarget?: 'first' | 'last') {
  isOpen.value = true
  emit('update:open', true)
  if (focusTarget) {
    void nextTick(() => {
      const items = getFocusableItems()
      if (!items.length) return
      focusItem(focusTarget === 'last' ? items.length - 1 : 0)
    })
  }
}

function onTriggerKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    if (!isOpen.value) open('first')
    else focusItem(0)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    if (!isOpen.value) open('last')
    else focusItem(-1)
  } else if (event.key === 'Escape' && isOpen.value) {
    event.preventDefault()
    event.stopPropagation()
    close(true)
  }
}

function onMenuKeydown(event: KeyboardEvent) {
  const items = getFocusableItems()
  const activeEl = document.activeElement as HTMLElement | null
  const currentIndex = activeEl ? items.indexOf(activeEl) : -1

  if (event.key === 'ArrowDown') {
    event.preventDefault()
    focusItem(currentIndex < 0 ? 0 : currentIndex + 1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    focusItem(currentIndex < 0 ? items.length - 1 : currentIndex - 1)
  } else if (event.key === 'Home') {
    event.preventDefault()
    focusItem(0)
  } else if (event.key === 'End') {
    event.preventDefault()
    focusItem(items.length - 1)
  } else if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    close(true)
  }
}

function onPointerDownOutside(event: PointerEvent) {
  const target = event.target as Node | null
  if (!target || rootRef.value?.contains(target)) return
  close()
}

function onWindowKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || event.defaultPrevented) return
  event.preventDefault()
  close(true)
}

watch(
  isOpen,
  (openState) => {
    if (openState) {
      window.addEventListener('pointerdown', onPointerDownOutside, true)
      window.addEventListener('keydown', onWindowKeydown)
    } else {
      window.removeEventListener('pointerdown', onPointerDownOutside, true)
      window.removeEventListener('keydown', onWindowKeydown)
    }
  },
  { immediate: true },
)

onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', onPointerDownOutside, true)
  window.removeEventListener('keydown', onWindowKeydown)
})
</script>

<template>
  <div ref="rootRef" class="relative">
    <div ref="triggerRef" @click="isOpen ? close() : open()" @keydown="onTriggerKeydown">
      <slot name="trigger" :open="isOpen" />
    </div>
    <UiTransition name="ui-pop">
      <div
        v-if="isOpen"
        ref="menuRef"
        role="menu"
        data-slot="dropdown-menu-content"
        :data-placement="placement"
        :class="cn(
          'absolute z-50 min-w-[8rem] overflow-hidden rounded-[var(--radius-control-md,8px)] border border-[var(--border-default)] bg-[var(--surface-raised)] p-1 text-[var(--text-primary)] shadow-[var(--shadow-panel)] outline-none',
          placement === 'top' ? 'bottom-full mb-1.5' : 'top-full mt-1.5',
          props.class,
        )"
        @keydown="onMenuKeydown"
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

[data-placement="top"].ui-pop-enter-active,
[data-placement="top"].ui-pop-leave-active {
  transform-origin: bottom center;
}

[data-placement="top"].ui-pop-enter-from,
[data-placement="top"].ui-pop-leave-to {
  transform: translateY(3px) scale(0.96);
}

@media (prefers-reduced-motion: reduce) {
  .ui-pop-enter-active,
  .ui-pop-leave-active {
    transition: none;
  }

  .ui-pop-enter-from,
  .ui-pop-leave-to,
  [data-placement="top"].ui-pop-enter-from,
  [data-placement="top"].ui-pop-leave-to {
    transform: none;
  }
}
</style>
