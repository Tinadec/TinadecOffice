const fs = require('node:fs');
const path = require('node:path');
const { parse } = require('smol-toml');
const MAX_FILE_BYTES = 10 * 1024 * 1024;
const MAX_TOTAL_BYTES = 200 * 1024 * 1024;
// The total-budget check walks the whole log tree (readdir + stat per file). Running it on
// every stdout chunk blocked the main process event loop in proportion to log volume. Check
// it instead after ~1 MiB appended or 5 s, whichever comes first; per-file rotation below is
// still enforced on every write. `budgetCheckBytes <= 0` forces a check per write (tests).
const BUDGET_CHECK_BYTES = 1024 * 1024;
const BUDGET_CHECK_MS = 5000;

/** One host sink for Electron and both child streams, bounded across all its files. */
function loggingBudget(file) {
  let document;
  try { document = parse(fs.readFileSync(file, 'utf8')); }
  catch (error) { if (error.code === 'ENOENT') return { maxFileBytes: MAX_FILE_BYTES, maxTotalBytes: MAX_TOTAL_BYTES }; throw new Error(`Invalid logging configuration ${file}: ${error.message}`); }
  const logging = document.logging;
  if (!logging || !Number.isSafeInteger(logging.rotation_bytes) || !Number.isSafeInteger(logging.total_bytes)
    || logging.rotation_bytes < 4096 || logging.total_bytes < logging.rotation_bytes) throw new Error(`Invalid logging configuration ${file}: total_bytes >= rotation_bytes >= 4096 is required.`);
  return { maxFileBytes: logging.rotation_bytes, maxTotalBytes: logging.total_bytes };
}
function createLogSink(directory, options = {}) {
  const budgetDirectory = options.budgetDirectory ?? path.dirname(directory);
  const configurationFile = options.configurationFile ?? path.join(path.dirname(budgetDirectory), 'config', 'logging.toml');
  const configured = loggingBudget(configurationFile);
  const maxFileBytes = options.maxFileBytes ?? configured.maxFileBytes;
  const maxTotalBytes = options.maxTotalBytes ?? configured.maxTotalBytes;
  const budgetCheckBytes = options.budgetCheckBytes ?? BUDGET_CHECK_BYTES;
  const budgetCheckMs = options.budgetCheckMs ?? BUDGET_CHECK_MS;
  if (maxFileBytes <= 0 || maxTotalBytes < maxFileBytes) throw new Error('Invalid host log budget.');
  fs.mkdirSync(directory, { recursive: true });
  let sequence = 0;
  let bytesSinceBudget = 0;
  let lastBudgetAt = Date.now();
  function enforceBudget() {
    const entries = [];
    function visit(root) {
      let children;
      try { children = fs.readdirSync(root, { withFileTypes: true }); }
      catch (error) { if (error.code === 'ENOENT') return; throw error; }
      for (const item of children) {
        const file = path.join(root, item.name);
        if (item.isSymbolicLink()) continue;
        if (item.isDirectory()) visit(file);
        else if (item.isFile()) {
          try { const stat = fs.statSync(file); entries.push({ file, size: stat.size, mtimeMs: stat.mtimeMs, rotated: /\.log\.\d+(?:-[a-f\d]{32})?$|\.\d+-\d+\.log$/i.test(item.name) }); }
          catch (error) { if (error.code !== 'ENOENT') throw error; }
        }
      }
    }
    visit(budgetDirectory);
    let total = entries.reduce((sum, item) => sum + item.size, 0);
    for (const item of entries.filter(item => item.rotated).sort((a, b) => a.mtimeMs - b.mtimeMs || a.file.localeCompare(b.file))) {
      if (total <= maxTotalBytes) break;
      try { fs.unlinkSync(item.file); } catch (error) { if (error.code !== 'ENOENT') throw error; }
      total -= item.size;
    }
  }
  function write(label, value) {
    if (!['electron', 'core', 'gateway'].includes(label)) throw new Error('Unknown host log stream.');
    const file = path.join(directory, `${label}.log`);
    let remaining = Buffer.isBuffer(value) ? value : Buffer.from(String(value));
    let appended = 0;
    while (remaining.length) {
      let size = fs.existsSync(file) ? fs.statSync(file).size : 0;
      if (size >= maxFileBytes) {
        fs.renameSync(file, path.join(directory, `${label}.${Date.now()}-${sequence++}.log`)); size = 0;
      }
      const chunk = remaining.subarray(0, maxFileBytes - size);
      fs.appendFileSync(file, chunk, { mode: 0o600 });
      appended += chunk.length;
      remaining = remaining.subarray(chunk.length);
    }
    bytesSinceBudget += appended;
    const now = Date.now();
    if (budgetCheckBytes <= 0 || bytesSinceBudget >= budgetCheckBytes || now - lastBudgetAt >= budgetCheckMs) {
      enforceBudget();
      bytesSinceBudget = 0;
      lastBudgetAt = now;
    }
  }
  return { write, enforceBudget };
}
module.exports = { createLogSink, loggingBudget, MAX_FILE_BYTES, MAX_TOTAL_BYTES };
