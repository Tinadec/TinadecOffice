export function basenameFromPath(path: string): string {
  const trimmed = path.trim().replace(/[\\/]+$/, '');
  const parts = trimmed.split(/[\\/]/);
  return parts.at(-1) || trimmed || 'Project';
}

export function toneForStatus(status: string): 'ok' | 'warn' | 'danger' | 'neutral' {
  if (status === 'ok' || status === 'approved' || status === 'active' || status === 'ready') return 'ok';
  if (status === 'pending' || status === 'warning') return 'warn';
  if (status === 'error' || status === 'rejected' || status === 'missing' || status === 'blocked') return 'danger';
  return 'neutral';
}
