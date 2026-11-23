import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Ad-hoc signing for the macOS bundle.
 *
 * electron-builder is told to skip signing (build.mac.identity is null: there is no Developer ID
 * yet), so the .app would keep the prebuilt Electron stub signature: identifier "Electron",
 * linker-signed, no resource seal, Info.plist unbound. With a quarantine attribute macOS then calls
 * the app "damaged" instead of merely "unidentified developer", and the user has no way through.
 *
 * Two-stage order is load-bearing:
 *
 * 1. The staged runtime under `apps/desktop/runtime` is signed BEFORE electron-builder copies it
 *    into `Contents/Resources/runtime`. The Office channel archives are built from that staged
 *    directory, and `verify-office-channel.mjs` requires the archived module files to be
 *    byte-identical to the bundle's copies. Signing the bundle copies afterwards would change their
 *    bytes and turn every macOS release into a failed byte-comparison.
 * 2. `afterPack` signs the nested code inside the bundle (Helpers, Frameworks) and the bundle
 *    itself, then asserts the bundled runtime still matches the staged one.
 *
 * A Developer ID build needs the same two lists with `--options runtime` and a timestamp before
 * notarization will accept the bundle.
 */
export const RUNTIME_EXECUTABLES = [
  'core/TinadecCore.Api',
  'gateway/TinadecGateway',
  'tools/TinadecTools',
  'tools/rg',
  'native/rg/rg',
];

function list(dir) {
  try { return readdirSync(dir, { withFileTypes: true }); } catch { return []; }
}

/**
 * Nested code inside the bundle, deepest first. Helper .app bundles come before the frameworks that
 * host them, and the top-level bundle is always last: signing an outer bundle seals its contents, so
 * anything inside signed afterwards leaves the seal stale.
 */
export function signingPlan(appPath) {
  const plan = [];
  const frameworksDir = join(appPath, 'Contents', 'Frameworks');
  for (const entry of list(frameworksDir)) {
    if (entry.name.endsWith('.app')) plan.push(join(frameworksDir, entry.name));
  }
  for (const entry of list(frameworksDir)) {
    if (!entry.name.endsWith('.framework')) continue;
    const framework = join(frameworksDir, entry.name);
    const versionsDir = join(framework, 'Versions');
    for (const version of list(versionsDir)) {
      if (version.isDirectory() && version.name !== 'Current') {
        const helpers = join(versionsDir, version.name, 'Helpers');
        for (const helper of list(helpers)) plan.push(join(helpers, helper.name));
      }
    }
    plan.push(framework);
  }
  plan.push(appPath);
  return plan;
}

export function codesignArguments(target) {
  return ['--force', '--sign', '-', '--timestamp=none', target];
}

/** Sign the staged runtime in place, so every copy electron-builder makes is already sealed. */
export function signStagedRuntime(runtimeDir, { run = execFileSync, platform = process.platform } = {}) {
  if (platform !== 'darwin') return [];
  const signed = [];
  for (const relative of RUNTIME_EXECUTABLES) {
    const target = join(runtimeDir, relative);
    if (!existsSync(target)) continue;
    run('codesign', codesignArguments(target), { stdio: 'pipe' });
    signed.push(target);
  }
  return signed;
}

export function signMacAdhoc(appPath, { run = execFileSync, platform = process.platform } = {}) {
  if (platform !== 'darwin') return [];
  if (!existsSync(appPath) || !statSync(appPath).isDirectory()) {
    throw new Error('macOS signing needs an existing .app directory: ' + appPath);
  }
  const signed = [];
  for (const target of signingPlan(appPath)) {
    if (!existsSync(target)) continue;
    run('codesign', codesignArguments(target), { stdio: 'pipe' });
    signed.push(target);
  }
  return signed;
}

/**
 * The Office channel archives and the bundle must carry the same runtime bytes; verify-office-channel
 * compares them one by one. Failing here names the file while the bundle is still in the build
 * directory, instead of failing later with "differs from the full app's bundled runtime copy".
 */
export function assertBundledRuntimeMatches(appPath, runtimeDir, { readFile = readFileSync } = {}) {
  if (!existsSync(runtimeDir)) return [];
  const checked = [];
  for (const relative of RUNTIME_EXECUTABLES) {
    const staged = join(runtimeDir, relative);
    const bundled = join(appPath, 'Contents', 'Resources', 'runtime', relative);
    if (!existsSync(staged) || !existsSync(bundled)) continue;
    if (!readFile(staged).equals(readFile(bundled))) {
      throw new Error('Bundled runtime differs from the staged runtime: ' + relative
        + '. Sign the staged runtime before packaging so the Office channel archives stay byte-identical.');
    }
    checked.push(relative);
  }
  return checked;
}

/** electron-builder afterPack hook: sign the bundle, then prove its runtime copies match staging. */
export default async function afterPack(context) {
  if (context.electronPlatformName !== 'darwin') return;
  const productName = context.packager.appInfo.productFilename;
  const appPath = join(context.appOutDir, productName + '.app');
  signMacAdhoc(appPath);
  assertBundledRuntimeMatches(appPath, join(context.packager.projectDir, 'runtime'));
}

if (process.argv[1] && process.argv[1].endsWith('sign-mac-adhoc.mjs')) {
  const [appPath, stagedRuntimeDir] = process.argv.slice(2);
  if (!appPath) {
    console.error('usage: node scripts/sign-mac-adhoc.mjs <path-to-app> [staged-runtime-dir]');
    process.exit(2);
  }
  if (stagedRuntimeDir) signStagedRuntime(stagedRuntimeDir);
  const signed = signMacAdhoc(appPath);
  if (stagedRuntimeDir) assertBundledRuntimeMatches(appPath, stagedRuntimeDir);
  console.log('ad-hoc signed ' + signed.length + ' nested target(s) and ' + appPath);
}
