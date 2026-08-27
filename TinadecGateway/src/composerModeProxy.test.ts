import assert from 'node:assert/strict';
import { test } from 'node:test';
import { validateInteractionBody, AGENT_MODES } from './mappers/interactionsMapper.js';
import { mapSession } from './mappers/sessionMapper.js';

test('interaction validation keeps composer agent_mode and rejects unknown values', () => {
  const valid = validateInteractionBody({
    content: '规划一下',
    client_message_id: 'cm-1',
    agent_mode: 'plan',
    dispatch_mode: 'queued',
  });
  assert.equal(valid.ok, true);
  if (valid.ok) assert.equal(valid.value.agent_mode, 'plan');

  const invalid = validateInteractionBody({ content: 'x', agent_mode: 'nonsense', dispatch_mode: 'queued' });
  assert.equal(invalid.ok, false);
  if (!invalid.ok) assert.ok(invalid.errors.join('; ').includes('agent_mode'));

  // Absent agent_mode stays valid: no selection keeps the legacy space admission.
  const absent = validateInteractionBody({ content: 'x', dispatch_mode: 'parallel' });
  assert.equal(absent.ok, true);
  if (absent.ok) assert.equal(absent.value.agent_mode, undefined);

  assert.deepEqual([...AGENT_MODES], ['plan', 'spec', 'ask', 'vibe', 'auto', 'agent']);
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
});
