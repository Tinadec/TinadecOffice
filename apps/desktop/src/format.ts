import i18n from './i18n'

export function basenameFromPath(path: string): string {
  const trimmed = path.trim().replace(/[\\/]+$/, '');
  const parts = trimmed.split(/[\\/]/);
  return parts.at(-1) || trimmed || i18n.global.t('home.untitledProject');
}

export function toneForStatus(status: string): 'ok' | 'warn' | 'danger' | 'neutral' {
  if (status === 'ok' || status === 'approved' || status === 'active' || status === 'ready') return 'ok';
  if (status === 'pending' || status === 'warning' || status === 'degraded' || status === 'unavailable') return 'warn';
  if (status === 'error' || status === 'rejected' || status === 'missing' || status === 'blocked') return 'danger';
  return 'neutral';
}
