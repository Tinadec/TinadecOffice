import type { CenterDiagnosticDto } from '@/api'

/** The subset of vue-i18n's `t` these labels need; keeps the module free of the i18n instance. */
export type Translate = (key: string, named?: Record<string, unknown>) => string

/** Provider/model status words, shared by the Model Center and the Agent Center that reads the same catalog. */
export function providerStatusLabel(t: Translate, status: string): string {
  if (status === 'ready') return t('settings.statusReady')
  if (status === 'needs_key') return t('settings.statusNeedsKey')
  if (status === 'disabled') return t('settings.statusDisabled')
  if (status === 'cooldown') return t('settings.statusCooldown')
  if (status === 'not_configured' || !status) return t('settings.statusNotConfigured')
  if (status === 'configured') return t('settings.statusConfigured')
  if (status === 'found') return t('settings.statusFound')
  if (status === 'missing') return t('settings.statusMissing')
  if (status === 'missing_provider') return t('settings.statusMissingProvider')
  if (status === 'unknown') return t('settings.statusUnknown')
  return status
}

export function centerDiagnosticLabel(t: Translate, diagnostic: CenterDiagnosticDto): string {
  if (diagnostic.code === 'CORE_CAPABILITY_UNAVAILABLE') {
    return t('settings.optionalCapabilityUnavailable', {
      source: diagnostic.source ?? 'Core',
      status: diagnostic.status ?? '—',
    })
  }
  if (diagnostic.code === 'LEGACY_SHARED_ROUTE') {
    return t('settings.sharedRouteDiagnostic', {
      purpose: diagnostic.route_purpose ?? '—',
      count: diagnostic.agent_ids?.length ?? 0,
    })
  }
  return diagnostic.message
}
