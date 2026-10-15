import { useI18n } from 'vue-i18n'

type Group = 'run' | 'tool' | 'risk' | 'severity' | 'approval'

/**
 * Status-like values in words: a run's or lane's status (the twelve run states and the lane
 * states), a tool execution's status, a tool's or call's risk, a finding's severity, an
 * approval's state. A value this build has no words for is shown as it came.
 */
export function useStatusLabels() {
  const { t, te } = useI18n()
  const label = (group: Group) => (value: string) =>
    te(`statusLabels.${group}.${value}`) ? t(`statusLabels.${group}.${value}`) : value
  return {
    runStatusLabel: label('run'),
    statusLabel: label('tool'),
    riskLabel: label('risk'),
    severityLabel: label('severity'),
    approvalStatusLabel: label('approval'),
  }
}
