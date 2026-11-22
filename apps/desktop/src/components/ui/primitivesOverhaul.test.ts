// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { nextTick } from 'vue'
import UiButton from './button.vue'
import UiCard from './card.vue'
import UiCheckbox from './checkbox.vue'
import UiCollapsible from './collapsible.vue'
import UiDropdownMenu from './dropdown-menu.vue'
import UiInput from './input.vue'
import UiPopover from './popover.vue'
import UiSelectField from './select-field.vue'
import UiSelect from './select.vue'
import UiSheet from './sheet.vue'
import UiSwitch from './switch.vue'
import UiTabs from './tabs.vue'
import UiTextarea from './textarea.vue'
import UiToggleGroup from './toggle-group.vue'
import UiToggle from './toggle.vue'
import buttonSource from './button.vue?raw'
import badgeSource from './badge.vue?raw'
import selectSource from './select.vue?raw'
import selectFieldSource from './select-field.vue?raw'
import popoverSource from './popover.vue?raw'
import dropdownMenuSource from './dropdown-menu.vue?raw'
import sheetSource from './sheet.vue?raw'
import switchSource from './switch.vue?raw'
import checkboxSource from './checkbox.vue?raw'
import collapsibleSource from './collapsible.vue?raw'
import progressSource from './progress.vue?raw'
import skeletonSource from './skeleton.vue?raw'
import cardSource from './card.vue?raw'
import islandCardSource from './island-card.vue?raw'
import uiTransitionSource from './ui-transition.ts?raw'

afterEach(() => {
  document.body.innerHTML = ''
})

describe('UI primitives contrast & tactile craft', () => {
  it('uses semantic --text-on-primary contrast and reduced-motion-safe tactile press on UiButton', () => {
    const wrapper = mount(UiButton, { slots: { default: 'Confirm' } })
    const btn = wrapper.get('button')
    const className = btn.attributes('class') ?? ''

    expect(className).toContain('text-[var(--text-on-primary,#ffffff)]')
    expect(className).not.toMatch(/(?:^|\s)text-white(?:\s|$)/)
    expect(className).toContain('active:scale-[0.985]')
    expect(className).toContain('motion-reduce:active:scale-100')
    expect(className).toContain('motion-reduce:transition-none')
    expect(btn.attributes('data-variant')).toBe('default')
    wrapper.unmount()
  })

  it('uses semantic --text-on-primary contrast on UiBadge default variant (Vapor SFC)', () => {
    expect(badgeSource).toContain('<template vapor>')
    expect(badgeSource).toContain('text-[var(--text-on-primary,#ffffff)]')
    expect(badgeSource).not.toMatch(/\btext-white\b/)
    expect(badgeSource).toContain('border-[var(--border-default)]')
    expect(badgeSource).toContain('border-[var(--border-muted)]')
    expect(badgeSource).toContain('motion-reduce:transition-none')
  })

  it('keeps zero left-stripe borders across overhauled primitive sources', () => {
    const sources = [
      buttonSource,
      badgeSource,
      selectSource,
      selectFieldSource,
      popoverSource,
      dropdownMenuSource,
      sheetSource,
      switchSource,
      checkboxSource,
      collapsibleSource,
      skeletonSource,
      cardSource,
      islandCardSource,
    ]
    for (const src of sources) {
      expect(src).not.toMatch(/border-left:\s*[23]px\s+solid/i)
      expect(src).not.toMatch(/inset\s+[23]px\s+0/i)
    }
  })
})

