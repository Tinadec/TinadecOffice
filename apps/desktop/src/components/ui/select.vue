<script setup lang="ts">
import { cn } from '@/lib/utils'
import { ChevronDown } from '@lucide/vue'
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import i18n from '@/i18n'
import { UiTransition } from './ui-transition'

interface Props {
  modelValue?: string
  placeholder?: string
  displayValue?: string
  ariaLabel?: string
  disabled?: boolean
  class?: string
}

const props = defineProps<Props>()

const emit = defineEmits<{
  'update:modelValue': [value: string]
}>()

const isOpen = ref(false)
const selectedValue = ref(props.modelValue)
const rootRef = ref<HTMLElement | null>(null)
const triggerRef = ref<HTMLButtonElement | null>(null)
const menuRef = ref<HTMLElement | null>(null)

watch(() => props.modelValue, (val) => {
  selectedValue.value = val
})

function getFocusableOptions(): HTMLElement[] {
  if (!menuRef.value) return []
  return Array.from(
    menuRef.value.querySelectorAll<HTMLElement>('button:not(:disabled), [role="option"]:not([aria-disabled="true"])'),
  )
}

function focusOption(index: number) {
  const options = getFocusableOptions()
  if (!options.length) return
  const normalized = ((index % options.length) + options.length) % options.length
  options[normalized]?.focus()
}

function openMenu(focusTarget?: 'first' | 'last') {
  if (props.disabled) return
  isOpen.value = true
  if (focusTarget) {
    void nextTick(() => {
      const options = getFocusableOptions()
      if (!options.length) return
      focusOption(focusTarget === 'last' ? options.length - 1 : 0)
    })
  }
}

function closeMenu(restoreFocus = false) {
  if (!isOpen.value) return
  isOpen.value = false
  if (restoreFocus) triggerRef.value?.focus()
}

function toggleMenu() {
  if (isOpen.value) closeMenu()
  else openMenu()
}

function select(value: string) {
  selectedValue.value = value
  emit('update:modelValue', value)
  closeMenu(true)
}

function onTriggerKeydown(event: KeyboardEvent) {
  if (event.key === 'ArrowDown') {
    event.preventDefault()
    if (!isOpen.value) openMenu('first')
    else focusOption(0)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    if (!isOpen.value) openMenu('last')
    else focusOption(-1)
  } else if (event.key === 'Escape' && isOpen.value) {
    event.preventDefault()
    event.stopPropagation()
    closeMenu(true)
  }
}

function onMenuKeydown(event: KeyboardEvent) {
  const options = getFocusableOptions()
  const activeEl = document.activeElement as HTMLElement | null
  const currentIndex = activeEl ? options.indexOf(activeEl) : -1

  if (event.key === 'ArrowDown') {
    event.preventDefault()
    focusOption(currentIndex < 0 ? 0 : currentIndex + 1)
  } else if (event.key === 'ArrowUp') {
    event.preventDefault()
    focusOption(currentIndex < 0 ? options.length - 1 : currentIndex - 1)
  } else if (event.key === 'Home') {
    event.preventDefault()
    focusOption(0)
  } else if (event.key === 'End') {
    event.preventDefault()
    focusOption(options.length - 1)
  } else if (event.key === 'Escape') {
    event.preventDefault()
    event.stopPropagation()
    closeMenu(true)
  }
}

function onPointerDownOutside(event: PointerEvent) {
  const target = event.target as Node | null
  if (!target || rootRef.value?.contains(target)) return
  closeMenu()
}

function onWindowKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape' || event.defaultPrevented) return
  event.preventDefault()
  closeMenu(true)
}

watch(isOpen, (open) => {
  if (open) {
    window.addEventListener('pointerdown', onPointerDownOutside, true)
    window.addEventListener('keydown', onWindowKeydown)
  } else {
    window.removeEventListener('pointerdown', onPointerDownOutside, true)
    window.removeEventListener('keydown', onWindowKeydown)
  }
})

watch(() => props.disabled, (disabled) => {
  if (disabled) closeMenu()
})

onBeforeUnmount(() => {
  window.removeEventListener('pointerdown', onPointerDownOutside, true)
  window.removeEventListener('keydown', onWindowKeydown)
})
</script>

<template>
  <div ref="rootRef" class="relative">
    <button
      ref="triggerRef"
      type="button"
      aria-haspopup="listbox"
      :aria-expanded="isOpen"
      :aria-label="ariaLabel"
      :disabled="disabled"
      :class="cn(
        'ui-select-inline-trigger flex h-[var(--control-height-md,34px)] w-full min-w-0 items-center justify-between gap-2 rounded-[var(--radius-control,6px)] border border-[var(--border-input,var(--border-default))] bg-[var(--surface-input)] px-3 py-1.5 text-[13px] text-[var(--text-primary)] transition-[background-color,border-color,color,box-shadow] duration-150 ease-out motion-reduce:transition-none placeholder:text-[var(--text-muted)] hover:bg-[var(--surface-hover)] hover:border-[var(--border-input-focus)] focus-visible:border-[var(--border-input-focus)] focus-visible:outline-none focus-visible:shadow-[var(--focus-ring,0_0_0_2px_color-mix(in_srgb,var(--accent-primary)_22%,transparent))] disabled:cursor-not-allowed disabled:opacity-50',
        props.class,
      )"
      @click="toggleMenu"
      @keydown="onTriggerKeydown"
    >
      <span v-if="selectedValue" class="min-w-0 truncate">{{ displayValue || selectedValue }}</span>
      <span v-else class="min-w-0 truncate text-[var(--text-muted)]">{{ placeholder || i18n.global.t('common.selectPlaceholder') }}</span>
      <ChevronDown
        :class="cn(
          'h-4 w-4 shrink-0 opacity-60 transition-transform duration-150 ease-out motion-reduce:transition-none',
          isOpen && 'rotate-180',
        )"
        aria-hidden="true"
      />
    </button>
    <UiTransition name="ui-pop">
      <div
        v-if="isOpen"
        ref="menuRef"
        role="listbox"
        data-slot="select-content"
        class="absolute z-50 mt-1.5 max-h-60 w-full overflow-auto rounded-[var(--radius-control-md,8px)] border border-[var(--border-default)] bg-[var(--surface-raised)] p-1 text-[var(--text-primary)] shadow-[var(--shadow-panel)] outline-none"
        @keydown="onMenuKeydown"
      >
        <slot :select="select" :selected-value="selectedValue" />
      </div>
    </UiTransition>
  </div>
</template>

<style scoped>
button.ui-select-inline-trigger {
  border: 1px solid var(--border-input, var(--border-default));
}

button.ui-select-inline-trigger:hover:not(:disabled),
button.ui-select-inline-trigger:focus-visible {
  border-color: var(--border-input-focus);
}

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
