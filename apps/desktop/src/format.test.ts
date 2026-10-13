import { describe, expect, it } from 'vitest';
import { basenameFromPath } from './format'

describe('format helpers', () => {
  it('extracts a project name from Windows paths', () => {
    expect(basenameFromPath('D:\\github\\TinadecOffice\\')).toBe('TinadecOffice');
  });

});
