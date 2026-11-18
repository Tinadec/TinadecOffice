import { readFileSync, readdirSync } from 'node:fs';
import { join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const srcRoot = fileURLToPath(new URL('.', import.meta.url));

function sourceFiles(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const full = join(directory, entry.name);
    if (entry.isDirectory()) return entry.name === 'generated' ? [] : sourceFiles(full);
    // The generated OpenAPI types legitimately name every route; tests name the forbidden ones.
    if (!/\.(ts|vue)$/.test(entry.name) || entry.name.endsWith('.test.ts')) return [];
    return [full];
  });
}

// The session token never reaches the renderer: auth.cjs keeps it in the main process and the
// renderer only gets the resolved identity over IPC. A renderer that called /auth/* directly
// would also be sending a request the host gate now refuses, since the renderer has no private
// host credential. This pins the boundary at the source level because there is no runtime path
// to exercise: the absence of a call is the invariant.
describe('renderer auth boundary', () => {
  it('never names an /auth/ route outside the generated contract', () => {
    const offenders = sourceFiles(srcRoot)
      .filter((file) => /['"`]\/auth\//.test(readFileSync(file, 'utf8')))
      .map((file) => relative(srcRoot, file));
    expect(offenders).toEqual([]);
  });
});