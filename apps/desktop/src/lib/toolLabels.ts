import { useI18n } from 'vue-i18n'

/** A tool execution's status and a tool's risk in words; a value with no words is shown as it came. */
export function useToolLabels() {
  const { t, te } = useI18n()
  const label = (group: 'status' | 'risk', value: string) =>
    te(`toolPanel.${group}.${value}`) ? t(`toolPanel.${group}.${value}`) : value
  return {
    statusLabel: (status: string) => label('status', status),
    riskLabel: (risk: string) => label('risk', risk),
  }
}
