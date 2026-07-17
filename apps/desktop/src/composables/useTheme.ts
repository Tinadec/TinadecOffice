import { useStorage } from '@vueuse/core'
import { ref, watch, type Ref } from 'vue'

export type Theme = 'dark' | 'light' | 'system'

export interface AccentColor {
  key: string
  labelKey: string
  dark: {
    accentPrimary: string
    accentBrand: string
    textBrand: string
    borderInputFocus: string
    shadowFocus: string
  }
  light: {
    accentPrimary: string
    accentBrand: string
    textBrand: string
    borderInputFocus: string
    shadowFocus: string
  }
}

export const ACCENT_COLORS: AccentColor[] = [
  {
    key: 'blue',
    labelKey: 'accentColors.blue',
    dark: {
      accentPrimary: '#58a6ff',
      accentBrand: '#58a6ff',
      textBrand: '#58a6ff',
      borderInputFocus: '#58a6ff',
      shadowFocus: '0 0 0 3px rgba(88, 166, 255, 0.3)',
    },
    light: {
      accentPrimary: '#0969da',
      accentBrand: '#1f6feb',
      textBrand: '#17484d',
      borderInputFocus: '#2e7d76',
      shadowFocus: '0 0 0 3px rgba(46, 125, 118, 0.14)',
    },
  },
  {
    key: 'green',
    labelKey: 'accentColors.green',
    dark: {
      accentPrimary: '#3fb950',
      accentBrand: '#3fb950',
      textBrand: '#3fb950',
      borderInputFocus: '#3fb950',
      shadowFocus: '0 0 0 3px rgba(63, 185, 80, 0.3)',
    },
    light: {
      accentPrimary: '#1a7f37',
      accentBrand: '#1a7f37',
      textBrand: '#1a7f37',
      borderInputFocus: '#1a7f37',
      shadowFocus: '0 0 0 3px rgba(26, 127, 55, 0.14)',
    },
  },
  {
    key: 'purple',
    labelKey: 'accentColors.purple',
    dark: {
      accentPrimary: '#bc8cff',
      accentBrand: '#bc8cff',
      textBrand: '#bc8cff',
      borderInputFocus: '#bc8cff',
      shadowFocus: '0 0 0 3px rgba(188, 140, 255, 0.3)',
    },
    light: {
      accentPrimary: '#8250df',
      accentBrand: '#8250df',
      textBrand: '#6e40c9',
      borderInputFocus: '#8250df',
      shadowFocus: '0 0 0 3px rgba(130, 80, 223, 0.14)',
    },
  },
  {
    key: 'orange',
    labelKey: 'accentColors.orange',
    dark: {
      accentPrimary: '#f0883e',
      accentBrand: '#f0883e',
      textBrand: '#f0883e',
      borderInputFocus: '#f0883e',
      shadowFocus: '0 0 0 3px rgba(240, 136, 62, 0.3)',
    },
    light: {
      accentPrimary: '#bc4c00',
      accentBrand: '#bc4c00',
      textBrand: '#953800',
      borderInputFocus: '#bc4c00',
      shadowFocus: '0 0 0 3px rgba(188, 76, 0, 0.14)',
    },
  },
  {
    key: 'pink',
    labelKey: 'accentColors.pink',
    dark: {
      accentPrimary: '#f778ba',
      accentBrand: '#f778ba',
      textBrand: '#f778ba',
      borderInputFocus: '#f778ba',
      shadowFocus: '0 0 0 3px rgba(247, 120, 186, 0.3)',
    },
    light: {
      accentPrimary: '#bf3989',
      accentBrand: '#bf3989',
      textBrand: '#953074',
      borderInputFocus: '#bf3989',
      shadowFocus: '0 0 0 3px rgba(191, 57, 137, 0.14)',
    },
  },
  {
    key: 'red',
    labelKey: 'accentColors.red',
    dark: {
      accentPrimary: '#f85149',
      accentBrand: '#f85149',
      textBrand: '#f85149',
      borderInputFocus: '#f85149',
      shadowFocus: '0 0 0 3px rgba(248, 81, 73, 0.3)',
    },
    light: {
      accentPrimary: '#cf222e',
      accentBrand: '#cf222e',
      textBrand: '#a40e26',
      borderInputFocus: '#cf222e',
      shadowFocus: '0 0 0 3px rgba(207, 34, 46, 0.14)',
    },
  },
  {
    key: 'cyan',
    labelKey: 'accentColors.cyan',
    dark: {
      accentPrimary: '#56d4dd',
      accentBrand: '#56d4dd',
      textBrand: '#56d4dd',
      borderInputFocus: '#56d4dd',
      shadowFocus: '0 0 0 3px rgba(86, 212, 221, 0.3)',
    },
    light: {
      accentPrimary: '#087990',
      accentBrand: '#087990',
      textBrand: '#065975',
      borderInputFocus: '#087990',
      shadowFocus: '0 0 0 3px rgba(8, 121, 144, 0.14)',
    },
  },
  {
    key: 'yellow',
    labelKey: 'accentColors.yellow',
    dark: {
      accentPrimary: '#d29922',
      accentBrand: '#d29922',
      textBrand: '#d29922',
      borderInputFocus: '#d29922',
      shadowFocus: '0 0 0 3px rgba(210, 153, 34, 0.3)',
    },
    light: {
      accentPrimary: '#9a6700',
      accentBrand: '#9a6700',
      textBrand: '#7c5200',
      borderInputFocus: '#9a6700',
      shadowFocus: '0 0 0 3px rgba(154, 103, 0, 0.14)',
    },
  },
]