describe('UiSheet transitions & dismissal', () => {
  it('uses independent Vue transitions instead of dead animate-in classes', () => {
    expect(sheetSource).not.toContain('animate-in')
    expect(sheetSource).not.toContain('animate-out')
    expect(sheetSource).toContain('<UiTransition name="ui-sheet-backdrop">')
    expect(sheetSource).toContain('<UiTransition :name="`ui-sheet-${side}`">')
    expect(uiTransitionSource).toContain('Transition(props, ctx)')
  })

  it('opens for every side direction and closes on Escape, backdrop click, or close button', async () => {
    for (const side of ['top', 'right', 'bottom', 'left'] as const) {
      const wrapper = mount(UiSheet, {
        props: { open: true, side },
        slots: { default: '<p class="sheet-body">Drawer content</p>' },
      })
      const content = document.body.querySelector('[data-slot="sheet-content"]')
      expect(content).not.toBeNull()
      expect(content?.getAttribute('data-side')).toBe(side)
      expect(document.body.querySelector('[data-slot="sheet-backdrop"]')).not.toBeNull()

      window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
      await nextTick()

      expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
      expect(document.body.querySelector('[data-slot="sheet-content"]')).toBeNull()
      wrapper.unmount()
    }
  })

  it('closes when clicking the backdrop or close button', async () => {
    const wrapper = mount(UiSheet, {
      props: { open: true, side: 'right' },
      slots: { default: '<p>Body</p>' },
    })
    const backdrop = document.body.querySelector<HTMLElement>('[data-slot="sheet-backdrop"]')!
    backdrop.click()
    await nextTick()
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    expect(document.body.querySelector('[data-slot="sheet-content"]')).toBeNull()

    await wrapper.setProps({ open: false })
    await wrapper.setProps({ open: true })
    await nextTick()
    const closeBtn = document.body.querySelector<HTMLButtonElement>('[data-slot="sheet-close"]')!
    closeBtn.click()
    await nextTick()
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    wrapper.unmount()
  })
})

