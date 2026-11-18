'use strict';

/**
 * Picking the folder that holds a Live2D model's model3.json.
 *
 * The picker is read-only and has no storage authority: the renderer hands the absolute path to
 * Core's import route, and Core copies the directory into its own storage. Only the trusted main
 * window may open a native dialog, matching the workspace folder picker.
 */
async function selectCompanionAvatarDirectory(event, { mainWindow, trusted, dialog }, title) {
  if (mainWindow?.webContents !== event.sender || !trusted(event)) {
    throw new Error('Directory selection requires the trusted main host page.');
  }
  const named = typeof title === 'string' && title.trim() && title.length <= 200 ? { title } : {};
  const result = await dialog.showOpenDialog(mainWindow, { properties: ['openDirectory'], ...named });
  if (result.canceled) return null;
  return result.filePaths[0] ?? null;
}

module.exports = { selectCompanionAvatarDirectory };
