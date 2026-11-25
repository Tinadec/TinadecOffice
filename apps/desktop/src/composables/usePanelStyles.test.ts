// @vitest-environment happy-dom
import { effectScope } from 'vue'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { beforeEach, describe, expect, it } from 'vitest'
import zhCN from '@/locales/zh-CN'
import en from '@/locales/en'
import PanelStyleControl from '@/components/ui/panel-style-control.vue'
import {
  PANEL_MATERIAL_PRESETS,
  __resetPanelStylesForTests,
  computePanelStyle,
  usePanelStyles,
} from './usePanelStyles'

const STORAGE_KEY = 'tinadec-panel-style'

describe('usePanelStyles three-layer material engine & presets', () => {
  beforeEach(() => {
    __resetPanelStylesForTests()
    localStorage.clear()
  })

  it('initializes with recommended frosted preset and syncs :root Layer 3 variables immediately', () => {
    const { panelStyle } = usePanelStyles()

    expect(panelStyle.value).toEqual({
      preset: 'frosted',
      effect: PANEL_MATERIAL_PRESETS.frosted.effect,
      opacity: 68,
      blur: 18,
      saturation: 160,
    })

    const root = document.documentElement
    expect(root.getAttribute('data-global-panel-effect')).toBe('blur')
    expect(root.getAttribute('data-panel-preset')).toBe('frosted')
    expect(root.style.getPropertyValue('--material-floating-bg')).toBe('rgba(var(--bg-primary-rgb, 10, 14, 20), 0.68)')
    expect(root.style.getPropertyValue('--material-floating-filter')).toBe('blur(18px) saturate(160%)')
    expect(root.style.getPropertyValue('--material-floating-border')).toBe('var(--material-glass-border)')
  })

  it('applies all 5 canonical presets + custom and updates :root Layer 3 tokens per effect', () => {
    const { panelStyle, applyPreset } = usePanelStyles()
    const root = document.documentElement

    // 1. solid -> opaque
    applyPreset('solid')
    expect(panelStyle.value).toEqual({
      preset: 'solid',
      effect: 'opaque',
      opacity: 100,
      blur: 0,
      saturation: 100,
    })
    expect(root.getAttribute('data-global-panel-effect')).toBe('opaque')
    expect(root.getAttribute('data-panel-preset')).toBe('solid')
    expect(root.style.getPropertyValue('--material-floating-bg')).toBe('var(--bg-secondary)')
    expect(root.style.getPropertyValue('--material-floating-filter')).toBe('none')
    expect(root.style.getPropertyValue('--material-floating-border')).toBe('var(--border-default)')

    // 2. custom from solid transitions cleanly into editable blur mode
    applyPreset('custom')
    expect(panelStyle.value).toEqual({
      preset: 'custom',
      effect: 'blur',
      opacity: 68,
      blur: 18,
      saturation: 160,
    })
    expect(root.getAttribute('data-panel-preset')).toBe('custom')

    // 3. crystal -> 48% / 24px / 180%
    applyPreset('crystal')
    expect(panelStyle.value).toEqual({
      preset: 'crystal',
      effect: 'blur',
      opacity: 48,
      blur: 24,
      saturation: 180,
    })
    expect(root.style.getPropertyValue('--material-floating-bg')).toBe('rgba(var(--bg-primary-rgb, 10, 14, 20), 0.48)')
    expect(root.style.getPropertyValue('--material-floating-filter')).toBe('blur(24px) saturate(180%)')
    expect(root.style.getPropertyValue('--material-floating-border')).toBe('var(--material-glass-border)')

    // 4. custom from crystal preserves 48% / 24px / 180%
    applyPreset('custom')
    expect(panelStyle.value).toEqual({
      preset: 'custom',
      effect: 'blur',
      opacity: 48,
      blur: 24,
      saturation: 180,
    })

    // 5. mica -> 82% / 14px / 130%
    applyPreset('mica')
    expect(panelStyle.value).toEqual({
      preset: 'mica',
      effect: 'blur',
      opacity: 82,
      blur: 14,
      saturation: 130,
    })

    // 6. translucent -> 76% / 0px / 100%
    applyPreset('translucent')
    expect(panelStyle.value).toEqual({
      preset: 'translucent',
      effect: 'translucent',
      opacity: 76,
      blur: 0,
      saturation: 100,
    })
    expect(root.getAttribute('data-global-panel-effect')).toBe('translucent')
    expect(root.getAttribute('data-panel-preset')).toBe('translucent')
    expect(root.style.getPropertyValue('--material-floating-bg')).toBe('rgba(var(--bg-primary-rgb, 10, 14, 20), 0.76)')
    expect(root.style.getPropertyValue('--material-floating-filter')).toBe('none')
    expect(root.style.getPropertyValue('--material-floating-border')).toBe('var(--border-muted)')
  })

  it('migrates legacy localStorage payloads without preset/saturation cleanly', () => {
    // Exact match -> resolves named preset + optical saturation
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      effect: 'blur',
      opacity: 48,
      blur: 24,
    }))
    expect(usePanelStyles().panelStyle.value).toEqual({
      preset: 'crystal',
      effect: 'blur',
      opacity: 48,
      blur: 24,
      saturation: 180,
    })

    // Non-matching blur legacy payload -> custom with saturation 160
    __resetPanelStylesForTests()
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      effect: 'blur',
      opacity: 64,
      blur: 15,
    }))
    expect(usePanelStyles().panelStyle.value).toEqual({
      preset: 'custom',
      effect: 'blur',
      opacity: 64,
      blur: 15,
      saturation: 160,
    })

    // Non-matching translucent legacy payload -> custom with saturation 100
    __resetPanelStylesForTests()
    localStorage.setItem(STORAGE_KEY, JSON.stringify({
      effect: 'translucent',
      opacity: 52,
      blur: 0,
    }))
    expect(usePanelStyles().panelStyle.value).toEqual({
      preset: 'custom',
      effect: 'translucent',
      opacity: 52,
      blur: 0,
      saturation: 100,
    })
  })

  it('persists material changes synchronously and survives component scope disposal', () => {
    const initializingScope = effectScope()

    initializingScope.run(() => usePanelStyles())
    initializingScope.stop()

    usePanelStyles().updatePanelStyle({ effect: 'blur', opacity: 55, blur: 28, saturation: 185 })

    expect(JSON.parse(localStorage.getItem(STORAGE_KEY) ?? 'null')).toEqual({
      preset: 'custom',
      effect: 'blur',
      opacity: 55,
      blur: 28,
      saturation: 185,
    })
    expect(document.documentElement.style.getPropertyValue('--material-floating-filter')).toBe(
      'blur(28px) saturate(185%)',
    )
  })

  it('marks deviations as custom while preserving explicit custom preset across preset values', () => {
    const { panelStyle, applyPreset, updatePanelStyle } = usePanelStyles()

    applyPreset('frosted')
    // Manual parameter patch without preset deviates -> marks custom
    updatePanelStyle({ blur: 26 })
    expect(panelStyle.value.preset).toBe('custom')
    expect(panelStyle.value.blur).toBe(26)

    // Explicit preset: 'custom' stays custom even when values match frosted (68 / 18 / 160)
    updatePanelStyle({ preset: 'custom', opacity: 68, blur: 18, saturation: 160 })
    expect(panelStyle.value.preset).toBe('custom')
  })

  it('clamps opacity (0..100), blur (0..32px), and saturation (100..200%) in runtime updates and computePanelStyle', () => {
    const { panelStyle, updatePanelStyle } = usePanelStyles()

    updatePanelStyle({
      preset: 'custom',
      effect: 'blur',
      opacity: -10,
      blur: 45,
      saturation: 260,
    })
    expect(panelStyle.value).toEqual({
      preset: 'custom',
      effect: 'blur',
      opacity: 0,
      blur: 32,
      saturation: 200,
    })

    expect(computePanelStyle({ effect: 'blur', opacity: 140, blur: 40, saturation: 240 })).toEqual({
      backdropFilter: 'blur(32px) saturate(200%)',
      WebkitBackdropFilter: 'blur(32px) saturate(200%)',
      backgroundColor: 'rgba(var(--bg-primary-rgb, 10, 14, 20), 1)',
      borderColor: 'var(--material-glass-border)',
      boxShadow: 'var(--material-glass-highlight)',
      '--material-filter-section': 'blur(6.4px) saturate(125%)',
      '--material-filter-raised': 'blur(11.2px) saturate(140%)',
    })

    expect(computePanelStyle({ effect: 'blur', opacity: -20, blur: -4, saturation: 50 })).toEqual({
      backdropFilter: 'blur(0px) saturate(100%)',
      WebkitBackdropFilter: 'blur(0px) saturate(100%)',
      backgroundColor: 'rgba(var(--bg-primary-rgb, 10, 14, 20), 0)',
      borderColor: 'var(--material-glass-border)',
      boxShadow: 'var(--material-glass-highlight)',
      '--material-filter-section': 'none',
      '--material-filter-raised': 'none',
    })
  })

  it('renders 6 preset cards and shows saturation slider STRICTLY ONLY in custom + blur', async () => {
    const i18n = createI18n({
      legacy: false,
      locale: 'zh-CN',
      messages: { 'zh-CN': zhCN, en },
    })

    const wrapper = mount(PanelStyleControl, {
      props: {
        label: '全局材质效果',
        settings: {
          preset: 'frosted',
          effect: 'blur',
          opacity: 68,
          blur: 18,
          saturation: 160,
        },
      },
      global: { plugins: [i18n] },
    })

    // All 6 preset cards exist
    for (const id of ['solid', 'frosted', 'crystal', 'mica', 'translucent', 'custom']) {
      expect(wrapper.find(`[data-testid="panel-preset-${id}"]`).exists()).toBe(true)
    }

    // Named preset ('frosted', even though effect === 'blur') MUST NOT render custom sliders or saturation
    expect(wrapper.find('[data-testid="panel-custom-controls"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="panel-slider-saturation"]').exists()).toBe(false)

    // Custom + translucent MUST show opacity slider ONLY — NO blur and NO saturation slider
    await wrapper.setProps({
      settings: {
        preset: 'custom',
        effect: 'translucent',
        opacity: 72,
        blur: 0,
        saturation: 100,
      },
    })
    expect(wrapper.find('[data-testid="panel-custom-controls"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="panel-slider-opacity"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="panel-slider-blur"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="panel-slider-saturation"]').exists()).toBe(false)

    // Custom + blur MUST show opacity (0..100), blur (0..32), AND saturation (100..200) sliders
    await wrapper.setProps({
      settings: {
        preset: 'custom',
        effect: 'blur',
        opacity: 60,
        blur: 22,
        saturation: 175,
      },
    })
    expect(wrapper.find('[data-testid="panel-slider-opacity"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="panel-slider-blur"]').attributes('max')).toBe('32')
    const satSlider = wrapper.find('[data-testid="panel-slider-saturation"]')
    expect(satSlider.exists()).toBe(true)
    expect(satSlider.attributes('min')).toBe('100')
    expect(satSlider.attributes('max')).toBe('200')

    // 1-click restore recommended emits frosted preset
    await wrapper.get('[data-testid="panel-style-restore-recommended"]').trigger('click')
    expect(wrapper.emitted('update')?.at(-1)?.[0]).toEqual({
      preset: 'frosted',
      effect: 'blur',
      opacity: 68,
      blur: 18,
      saturation: 160,
    })

    wrapper.unmount()
  })
})
