<script setup lang="ts">
/**
 * Styled replacement for the native `<select>`.
 *
 * The listbox is teleported to <body> (or its open modal dialog) and positioned
 * with the shared `computeDropdownPlacement` helper. Scrolling outside the menu
 * closes it before its fixed position can drift away from the trigger. Declarative `value`/`change` keeps
 * existing callers working; the native element is not rendered at all, so the
 * OS popup can never leak through.
 */
import { computed, nextTick, onBeforeUnmount, ref, useId, watch } from 'vue'
import { ChevronDown } from '@lucide/vue'
import { computeDropdownPlacement, type DropdownPlacement } from '@/lib/dropdownPlacement'
import { cn } from '@/lib/utils'

export interface SelectFieldOption {
  value: string
  label: string
  disabled?: boolean
}

interface Props {
  /** Current value. `modelValue` is accepted as the Vue 3 shorthand alias. */
  value?: string | null
  modelValue?: string | null
  options: readonly SelectFieldOption[]
  disabled?: boolean
  ariaLabel?: string
  placeholder?: string
  class?: string
}

const props = defineProps<Props>()
const emit = defineEmits<{
  'update:value': [value: string]
  'update:modelValue': [value: string]
  /** Mirrors the native select event payload: the chosen value. */
  change: [value: string]
}>()

defineOptions({ inheritAttrs: false })

const currentValue = computed(() => props.value ?? props.modelValue ?? '')
const selectedOption = computed(() => props.options.find((option) => option.value === currentValue.value))
const displayLabel = computed(
  () => selectedOption.value?.label ?? (currentValue.value === '' ? props.placeholder ?? '' : currentValue.value),
)

const open = ref(false)
const menuId = `ui-select-menu-${useId()}`
const trigger = ref<HTMLButtonElement | null>(null)
const menu = ref<HTMLElement | null>(null)
const portalTarget = ref<HTMLElement | 'body'>('body')
let ownerDialog: HTMLElement | null = null
const placement = ref<DropdownPlacement>({ position: 'fixed', left: '0px' })
const activeIndex = ref(0)
const controlDisabled = computed(() => props.disabled === true)
const activeOptionId = computed(() => (open.value ? `${menuId}-option-${activeIndex.value}` : undefined))

// overflowY is carried as a CSS variable so the menu keeps a stable inline
// style shape while the helper decides whether it may scroll.
const menuStyle = computed(() => {
  const { overflowY, ...rest } = placement.value
  return { ...rest, '--ui-select-overflow-y': overflowY ?? 'visible' }
})

function enabledIndexes(): number[] {
  return props.options.map((option, index) => (option.disabled ? -1 : index)).filter((index) => index >= 0)
}

function moveActive(delta: number): void {
  const indexes = enabledIndexes()
  if (!indexes.length) return
  const position = indexes.indexOf(activeIndex.value)
  const next = position === -1 ? 0 : (position + delta + indexes.length) % indexes.length
  activeIndex.value = indexes[next]
}

function placeMenu(): void {
  const el = trigger.value
  if (!el) return
  placement.value = computeDropdownPlacement(el.getBoundingClientRect(), window.innerWidth, window.innerHeight, {
    estimatedHeight: Math.min(280, props.options.length * 32 + 12),
    minWidth: Math.round(el.getBoundingClientRect().width),
  })
}

function openMenu(): void {
  if (props.disabled) return
  ownerDialog = trigger.value?.closest<HTMLElement>('dialog[open], [role="dialog"]') ?? null
  portalTarget.value = ownerDialog ?? 'body'
  if (ownerDialog instanceof HTMLDialogElement) {
    ownerDialog.addEventListener('close', onDialogClose, { once: true })
  }
  open.value = true
  const selected = props.options.findIndex((option) => option.value === currentValue.value && !option.disabled)
  activeIndex.value = selected >= 0 ? selected : enabledIndexes()[0] ?? 0
  void nextTick(() => {
    placeMenu()
    menu.value?.querySelector<HTMLElement>('.ui-select-option[data-active="true"]')?.scrollIntoView({ block: 'nearest' })
  })
}

function closeMenu(restoreFocus = false): void {
  if (!open.value) return
  open.value = false
  if (ownerDialog instanceof HTMLDialogElement) {
    ownerDialog.removeEventListener('close', onDialogClose)
  }
  ownerDialog = null
  if (restoreFocus) trigger.value?.focus()
}

