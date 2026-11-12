import { execFileSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';

/**
 * Ad-hoc signing for the macOS bundle.
 *
 * electron-builder is told to skip signing (build.mac.identity is null: there is no Developer ID
 * yet), so the .app keeps the prebuilt Electron stub signature: identifier "Electron",
 * linker-signed, no resource seal, Info.plist unbound. With a quarantine attribute macOS then calls
 * the app "damaged" instead of merely "unidentified developer", and the user has no way through.
 *
 * A complete ad-hoc signature fixes exactly that: the seal exists, so Finder shows the ordinary
 * "unidentified developer" path (right-click -> Open) and the app launches. Signing order matters
 * and matches what codesign requires: nested code first, the bundle last. The runtime binaries under
 * Contents/Resources are not Mach-O-agnostic to codesign, so they are signed explicitly - a
 * Developer ID build will need the same list (with --options runtime and a timestamp) before
 * notarization will accept the bundle.
 */
export const RUNTIME_EXECUTABLES = [
  'Contents/Resources/runtime/core/TinadecCore.Api',
  'Contents/Resources/runtime/gateway/TinadecGateway',
  'Contents/Resources/runtime/tools/TinadecTools',
  'Contents/Resources/runtime/tools/rg',
  'Contents/Resources/runtime/native/rg/rg',
];

function list(dir) {
  try { return readdirSync(dir, { withFileTypes: true }); } catch { return []; }
}

/**
 * Nested code, deepest first. Helper .app bundles come before the frameworks that host them, and
 * the top-level bundle is always last: signing an outer bundle seals its contents, so anything
 * inside signed afterwards leaves the seal stale.
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
  for (const relative of RUNTIME_EXECUTABLES) plan.push(join(appPath, relative));
  plan.push(appPath);
  return plan;
}

export function codesignArguments(target) {
  return ['--force', '--sign', '-', '--timestamp=none', target];
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

/** electron-builder afterPack hook: unsigned everywhere except macOS, where the seal must exist. */
export default async function afterPack(context) {
  if (context.electronPlatformName !== 'darwin') return;
  const productName = context.packager.appInfo.productFilename;
  signMacAdhoc(join(context.appOutDir, productName + '.app'));
}

if (process.argv[1] && process.argv[1].endsWith('sign-mac-adhoc.mjs')) {
  const appPath = process.argv[2];
  if (!appPath) {
    console.error('usage: node scripts/sign-mac-adhoc.mjs <path-to-app>');
    process.exit(2);
  }
  const signed = signMacAdhoc(appPath);
  console.log('ad-hoc signed ' + signed.length + ' nested target(s) and ' + appPath);
}
