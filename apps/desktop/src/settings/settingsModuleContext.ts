import { inject, provide, type InjectionKey } from 'vue'

export type SettingsModuleContext = Record<string, any>

const SETTINGS_MODULE_CONTEXT: InjectionKey<SettingsModuleContext> = Symbol('settings-module-context')

export function provideSettingsModuleContext(context: SettingsModuleContext): void {
  provide(SETTINGS_MODULE_CONTEXT, context)
}

export function useSettingsModuleContext(): SettingsModuleContext {
  const context = inject(SETTINGS_MODULE_CONTEXT)
  if (!context) throw new Error('Settings module context was not provided.')
  return context
}
