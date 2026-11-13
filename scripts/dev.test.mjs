import test from 'node:test';
import assert from 'node:assert/strict';
import { DEVELOPMENT_DATA_ROOT, developmentHostCommands } from './dev.mjs';

test('development launch hosts share one private credential without mutating the parent environment', () => {
  const parent = { PATH: 'tool-path', TINADEC_HOST_CONTROL_TOKEN: 'old' };
  const commands = developmentHostCommands(parent, 'new-launch-credential');
  assert.deepEqual(commands.map(command => command.name), ['core', 'gateway', 'desktop']);
  assert.ok(commands.every(command => command.env.TINADEC_HOST_CONTROL_TOKEN === 'new-launch-credential'));
  commands[0].env.PATH = 'changed';
  assert.equal(commands[1].env.PATH, 'tool-path');
  assert.deepEqual(parent, { PATH: 'tool-path', TINADEC_HOST_CONTROL_TOKEN: 'old' });
});

test('development hosts open an isolated data root instead of the installed application root', () => {
  const commands = developmentHostCommands({ PATH: 'tool-path' }, 'launch-credential');
  assert.ok(commands.every(command => command.env.TINADEC_HOME === DEVELOPMENT_DATA_ROOT));
  assert.ok(DEVELOPMENT_DATA_ROOT.endsWith('.tinadec-dev'));
  // An explicit root still wins: developers prepare fixtures and point the launcher at them.
  const prepared = developmentHostCommands({ TINADEC_HOME: '/tmp/prepared-root' }, 'launch-credential');
  assert.ok(prepared.every(command => command.env.TINADEC_HOME === '/tmp/prepared-root'));
});

test('independent development launches use distinct unpredictable credentials', () => {
  const first = developmentHostCommands({})[0].env.TINADEC_HOST_CONTROL_TOKEN;
  const second = developmentHostCommands({})[0].env.TINADEC_HOST_CONTROL_TOKEN;
  assert.match(first, /^[a-zA-Z0-9_-]{43}$/);
  assert.notEqual(first, second);
});
