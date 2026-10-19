import assert from 'node:assert/strict';
import test from 'node:test';
import { developmentCdpPort, developmentEnvironment } from './developmentEnvironment.mjs';

test('Vite plugins cannot inherit or override the private development host credential', () => {
  const environment = { PATH: 'fixture-path', TINADEC_HOST_CONTROL_TOKEN: 'private', ELECTRON_RUN_AS_NODE: '1', ELECTRON_NO_ATTACH_CONSOLE: '1' };
  assert.deepEqual(developmentEnvironment({ TINADEC_HOST_CONTROL_TOKEN: 'override', VITE_DEV_SERVER_URL: 'http://127.0.0.1:5173' }, false, environment),
    { PATH: 'fixture-path', VITE_DEV_SERVER_URL: 'http://127.0.0.1:5173' });
  assert.equal(environment.TINADEC_HOST_CONTROL_TOKEN, 'private');
});

test('only the explicit trusted Electron launch retains the shared credential', () => {
  const result = developmentEnvironment({ VITE_DEV_SERVER_URL: 'http://127.0.0.1:5173' }, true,
    { PATH: 'fixture-path', TINADEC_HOST_CONTROL_TOKEN: 'private', ELECTRON_RUN_AS_NODE: '1' });
  assert.equal(result.TINADEC_HOST_CONTROL_TOKEN, 'private');
  assert.equal(result.ELECTRON_RUN_AS_NODE, undefined);
});

test('Electron CDP stays closed unless a valid explicit port is requested', () => {
  assert.equal(developmentCdpPort({}), null);
  assert.equal(developmentCdpPort({ TINADEC_DEV_CDP_PORT: '9222' }), 9222);
  assert.throws(() => developmentCdpPort({ TINADEC_DEV_CDP_PORT: '80' }), /between 1024 and 65535/);
  assert.throws(() => developmentCdpPort({ TINADEC_DEV_CDP_PORT: '9222; calc' }), /must be an integer/);
});
