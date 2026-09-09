// @vitest-environment happy-dom
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ColorField from './color-field.vue'

function mountField(overrides: Record<string, unknown> = {}) {
  return mount(ColorField, {
    props: {
      hue: 210,
      saturation: 80,
      lightness: 40,
      label: 'Custom accent color',
      saturationLabel: 'Saturation',
      lightnessLabel: 'Lightness',
      ...overrides,
    },
  })
}

function stubRect(wrapper: ReturnType<typeof mountField>): void {
  const element = wrapper.element as HTMLElement
  // happy-dom reports a zero rect; the pointer handler needs real geometry.
  element.getBoundingClientRect = () =>
    ({ left: 0, top: 0, width: 200, height: 100, right: 200, bottom: 100, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect
}

describe('ColorField', () => {
  it('exposes a single named composite control with a spoken value', () => {
    const wrapper = mountField()
    const field = wrapper.find('[role="slider"]')
    expect(field.attributes('aria-label')).toBe('Custom accent color')
    expect(field.attributes('aria-valuemin')).toBe('0')
    expect(field.attributes('aria-valuemax')).toBe('100')
    expect(field.attributes('aria-valuenow')).toBe('80')
    expect(field.attributes('aria-valuetext')).toBe('Saturation 80% / Lightness 40%')
  })

  it('keeps both gradient layers (regression: shorthand drops the lightness ramp)', () => {
    const wrapper = mountField({ hue: 210 })
    // happy-dom keeps only the last layer for the `background` shorthand, so the
    // component must emit `background-image` for both layers to survive.
    const style = wrapper.element.getAttribute('style') ?? ''
    expect(style).toContain('rgba(0, 0, 0, 0)')
    expect(style).toContain('hsl(210 0% 50%)')
    expect(style).toContain('hsl(210 100% 50%)')
  })

  it('positions the handle from saturation and lightness', () => {
    const wrapper = mountField({ saturation: 25, lightness: 75 })
    const handle = wrapper.find('span')
    expect(handle.attributes('style')).toContain('left: 25%')
    expect(handle.attributes('style')).toContain('top: 25%')
  })

  it('maps a pointer press to saturation and lightness', async () => {
    const wrapper = mountField()
    stubRect(wrapper)
    const field = wrapper.find('[role="slider"]')

    await field.trigger('pointerdown', { button: 0, clientX: 200, clientY: 100, pointerId: 1 })
    expect(wrapper.emitted('update:saturation')?.at(-1)).toEqual([100])
    expect(wrapper.emitted('update:lightness')?.at(-1)).toEqual([0])
    // The host must be told this was direct manipulation, not a programmatic sync.
    expect(wrapper.emitted('direct-input')).toHaveLength(1)
  })

  it('ignores non-primary pointer buttons', async () => {
    const wrapper = mountField()
    stubRect(wrapper)
    await wrapper.find('[role="slider"]').trigger('pointerdown', { button: 2, clientX: 10, clientY: 10, pointerId: 1 })
    expect(wrapper.emitted('update:saturation')).toBeUndefined()
  })

  it('adjusts one axis per arrow key and jumps with Home/End', async () => {
    const wrapper = mountField({ saturation: 50, lightness: 50 })
    const field = wrapper.find('[role="slider"]')

    await field.trigger('keydown', { key: 'ArrowRight' })
    expect(wrapper.emitted('update:saturation')?.at(-1)).toEqual([51])
    expect(wrapper.emitted('update:lightness')?.at(-1)).toEqual([50])

    await field.trigger('keydown', { key: 'ArrowDown', shiftKey: true })
    expect(wrapper.emitted('update:lightness')?.at(-1)).toEqual([40])

    await field.trigger('keydown', { key: 'End' })
    expect(wrapper.emitted('update:saturation')?.at(-1)).toEqual([100])
  })

  it('clamps keyboard input at the domain edges', async () => {
    const wrapper = mountField({ saturation: 100, lightness: 0 })
    const field = wrapper.find('[role="slider"]')

    await field.trigger('keydown', { key: 'ArrowRight' })
    expect(wrapper.emitted('update:saturation')?.at(-1)).toEqual([100])

    await field.trigger('keydown', { key: 'ArrowDown' })
    expect(wrapper.emitted('update:lightness')?.at(-1)).toEqual([0])
  })
})
