import assert from 'node:assert/strict';
import test from 'node:test';
import { app } from './index.js';

function valueAt(root: unknown, ...path: string[]): unknown {
  let current = root;
  for (const segment of path) {
    assert.ok(current !== null && typeof current === 'object' && !Array.isArray(current), `Expected object at ${path.join('.')}`);
    current = (current as Record<string, unknown>)[segment];
  }
  return current;
}

function objectAt(root: unknown, ...path: string[]): Record<string, unknown> {
  const value = valueAt(root, ...path);
  assert.ok(value !== null && typeof value === 'object' && !Array.isArray(value), `Expected object at ${path.join('.')}`);
  return value as Record<string, unknown>;
}

test('openapi external contract — serves valid document with host security scheme and no storage-path leak', async () => {
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

  // Security boundary: default host-control auth everywhere except explicit public health/challenge routes.
  assert.deepEqual(doc.security, [{ TinadecHostControl: [] }]);
  assert.equal(valueAt(doc, 'components', 'securitySchemes', 'TinadecHostControl', 'name'), 'X-Tinadec-Host-Control');
  assert.deepEqual(valueAt(doc, 'paths', '/api/v1/health', 'get', 'security'), []);
  assert.deepEqual(valueAt(doc, 'paths', '/api/v1/host-challenge', 'get', 'security'), []);

  // Storage-path leak guard: attachment schemas address bytes by id, never by data-root path.
  const schemas = objectAt(doc, 'components', 'schemas');
  for (const name of ['Message', 'MessageAttachment', 'MessageAttachmentSummary']) {
    if (name in schemas) {
      assert.ok(!JSON.stringify(schemas[name]).includes('content_reference'), `${name} must not name a storage path`);
    }
  }

  assert.ok('ContextPolicyParamsOverride' in schemas, 'ContextPolicyParamsOverride schema must exist');
  assert.equal(valueAt(schemas, 'ContextPolicyParamsOverride', 'additionalProperties'), false);
  assert.equal(valueAt(schemas, 'SpaceOptions', 'additionalProperties'), false);
  assert.deepEqual(valueAt(schemas, 'SpaceOptions', 'properties', 'context_strategy', 'enum'), ['direct', 'managed', 'deep_curator']);
});
