import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useBackground } from '@/composables/useBackground'
import { usePanelStyles } from '@/composables/usePanelStyles'
import { useTheme } from '@/composables/useTheme'

export function useAppearanceSettings() {
  const { t } = useI18n()
  const { theme, setTheme, accentColor, setAccentColor, accentColors } = useTheme()
  const { settings: backgroundSettings, setBackgroundType, setBackgroundSource, setBackgroundOpacity, setBackgroundBlur, setBackgroundSize, setBackgroundPosition, setBackgroundRepeat, selectFile: selectBackgroundFile, resetBackground } = useBackground()
  const { panelStyle, updatePanelStyle, resetPanelStyle } = usePanelStyles()
  const backgroundSource = computed({
    get: () => backgroundSettings.value.source,
    set: (value: string) => setBackgroundSource(value),
  })

  function changeTheme(newTheme: 'dark' | 'light' | 'system') {
    setTheme(newTheme)
    window.tinadec?.broadcastTheme?.(newTheme, accentColor.value)
  }

  function changeAccentColor(key: string) {
    setAccentColor(key)
    window.tinadec?.broadcastTheme?.(theme.value, key)
  }

  function setBackgroundSizeFromEvent(event: Event) {
    if (!(event.target instanceof HTMLSelectElement)) return
    const { value } = event.target
    if (value === 'cover' || value === 'contain' || value === 'auto') setBackgroundSize(value)
  }

  function setBackgroundPositionFromEvent(event: Event) {
    if (!(event.target instanceof HTMLSelectElement)) return
    const { value } = event.target
    if (value === 'center' || value === 'top' || value === 'bottom' || value === 'left' || value === 'right') setBackgroundPosition(value)
  }

  function setBackgroundRepeatFromEvent(event: Event) {
    if (!(event.target instanceof HTMLSelectElement)) return
    const { value } = event.target
    if (value === 'no-repeat' || value === 'repeat' || value === 'repeat-x' || value === 'repeat-y') setBackgroundRepeat(value)
  }

  function setBackgroundOpacityFromEvent(event: Event) {
    if (event.target instanceof HTMLInputElement) setBackgroundOpacity(Number.parseInt(event.target.value, 10))
  }

  function setBackgroundBlurFromEvent(event: Event) {
    if (event.target instanceof HTMLInputElement) setBackgroundBlur(Number.parseInt(event.target.value, 10))
  }

  return { accentColor, accentColors, backgroundSettings, backgroundSource, changeAccentColor, changeTheme, panelStyle, resetBackground, resetPanelStyle, selectBackgroundFile, setBackgroundBlurFromEvent, setBackgroundOpacityFromEvent, setBackgroundPositionFromEvent, setBackgroundRepeatFromEvent, setBackgroundSizeFromEvent, setBackgroundType, t, theme, updatePanelStyle }
}
