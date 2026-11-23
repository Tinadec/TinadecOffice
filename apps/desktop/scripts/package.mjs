import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { hostRuntimeTarget } from "./runtimeTargets.mjs";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const tempDir = join(desktopDir, ".runtime-cache", "tmp");
const ARTIFACT_LABEL_RE = /^[0-9A-Za-z._-]+$/u;

// electron-builder's own flag names, keyed by the platform facts module so the packaging entry and
// the staged runtime can never disagree about which platforms exist.
const BUILDER_FLAGS = {
	"win-x64": { flag: "--win", arch: "--x64", script: "package:win" },
	"linux-x64": { flag: "--linux", arch: "--x64", script: "package:linux" },
	"osx-arm64": { flag: "--mac", arch: "--arm64", script: "package:mac" },
};

function requireBuilderTarget(targetKey) {
	const entry = BUILDER_FLAGS[targetKey];
	if (!entry) {
		throw new Error(
			`No electron-builder mapping for '${targetKey}'. Known: ${Object.keys(BUILDER_FLAGS).join(", ")}.`,
		);
	}
	return entry;
}

function readDesktopVersion() {
	return JSON.parse(readFileSync(join(desktopDir, "package.json"), "utf8")).version;
}

export function artifactLabelOverrides(targetKey, { artifactLabel, packageVersion } = {}) {
	requireBuilderTarget(targetKey);
	if (!artifactLabel) return [];
	const label = String(artifactLabel).trim();
	if (!label || label === packageVersion) return [];
	if (!ARTIFACT_LABEL_RE.test(label)) {
		throw new Error(
			`Invalid artifact label '${label}'. Artifact labels must match /^[0-9A-Za-z._-]+$/.`,
		);
	}
	switch (targetKey) {
		case "win-x64":
			return [
				`-c.nsis.artifactName=TinadecOffice-${label}-win-x64-setup.\${ext}`,
				`-c.portable.artifactName=TinadecOffice-${label}-win-x64-portable.\${ext}`,
			];
		case "linux-x64":
			return [`-c.linux.artifactName=TinadecOffice-${label}-linux-x64.\${ext}`];
		case "osx-arm64":
			return [
				`-c.mac.artifactName=TinadecOffice-${label}-osx-arm64.\${ext}`,
				`-c.dmg.artifactName=TinadecOffice-${label}-osx-arm64.\${ext}`,
			];
		default:
			return [];
	}
}

export function builderArgs(
	targetKey,
	extra = [],
	{
		artifactLabel = process.env.OFFICE_ARTIFACT_LABEL,
		packageVersion = readDesktopVersion(),
	} = {},
) {
	const entry = requireBuilderTarget(targetKey);
	const overrides = artifactLabelOverrides(targetKey, { artifactLabel, packageVersion });
	return [entry.flag, entry.arch, "--publish", "never", ...overrides, ...extra];
}

const isDirectRun = process.argv[1] && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url));

if (isDirectRun) {
	const target = hostRuntimeTarget();
	const passthrough = process.argv.slice(2);
	mkdirSync(tempDir, { recursive: true });
	const result = spawnSync("bun", ["x", "electron-builder", ...builderArgs(target.key, passthrough)], {
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
	});
	if (result.error) throw result.error;
	process.exit(result.status ?? 1);
}
