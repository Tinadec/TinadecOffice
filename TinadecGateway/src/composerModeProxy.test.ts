import assert from 'node:assert/strict';
import { test } from 'node:test';
import { validateInteractionBody } from './mappers/interactionsMapper.js';
import { mapSession } from './mappers/sessionMapper.js';

test('interaction validation carries mode_version_id and rejects the retired agent_mode', () => {
  const valid = validateInteractionBody({
    content: '规划一下',
    client_message_id: 'cm-1',
    mode_version_id: '0d3f6a2e-0000-4000-8000-000000000001',
    dispatch_mode: 'queued',
  });
  assert.equal(valid.ok, true);
  if (valid.ok) assert.equal(valid.value.mode_version_id, '0d3f6a2e-0000-4000-8000-000000000001');

  // The six-value selector is gone from the contract: a stale client is told so
  // here rather than by a silently ignored field.
  const retired = validateInteractionBody({ content: 'x', agent_mode: 'plan', dispatch_mode: 'queued' });
  assert.equal(retired.ok, false);
  if (!retired.ok) assert.ok(retired.errors.join('; ').includes('agent_mode'));

  // Absent mode_version_id stays valid: no selection keeps the session/default resolution.
  const absent = validateInteractionBody({ content: 'x', dispatch_mode: 'parallel' });
  assert.equal(absent.ok, true);
  if (absent.ok) assert.equal(absent.value.mode_version_id, undefined);
});

test('interaction validation forwards attachment_ids without re-deciding them', () => {
  const ids = ['0d3f6a2e-0000-4000-8000-000000000001', '0d3f6a2e-0000-4000-8000-000000000002'];
  const valid = validateInteractionBody({ content: '看这个文件', client_message_id: 'cm-2', dispatch_mode: 'queued', attachment_ids: ids });
  assert.equal(valid.ok, true);
  if (valid.ok) assert.deepEqual(valid.value.attachment_ids, ids);

  // Ceiling, guid shape and which dispatch modes may carry files are Core's call.
  // Dropping or rejecting such a value here would turn a refusal the user can read
  // into a send that quietly carries nothing.
  const odd = validateInteractionBody({ content: 'x', dispatch_mode: 'queued', attachment_ids: ['not-a-guid'] });
  assert.equal(odd.ok, true);
});

test('session mapper preserves mode binding fields Core owns', () => {
  const mapped = mapSession({
    id: 's-1',
    project_id: 'p-1',
    title: 't',
    status: 'ready',
    mode: 'auto',
    mode_version_id: '0d3f6a2e-0000-4000-8000-000000000001',
    meeting_model_override: { provider_instance_id: 'prov-1', model: 'gpt-test' },
    summary: null,
    history_revision: 3,
    created_at: '2026-08-26T00:00:00Z',
    updated_at: '2026-08-26T00:00:01Z',
    lifecycle_status: 'active',
    trashed_at: null,
  });
  assert.ok(mapped);
  assert.equal(mapped.mode_version_id, '0d3f6a2e-0000-4000-8000-000000000001');
  assert.deepEqual(mapped.meeting_model_override, { provider_instance_id: 'prov-1', model: 'gpt-test' });
  assert.equal(mapped.lifecycle_status, 'active');
  assert.equal(mapped.trashed_at, null);

  const legacy = mapSession({ id: 's-2' });
  assert.ok(legacy);
  assert.equal(legacy.mode_version_id, null);
  assert.equal(legacy.meeting_model_override, null);

  // A projectless (free-conversation) session keeps "no project" as null: coercing
  // it to '' would satisfy truthiness checks but fail strict equality against a
  // real project id in the renderer.
  const projectless = mapSession({ id: 's-3', title: 'Free conversation' });
  assert.ok(projectless);
  assert.equal(projectless.project_id, null);
});
