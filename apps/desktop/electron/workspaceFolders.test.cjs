const { test } = require('node:test');
const assert = require('node:assert/strict');
const { selectWorkspaceFolders } = require('./workspaceFolders.cjs');
test('folder picker returns path arrays and cancellation, only for the trusted main sender', async () => {
  const sender = {}; const window = { webContents: sender }; const event = { sender }; let calls = 0; let title;
  const options = { mainWindow: window, trusted: () => true, dialog: { async showOpenDialog(parent, config) { calls++; assert.equal(parent, window); assert.deepEqual(config.properties, ['openDirectory', 'multiSelections']); title = config.title; return { canceled: false, filePaths: ['C:/a', 'C:/b'] }; } } };
  // The title is the renderer's translated wording; the main process spells out no language itself.
  assert.deepEqual(await selectWorkspaceFolders(event, options, 'Pick folders'), ['C:/a', 'C:/b']); assert.equal(title, 'Pick folders');
  assert.deepEqual(await selectWorkspaceFolders(event, options), ['C:/a', 'C:/b']); assert.equal(title, undefined);
  await assert.rejects(selectWorkspaceFolders({ sender: {} }, options), /trusted main/);
  await assert.rejects(selectWorkspaceFolders(event, { ...options, trusted: () => false }), /trusted main/); assert.equal(calls, 2);
  assert.deepEqual(await selectWorkspaceFolders(event, { ...options, dialog: { showOpenDialog: async () => ({ canceled: true, filePaths: ['ignored'] }) } }), []);
});
