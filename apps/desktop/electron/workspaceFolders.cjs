'use strict';

/**
 * Picking is read-only. The Core workspace-confirmation action grants and initializes storage.
 * The title is the renderer's translated wording; without one the OS default stands in.
 */
async function selectWorkspaceFolders(event, { mainWindow, trusted, dialog }, title) {
  if (mainWindow?.webContents !== event.sender || !trusted(event)) throw new Error('Directory selection requires the trusted main host page.');
  const named = typeof title === 'string' && title.trim() && title.length <= 200 ? { title } : {};
  const result = await dialog.showOpenDialog(mainWindow, { properties: ['openDirectory', 'multiSelections'], ...named });
  return result.canceled ? [] : result.filePaths;
}
module.exports = { selectWorkspaceFolders };
