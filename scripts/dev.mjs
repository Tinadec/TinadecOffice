import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import concurrently from 'concurrently';

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/**
 * Development hosts must not open the installed application's data root (`~/.tinadec`).
 *
 * A dev run writes newer configuration documents and newer migrations into whatever root it
 * opens. When the packaged app then reads that root it either crashes on a document the release
 * schema does not accept, or refuses a schema a newer build created. The smoke scripts already
 * isolate themselves; this makes the same isolation the default for `npm run dev`.
 * An explicit `TINADEC_HOME` still wins, so pointing dev at a prepared root stays possible.
 */
export const DEVELOPMENT_DATA_ROOT = resolve(repoRoot, '.tinadec-dev');

/** One launch credential reaches only the three trusted development hosts. */
export function developmentHostCommands(environment = process.env, token = randomBytes(32).toString('base64url'),
  dataRoot = environment.TINADEC_HOME?.trim() || DEVELOPMENT_DATA_ROOT) {
  const env = { ...environment, TINADEC_HOST_CONTROL_TOKEN: token, TINADEC_HOME: dataRoot };
  return [
    { command: 'npm run dev:core', name: 'core', prefixColor: 'blue', env: { ...env } },
    { command: 'npm run dev:gateway', name: 'gateway', prefixColor: 'magenta', env: { ...env } },
    { command: 'npm run dev:desktop', name: 'desktop', prefixColor: 'green', env: { ...env } },
  ];
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { result } = concurrently(developmentHostCommands(), { killOthersOn: ['success', 'failure'], prefix: 'name' });
  try { await result; }
  catch { process.exitCode = 1; } // Never print close events: they carry each child's private environment.
}
