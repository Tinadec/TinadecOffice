import { describe, expect, it } from 'vitest';
import { basenameFromPath, toneForStatus } from './format';

describe('format helpers', () => {
  it('extracts a project name from Windows paths', () => {
    expect(basenameFromPath('D:\\github\\TinadecCode\\')).toBe('TinadecCode');
  });

  it('maps pending work to a warning tone', () => {
    expect(toneForStatus('pending')).toBe('warn');
  });

  it('maps runtime readiness statuses to existing tones', () => {
    expect(toneForStatus('ready')).toBe('ok');
    expect(toneForStatus('warning')).toBe('warn');
    expect(toneForStatus('blocked')).toBe('danger');
  });
});
