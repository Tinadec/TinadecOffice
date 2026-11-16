import { randomBytes } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { resolve } from 'node:path';
import concurrently from 'concurrently';

/** One launch credential reaches only the three trusted development hosts. */
export function developmentHostCommands(environment = process.env, token = randomBytes(32).toString('base64url')) {
  const env = { ...environment, TINADEC_HOST_CONTROL_TOKEN: token };
  return [
    { command: 'bun run dev:core', name: 'core', prefixColor: 'blue', env: { ...env } },
    { command: 'bun run dev:gateway', name: 'gateway', prefixColor: 'magenta', env: { ...env } },
    { command: 'bun run dev:desktop', name: 'desktop', prefixColor: 'green', env: { ...env } },
  ];
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const { result } = concurrently(developmentHostCommands(), { killOthersOn: ['success', 'failure'], prefix: 'name' });
  try { await result; }
  catch { process.exitCode = 1; } // Never print close events: they carry each child's private environment.
}
