import assert from 'node:assert/strict';
import test from 'node:test';
import { listCodeToolSpecs } from './codeTools.js';

test('project template specs include language support and read-only access', () => {
  const specs = listCodeToolSpecs();
  const templates = specs.find((tool) => tool.id === 'project_templates');
  assert.equal(templates?.requires_approval, false);
  assert.ok(templates?.language_support?.includes('rust'));
  assert.ok(templates?.language_support?.includes('bun'));

  const scaffold = specs.find((tool) => tool.id === 'project_template_scaffold');
  assert.equal(scaffold?.requires_approval, true);
  assert.ok(scaffold?.language_support?.includes('rust'));
});

test('all git tools require approval', () => {
  const specs = listCodeToolSpecs();
  const gitTools = specs.filter((tool) => tool.category === 'git');
  assert.ok(gitTools.length > 0);
  for (const tool of gitTools) {
    assert.equal(tool.requires_approval, true, `Tool ${tool.id} should require approval`);
  }
});

test('read-only primitive tools do not require approval', () => {
  const specs = listCodeToolSpecs();
  const primitiveTools = specs.filter((tool) => tool.category === 'primitive');
  assert.ok(primitiveTools.length > 0);
  for (const tool of primitiveTools) {
    assert.equal(tool.requires_approval, false, `Tool ${tool.id} should not require approval`);
  }
});
