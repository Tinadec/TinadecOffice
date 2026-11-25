import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';
import { app } from './index.js';

function sortKeysRecursively(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(sortKeysRecursively);
  if (value && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value as Record<string, unknown>)
        .sort()
        .map((key) => [key, sortKeysRecursively((value as Record<string, unknown>)[key])]),
    );
  }
  return value;
}

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
  assert.ok('ProjectBoardTask' in schemas, 'ProjectBoardTask schema must exist');
  assert.ok('ProjectBoardSettings' in schemas, 'ProjectBoardSettings schema must exist');
  assert.ok('ProjectBoardSummary' in schemas, 'ProjectBoardSummary schema must exist');
  assert.ok('ProjectBoardView' in schemas, 'ProjectBoardView schema must exist');

  // Approval redesign schemas and paths (preserved /gates from organizationRoutes + policy, decision, batch-decision).
  for (const name of [
    'ApprovalPolicySummary',
    'ApprovalRememberRuleInput',
    'ApprovalDecisionRequest',
    'ApprovalDecisionResponse',
    'BatchApprovalDecisionRequest',
    'BatchApprovalDecisionItem',
    'BatchApprovalDecisionResponse',
  ]) {
    assert.ok(name in schemas, `${name} schema must exist`);
  }
  assert.deepEqual(valueAt(schemas, 'ApprovalPolicySummary', 'required'), [
    'policy_version',
    'enabled',
    'risk_max',
    'delegated_risk_max',
    'max_per_run',
    'release_read_only_in_ask_mode',
    'human_only_tools',
    'delegation_blocked_tools',
  ]);
  assert.equal(valueAt(schemas, 'ApprovalPolicySummary', 'properties', 'max_per_run', 'type'), 'integer');
  assert.equal(valueAt(schemas, 'ApprovalRememberRuleInput', 'additionalProperties'), false);
  assert.deepEqual(valueAt(schemas, 'ApprovalRememberRuleInput', 'properties', 'kind', 'enum'), ['command_prefix', 'delegate_tool']);
  assert.equal(valueAt(schemas, 'ApprovalDecisionRequest', 'additionalProperties'), false);
  assert.equal(valueAt(schemas, 'BatchApprovalDecisionRequest', 'additionalProperties'), false);
  assert.equal(valueAt(schemas, 'BatchApprovalDecisionRequest', 'properties', 'approval_ids', 'minItems'), 1);
  assert.equal(valueAt(schemas, 'BatchApprovalDecisionRequest', 'properties', 'approval_ids', 'maxItems'), 50);

  assert.ok(valueAt(doc, 'paths', '/api/v1/approvals/policy', 'get'), 'GET /api/v1/approvals/policy must exist');
  assert.ok(valueAt(doc, 'paths', '/api/v1/approvals/batch-decision', 'post'), 'POST /api/v1/approvals/batch-decision must exist');
  assert.ok(valueAt(doc, 'paths', '/api/v1/approvals/{approvalId}/decision', 'post'), 'POST /api/v1/approvals/{approvalId}/decision must exist');
  assert.ok(valueAt(doc, 'paths', '/api/v1/approvals/{approvalId}/gates', 'get'), 'GET /api/v1/approvals/{approvalId}/gates must remain registered');

  const snapshotPath = resolve(import.meta.dir, '../tests/__snapshots__/openapi.external.json');
  const committedSnapshot = readFileSync(snapshotPath, 'utf8').replace(/\r\n/g, '\n');
  const liveSerialized = JSON.stringify(sortKeysRecursively(doc), null, 2) + '\n';
  assert.equal(liveSerialized, committedSnapshot, 'openapi.external.json snapshot must match sorted /docs/json');
});