function onDialogClose(): void { closeMenu() }

function choose(option: SelectFieldOption): void {
  if (controlDisabled.value || option.disabled) return
  // `v-model` binds to update:modelValue; callers that pass a literal
  // value/change pair use update:value. Emit both so either contract works.
  emit('update:modelValue', option.value)
  emit('update:value', option.value)
  emit('change', option.value)
  closeMenu(true)
}

function onTriggerKeydown(event: KeyboardEvent): void {
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    if (!open.value) openMenu()
    else moveActive(event.key === 'ArrowDown' ? 1 : -1)
    return
  }
  if (!open.value && (event.key === 'Enter' || event.key === ' ')) {
    event.preventDefault()
    openMenu()
    return
  }
  if (!open.value) return
  if (event.key === 'Escape') {
    event.preventDefault()
    closeMenu(true)
  } else if (event.key === 'Enter' || event.key === ' ') {
    event.preventDefault()
    const option = props.options[activeIndex.value]
    if (option) choose(option)
  } else if (event.key === 'Home' || event.key === 'End') {
    event.preventDefault()
    const indexes = enabledIndexes()
    if (indexes.length) activeIndex.value = event.key === 'Home' ? indexes[0] : indexes[indexes.length - 1]
  }
}

function onMenuKeydown(event: KeyboardEvent): void {
  if (event.key !== 'Escape') return
  event.preventDefault()
  event.stopPropagation()
  closeMenu(true)
}

function onPointerDownOutside(event: PointerEvent): void {
  const target = event.target as Node | null
  if (!target) return
  if (trigger.value?.contains(target) || menu.value?.contains(target)) return
  closeMenu()
}

function onScroll(event: Event): void {
  if (!(event.target instanceof Node) || !menu.value?.contains(event.target)) closeMenu()
}
function onResize(): void { closeMenu() }

watch(open, (isOpen) => {
  if (isOpen) {
    window.addEventListener('pointerdown', onPointerDownOutside, true)
    window.addEventListener('scroll', onScroll, true)
    window.addEventListener('resize', onResize)
  } else {
    window.removeEventListener('pointerdown', onPointerDownOutside, true)
    window.removeEventListener('scroll', onScroll, true)
    window.removeEventListener('resize', onResize)
  }
})

// A disabled control must not leave its teleported menu interactive: the
// trigger button disables itself, but the menu lives outside it in the DOM.
watch(controlDisabled, (isDisabled) => {
  if (isDisabled) closeMenu()
})

onBeforeUnmount(() => {
  closeMenu()
  window.removeEventListener('pointerdown', onPointerDownOutside, true)
  window.removeEventListener('scroll', onScroll, true)
  window.removeEventListener('resize', onResize)
})
</script>

<template>
  <div :class="cn('ui-select', props.class)">
    <button
      ref="trigger"
      type="button"
      class="ui-select-trigger"
      role="combobox"
      aria-haspopup="listbox"
      :aria-expanded="open"
      :aria-controls="open ? menuId : undefined"
      :aria-activedescendant="activeOptionId"
      :aria-label="ariaLabel"
      :disabled="disabled"
      v-bind="$attrs"
      @click="open ? closeMenu() : openMenu()"
      @keydown="onTriggerKeydown"
    >
      <span class="ui-select-label" :class="{ 'is-placeholder': !selectedOption }">{{ displayLabel }}</span>
      <ChevronDown :size="14" class="ui-select-chevron" :class="{ 'is-open': open }" aria-hidden="true" />
    </button>

    <Teleport :to="portalTarget">
      <div
        v-if="open"
        ref="menu"
        :id="menuId"
        class="ui-select-menu"
        role="listbox"
        :style="menuStyle"
        :aria-label="ariaLabel"
      @keydown="onMenuKeydown"
      >
        <button
          v-for="(option, index) in options"
          :key="option.value"
          type="button"
          class="ui-select-option"
          role="option"
          :id="`${menuId}-option-${index}`"
          :aria-selected="option.value === currentValue"
          :disabled="option.disabled"
          :data-active="index === activeIndex"
          :data-value="option.value"
          :class="{ 'is-selected': option.value === currentValue, 'is-disabled': option.disabled }"
          @click="choose(option)"
          @mousemove="activeIndex = index"
        >
          {{ option.label }}
        </button>
      </div>
    </Teleport>
  </div>
</template>
