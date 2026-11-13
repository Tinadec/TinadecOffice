import { spawnSync } from "node:child_process";
import { mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { hostRuntimeTarget } from "./runtimeTargets.mjs";
import { signStagedRuntime } from "./sign-mac-adhoc.mjs";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const tempDir = join(desktopDir, ".runtime-cache", "tmp");

// electron-builder's own flag names, keyed by the platform facts module so the packaging entry and
// the staged runtime can never disagree about which platforms exist.
const BUILDER_FLAGS = {
	"win-x64": { flag: "--win", arch: "--x64", script: "package:win" },
	"linux-x64": { flag: "--linux", arch: "--x64", script: "package:linux" },
	"osx-arm64": { flag: "--mac", arch: "--arm64", script: "package:mac" },
};

export function builderArgs(targetKey, extra = []) {
	const entry = BUILDER_FLAGS[targetKey];
	if (!entry) {
		throw new Error(
			`No electron-builder mapping for '${targetKey}'. Known: ${Object.keys(BUILDER_FLAGS).join(", ")}.`,
		);
	}
	return [entry.flag, entry.arch, "--publish", "never", ...extra];
}

const isDirectRun = process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url));

if (isDirectRun) {
	const target = hostRuntimeTarget();
	const passthrough = process.argv.slice(2);
	mkdirSync(tempDir, { recursive: true });
	// Seal the staged runtime before electron-builder copies it into the bundle. The Office channel
	// archives are built from this directory and compared byte-for-byte with the bundle's copy, so
	// signing the bundle copy afterwards would silently break every macOS release.
	if (process.platform === "darwin") signStagedRuntime(join(desktopDir, "runtime"));
	const command = process.platform === "win32" ? "npx.cmd" : "npx";
	const result = spawnSync(command, ["electron-builder", ...builderArgs(target.key, passthrough)], {
		cwd: desktopDir,
		env: {
			...process.env,
			// macOS signing stays off until there is a Developer ID to use; without this,
			// electron-builder spends minutes looking for an identity and then fails the build.
			CSC_IDENTITY_AUTO_DISCOVERY: "false",
			TEMP: tempDir,
			TMP: tempDir,
			TMPDIR: tempDir,
		},
		stdio: "inherit",
		windowsHide: true,
		shell: process.platform === "win32",
	});
	if (result.error) throw result.error;
	process.exit(result.status ?? 1);
}
