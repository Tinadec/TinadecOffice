import assert from 'node:assert/strict';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { app } from './index.js';

// CI drift gate (ponytail: zero-dep file snapshot):
//   bun test          # gateway — writes/validates TinadecGateway/tests/__snapshots__/openapi.external.json
//   dotnet test TinadecCore/TinadecCore.slnx --no-build  # core — validates /openapi/core.json
//   git diff --exit-code -- TinadecGateway/tests/__snapshots__/openapi.external.json TinadecCore/tests/__snapshots__/openapi.core.json
// non-zero exit = contract drifted. Review diff, then `git add` the snapshot(s) if intentional.

function sortKeys(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeys);
  if (value !== null && typeof value === 'object') {
    const out: Record<string, unknown> = {};
    for (const k of Object.keys(value as Record<string, unknown>).sort()) out[k] = sortKeys((value as Record<string, unknown>)[k]);
    return out;
  }
  return value;
}

test('openapi external snapshot — title contains Gateway, paths non-empty, file baseline', async () => {
  const res = await app.handle(new Request('http://localhost/docs/json'));
  assert.equal(res.status, 200, `GET /docs/json -> ${res.status}`);
  const doc = (await res.json()) as Record<string, unknown>;
  const info = doc.info as Record<string, unknown> | undefined;
  const title = String(info?.title ?? '');
  assert.match(title, /Gateway/i, `info.title should contain Gateway, got: ${title}`);
  const paths = doc.paths as Record<string, unknown> | undefined;
  assert.ok(paths && typeof paths === 'object', 'openapi.paths should be an object');
  assert.ok(Object.keys(paths).length > 0, 'openapi.paths should be non-empty');
  assert.ok(typeof doc.openapi === 'string' || typeof (doc as Record<string, unknown>).swagger === 'string', 'openapi version field should exist');

  const here = dirname(fileURLToPath(import.meta.url));
  // target per spec: TinadecGateway/tests/__snapshots__/openapi.external.json (fallback same-dir tolerated)
  const snapshotPath = join(here, '..', 'tests', '__snapshots__', 'openapi.external.json');
  mkdirSync(dirname(snapshotPath), { recursive: true });
  const normalized = sortKeys(doc);
  const serialized = JSON.stringify(normalized, null, 2) + '\n';
  if (existsSync(snapshotPath)) {
    const expected = JSON.parse(readFileSync(snapshotPath, 'utf8')) as unknown;
    assert.deepEqual(normalized, sortKeys(expected), `OpenAPI snapshot drift at ${snapshotPath}. Run bun test then git diff --exit-code to gate CI; git add the snapshot if intentional.`);
  }
  writeFileSync(snapshotPath, serialized, 'utf8');
});
