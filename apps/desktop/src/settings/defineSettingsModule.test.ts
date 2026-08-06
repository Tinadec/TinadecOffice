// @vitest-environment happy-dom

import { defineComponent, h } from 'vue'
import { mount } from '@vue/test-utils'
import { createI18n } from 'vue-i18n'
import { describe, expect, it, vi } from 'vitest'
import SettingsAppearanceModule from './modules/SettingsAppearanceModule.vue'
import { provideSettingsModuleContext } from './settingsModuleContext'

describe('defineSettingsModule', () => {
  it('registers shared controls and icons as Vue components', () => {
    const context = {
      t: (key: string) => key,
      theme: 'dark',
      accentColor: 'blue',
      accentColors: [],
      changeTheme: vi.fn(),
      changeAccentColor: vi.fn(),
      panelStyle: { effect: 'opaque', opacity: 80, blur: 8 },
      updatePanelStyle: vi.fn(),
      resetPanelStyle: vi.fn(),
      backgroundSettings: { type: 'none', source: '', opacity: 100, blur: 0 },
      setBackgroundType: vi.fn(),
      setBackgroundOpacity: vi.fn(),
      setBackgroundBlur: vi.fn(),
      setBackgroundSize: vi.fn(),
      setBackgroundPosition: vi.fn(),
      setBackgroundRepeat: vi.fn(),
      selectBackgroundFile: vi.fn(),
      resetBackground: vi.fn(),
      backgroundSource: '',
    }
    const Host = defineComponent({
      setup() {
        provideSettingsModuleContext(context)
        return () => h(SettingsAppearanceModule)
      },
    })
    const i18n = createI18n({ legacy: false, locale: 'en', messages: { en: {} } })
    const wrapper = mount(Host, { global: { plugins: [i18n] } })

    expect(wrapper.find('.panel-style-control').exists()).toBe(true)
    expect(wrapper.findAll('.effect-option')).toHaveLength(3)
    expect(wrapper.findAll('svg')).not.toHaveLength(0)
    expect(wrapper.element.querySelector('panelstylecontrol, uibutton, moon, check')).toBeNull()
  })
})
