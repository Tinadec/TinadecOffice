const { readFileSync } = require('node:fs');
const path = require('node:path');

/**
 * Reads the receipt Core writes when it cannot finish bootstrap.
 *
 * `storage.toml` / `logging.toml` are read before Core binds a port, so that failure can never show up
 * in /api/v1/readiness — without it the window only said "the local services are temporarily
 * unavailable" while the real cause sat in a log file nobody opens.
 *
 * Main passes the document and the code to the renderer, which words the sentence in the user's
 * language. Document contents never come from here: the receipt carries codes and file ids only.
 */
const MAX_MESSAGE = 400;

/** Core ids are "storage" and "logging"; the window should name the file a person can open. */
function documentFile(document) {
  if (typeof document !== 'string' || document.trim() === '') return null;
  return document.endsWith('.toml') ? document : document + '.toml';
}

function readStartupFailure(userRoot, options = {}) {
  const read = options.readFileSync ?? readFileSync;
  if (typeof userRoot !== 'string' || userRoot.trim() === '') return null;
  const file = path.join(userRoot, 'state', 'startup-failure.json');
  let text;
  try {
    text = read(file, 'utf8');
  } catch {
    return null; // no receipt: ordinary startup failure, say nothing extra
  }
  let parsed;
  try {
    parsed = JSON.parse(String(text));
  } catch {
    return null; // a truncated receipt must not mask the startup failure it was meant to explain
  }
  if (!parsed || typeof parsed.code !== 'string' || parsed.code.trim() === '') return null;
  return {
    code: parsed.code.slice(0, 60),
    document: documentFile(parsed.document),
    message: typeof parsed.message === 'string' ? parsed.message.slice(0, MAX_MESSAGE) : null,
    at: typeof parsed.at === 'string' ? parsed.at : null,
  };
}

module.exports = { readStartupFailure, documentFile };