describe('Popup primitives: UiSelect, UiSelectField, UiPopover, UiDropdownMenu', () => {
  it('wires genuine Vue ui-pop transitions across all popup primitives', () => {
    expect(selectSource).toContain('<UiTransition name="ui-pop">')
    expect(selectFieldSource).toContain('<UiTransition name="ui-pop">')
    expect(popoverSource).toContain('<UiTransition name="ui-pop">')
    expect(dropdownMenuSource).toContain('<UiTransition name="ui-pop">')
    expect(uiTransitionSource).toContain('Transition(props, ctx)')
  })

  it('UiSelect opens with rotating chevron, navigates with keyboard, and closes on Escape or outside click', async () => {
    const wrapper = mount(UiSelect, {
      attachTo: document.body,
      props: { modelValue: 'alpha', ariaLabel: 'Mode' },
      slots: {
        default: `
          <template #default="{ select }">
            <button type="button" class="opt-a" @click="select('alpha')">Alpha</button>
            <button type="button" class="opt-b" @click="select('beta')">Beta</button>
          </template>
        `,
      },
    })

    const trigger = wrapper.get('button')
    expect(trigger.attributes('aria-expanded')).toBe('false')
    expect(wrapper.get('svg').classes()).not.toContain('rotate-180')

    await trigger.trigger('keydown', { key: 'ArrowDown' })
    await nextTick()
    expect(trigger.attributes('aria-expanded')).toBe('true')
    expect(wrapper.get('svg').classes()).toContain('rotate-180')
    expect(document.activeElement).toBe(wrapper.get('.opt-a').element)

    await wrapper.get('[data-slot="select-content"]').trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(wrapper.get('.opt-b').element)

    await wrapper.get('[data-slot="select-content"]').trigger('keydown', { key: 'Escape' })
    await nextTick()
    expect(wrapper.find('[data-slot="select-content"]').exists()).toBe(false)
    expect(trigger.attributes('aria-expanded')).toBe('false')

    await trigger.trigger('click')
    expect(wrapper.find('[data-slot="select-content"]').exists()).toBe(true)

    const outside = document.createElement('div')
    document.body.append(outside)
    outside.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))
    await nextTick()
    expect(wrapper.find('[data-slot="select-content"]').exists()).toBe(false)

    await trigger.trigger('click')
    await wrapper.get('.opt-b').trigger('click')
    expect(wrapper.emitted('update:modelValue')?.at(-1)).toEqual(['beta'])
    expect(wrapper.find('[data-slot="select-content"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('UiSelectField closes via window Escape while keeping listbox transition intact', async () => {
    const wrapper = mount(UiSelectField, {
      attachTo: document.body,
      props: {
        value: 'a',
        options: [
          { value: 'a', label: 'Alpha' },
          { value: 'b', label: 'Beta' },
        ],
      },
    })

    await wrapper.get('.ui-select-trigger').trigger('click')
    expect(document.body.querySelector('.ui-select-menu')).not.toBeNull()

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await nextTick()
    expect(document.body.querySelector('.ui-select-menu')).toBeNull()
    wrapper.unmount()
  })

  it('UiPopover dismisses on outside click and Escape', async () => {
    const wrapper = mount(UiPopover, {
      attachTo: document.body,
      slots: {
        trigger: '<button type="button" class="pop-trigger">Open</button>',
        default: '<div class="pop-body">Details</div>',
      },
    })

    await wrapper.get('.pop-trigger').trigger('click')
    expect(wrapper.find('[data-slot="popover-content"]').exists()).toBe(true)

    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await nextTick()
    expect(wrapper.find('[data-slot="popover-content"]').exists()).toBe(false)
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])

    await wrapper.get('.pop-trigger').trigger('click')
    const outside = document.createElement('div')
    document.body.append(outside)
    outside.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }))
    await nextTick()
    expect(wrapper.find('[data-slot="popover-content"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('UiDropdownMenu supports keyboard navigation, outside click dismissal, and Escape', async () => {
    const wrapper = mount(UiDropdownMenu, {
      attachTo: document.body,
      props: { placement: 'top' },
      slots: {
        trigger: '<button type="button" class="menu-trigger">Menu</button>',
        default: `
          <button type="button" role="menuitem" class="item-1">First</button>
          <button type="button" role="menuitem" class="item-2">Second</button>
        `,
      },
    })

    await wrapper.get('.menu-trigger').trigger('keydown', { key: 'ArrowDown' })
    await nextTick()
    const menu = wrapper.get('[data-slot="dropdown-menu-content"]')
    expect(menu.attributes('data-placement')).toBe('top')
    expect(document.activeElement).toBe(wrapper.get('.item-1').element)

    await menu.trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(wrapper.get('.item-2').element)

    await menu.trigger('keydown', { key: 'Escape' })
    await nextTick()
    expect(wrapper.find('[data-slot="dropdown-menu-content"]').exists()).toBe(false)
    expect(wrapper.emitted('update:open')?.at(-1)).toEqual([false])
    wrapper.unmount()
  })
})

