const fs = require('node:fs');
const path = require('node:path');

const MAX_LAYOUT_BYTES = 4 * 1024 * 1024;

function createWorkbenchLayoutStore(filePath) {
  function load() {
    try {
      const raw = fs.readFileSync(filePath, 'utf8');
      if (Buffer.byteLength(raw, 'utf8') > MAX_LAYOUT_BYTES) return null;
      const value = JSON.parse(raw);
      return value && typeof value === 'object' ? value : null;
    } catch {
      return null;
    }
  }

  function save(value) {
    if (!value || typeof value !== 'object' || value.version !== 1) {
      throw new Error('Invalid workbench layout document.');
    }
    const serialized = JSON.stringify(value);
    if (Buffer.byteLength(serialized, 'utf8') > MAX_LAYOUT_BYTES) {
      throw new Error('Workbench layout document is too large.');
    }
    fs.mkdirSync(path.dirname(filePath), { recursive: true });
    const temporary = `${filePath}.${process.pid}.tmp`;
    try {
      fs.writeFileSync(temporary, serialized, 'utf8');
      fs.renameSync(temporary, filePath);
    } finally {
      if (fs.existsSync(temporary)) fs.rmSync(temporary, { force: true });
    }
    return value;
  }

  return { load, save, filePath };
}

module.exports = { MAX_LAYOUT_BYTES, createWorkbenchLayoutStore };
