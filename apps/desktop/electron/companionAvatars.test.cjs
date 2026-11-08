const { test } = require('node:test');
const assert = require('node:assert/strict');
const { selectCompanionAvatarDirectory } = require('./companionAvatars.cjs');

test('avatar folder picker returns one directory and null on cancel, only for the trusted main sender', async () => {
  const sender = {};
  const window = { webContents: sender };
  const event = { sender };
  let calls = 0;
  let title;
  const options = {
    mainWindow: window,
    trusted: () => true,
    dialog: {
      async showOpenDialog(parent, config) {
        calls++;
        assert.equal(parent, window);
        assert.deepEqual(config.properties, ['openDirectory']);
        title = config.title;
        return { canceled: false, filePaths: ['C:/models/haru'] };
      },
    },
  };

  // The title is the renderer's translated wording; the main process spells out no language itself.
  assert.equal(await selectCompanionAvatarDirectory(event, options, 'Pick model folder'), 'C:/models/haru');
  assert.equal(title, 'Pick model folder');
  assert.equal(await selectCompanionAvatarDirectory(event, options), 'C:/models/haru');
  assert.equal(title, undefined);

  await assert.rejects(selectCompanionAvatarDirectory({ sender: {} }, options), /trusted main/);
  await assert.rejects(
    selectCompanionAvatarDirectory(event, { ...options, trusted: () => false }),
    /trusted main/,
  );
  assert.equal(calls, 2);

  assert.equal(
    await selectCompanionAvatarDirectory(event, {
      ...options,
      dialog: { showOpenDialog: async () => ({ canceled: true, filePaths: ['ignored'] }) },
    }),
    null,
  );
});