describe('Form, segmented & content primitives', () => {
  it('UiSwitch renders monochrome-safe track & thumb contrast and toggles value', async () => {
    const wrapper = mount(UiSwitch, { props: { modelValue: false } })
    const control = wrapper.get('button[role="switch"]')
    const thumb = wrapper.get('[data-slot="switch-thumb"]')

    expect(control.attributes('data-state')).toBe('unchecked')
    expect(control.attributes('class')).toContain('border-[var(--border-default)]')
    expect(control.attributes('class')).toContain('data-[state=checked]:bg-[var(--bg-primary-button,hsl(var(--primary)))]')
    expect(thumb.attributes('class')).toContain('data-[state=checked]:bg-[var(--text-on-primary,#ffffff)]')

    await control.trigger('click')
    expect(wrapper.emitted('update:modelValue')).toEqual([[true]])

    await wrapper.setProps({ disabled: true })
    await control.trigger('click')
    expect(wrapper.emitted('update:modelValue')).toHaveLength(1)
    wrapper.unmount()
  })

  it('UiCheckbox uses neutral border when unchecked and high-contrast fill when checked', async () => {
    const wrapper = mount(UiCheckbox, { props: { modelValue: false } })
    const box = wrapper.get('button[role="checkbox"]')
    const cls = box.attributes('class') ?? ''

    expect(cls).toContain('border-[var(--border-default)]')
    expect(cls).toContain('data-[state=checked]:bg-[var(--bg-primary-button,hsl(var(--primary)))]')
    expect(cls).toContain('data-[state=checked]:text-[var(--text-on-primary,#ffffff)]')
    expect(wrapper.find('svg').exists()).toBe(false)

    await wrapper.setProps({ modelValue: true })
    expect(box.attributes('data-state')).toBe('checked')
    expect(wrapper.get('svg').classes()).toContain('ui-check-icon')
    wrapper.unmount()
  })

  it('UiCollapsible uses grid-template-rows collapse and updates data-state cleanly', async () => {
    expect(collapsibleSource).toContain('grid-template-rows: 0fr')
    expect(collapsibleSource).toContain('grid-template-rows: 1fr')

    const wrapper = mount(UiCollapsible, {
      props: { open: false },
      slots: {
        trigger: '<button type="button" class="collapse-btn">Toggle</button>',
        default: '<p>Body</p>',
      },
    })
    const panel = wrapper.get('[data-slot="collapsible-content"]')
    expect(panel.attributes('data-state')).toBe('closed')

    await wrapper.get('.collapse-btn').trigger('click')
    expect(panel.attributes('data-state')).toBe('open')
    expect(wrapper.emitted('update:open')).toEqual([[true]])
    wrapper.unmount()
  })

  it('UiProgress updates reactively with monochrome fill and UiSkeleton drops purple primary tint (Vapor SFCs)', () => {
    expect(progressSource).toContain('<template vapor>')
    expect(progressSource).toContain('const percentage = computed(')
    expect(progressSource).toContain('role="progressbar"')
    expect(progressSource).toContain('bg-[var(--bg-primary-button,hsl(var(--primary)))]')
    expect(progressSource).toContain('motion-reduce:transition-none')
    expect(progressSource).not.toContain('bg-primary/20')

    expect(skeletonSource).toContain('<template vapor>')
    expect(skeletonSource).toContain('ui-skeleton')
    expect(skeletonSource).toContain('@media (prefers-reduced-motion: reduce)')
    expect(skeletonSource).not.toContain('bg-primary/10')
  })

  it('UiTabs, UiToggleGroup, UiToggle, UiInput, UiTextarea, and UiCard use monochrome neutral tokens', async () => {
    const tabs = mount(UiTabs, {
      props: { modelValue: 'general' },
      slots: {
        default: '<button type="button" role="tab" data-state="active">General</button>',
      },
    })
    expect(tabs.get('[data-slot="tabs-list"]').attributes('class')).toContain('border-[var(--border-muted)]')
    tabs.unmount()

    const group = mount(UiToggleGroup, {
      props: { modelValue: 'grid' },
      slots: {
        default: '<button type="button" data-state="on">Grid</button>',
      },
    })
    expect(group.get('[data-slot="toggle-group"]').attributes('class')).toContain('border-[var(--border-muted)]')
    group.unmount()

    const toggle = mount(UiToggle, { props: { pressed: true }, slots: { default: 'Bold' } })
    expect(toggle.get('button').attributes('data-state')).toBe('on')
    expect(toggle.get('button').attributes('class')).toContain('data-[state=on]:bg-[var(--surface-raised)]')
    toggle.unmount()

    const input = mount(UiInput, { props: { modelValue: '' } })
    expect(input.get('input').attributes('class')).toContain('--focus-ring')
    expect(input.get('input').attributes('class')).not.toContain('ring-ring')
    input.unmount()

    const textarea = mount(UiTextarea, { props: { modelValue: '' } })
    expect(textarea.get('textarea').attributes('class')).toContain('--focus-ring')
    expect(textarea.get('textarea').attributes('class')).not.toContain('ring-ring')
    textarea.unmount()

    const card = mount(UiCard, { slots: { default: 'Card content' } })
    expect(card.get('[data-slot="card"]').attributes('class')).toContain('border-[var(--border-card,var(--border-default))]')
    card.unmount()
  })
})