// 使用 ref 延迟初始化，避免在模块加载时访问 localStorage
let stored: Ref<Theme> | null = null
let storedAccentColor: Ref<string> | null = null

function getStoredTheme(): Ref<Theme> {
  if (!stored) {
    stored = useStorage<Theme>('tinadec-theme', 'dark')
  }
  return stored
}

function getStoredAccentColor(): Ref<string> {
  if (!storedAccentColor) {
    storedAccentColor = useStorage<string>('tinadec-accent-color', 'blue')
  }
  return storedAccentColor
}

function getAccentColor(key: string): AccentColor {
  return ACCENT_COLORS.find((c) => c.key === key) ?? ACCENT_COLORS[0]
}

function applyTheme(theme: Theme) {
  let resolved: 'dark' | 'light'
  if (theme === 'system') {
    resolved = window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  } else {
    resolved = theme
  }
  document.documentElement.setAttribute('data-theme', resolved)
}

function applyAccentColor(colorKey: string) {
  const color = getAccentColor(colorKey)
  const resolved = document.documentElement.getAttribute('data-theme') as 'dark' | 'light' ?? 'dark'
  const vars = resolved === 'dark' ? color.dark : color.light

  const root = document.documentElement
  root.style.setProperty('--accent-primary', vars.accentPrimary)
  root.style.setProperty('--accent-brand', vars.accentBrand)
  root.style.setProperty('--text-brand', vars.textBrand)
  root.style.setProperty('--border-input-focus', vars.borderInputFocus)
  root.style.setProperty('--shadow-focus', vars.shadowFocus)
}

export function useTheme() {
  const themeRef = getStoredTheme()
  const accentColorRef = getStoredAccentColor()

  function applyInitialTheme() {
    applyTheme(themeRef.value)
    applyAccentColor(accentColorRef.value)
  }

  // 立即应用主题
  applyInitialTheme()

  watch(themeRef, (val) => {
    applyTheme(val)
    applyAccentColor(accentColorRef.value)
  })

  watch(accentColorRef, (val) => {
    applyAccentColor(val)
  })

  window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    if (themeRef.value === 'system') {
      applyTheme('system')
      applyAccentColor(accentColorRef.value)
    }
  })

  return {
    theme: themeRef,
    setTheme: (t: Theme) => {
      themeRef.value = t
    },
    accentColor: accentColorRef,
    setAccentColor: (key: string) => {
      accentColorRef.value = key
    },
    accentColors: ACCENT_COLORS,
    applyInitialTheme,
  }
}
