import { spawnSync } from "node:child_process";
import { statSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const defaultDesktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const defaultRepoRoot = resolve(defaultDesktopDir, "..", "..");

function isNonEmptyFile(path) {
	try {
		const stats = statSync(path);
		return stats.isFile() && stats.size > 0;
	} catch {
		return false;
	}
}

export function findNativePtyBinary(
	desktopDir = defaultDesktopDir,
	repoRoot = resolve(desktopDir, "..", ".."),
) {
	const candidates = [
		join(desktopDir, "node_modules", "node-pty", "build", "Release", "pty.node"),
		join(repoRoot, "node_modules", "node-pty", "build", "Release", "pty.node"),
	];
	for (const candidate of candidates) {
		if (isNonEmptyFile(candidate)) return candidate;
	}
	return null;
}

export function verifyNativePtyBinary(
	desktopDir = defaultDesktopDir,
	repoRoot = resolve(desktopDir, "..", ".."),
) {
	const found = findNativePtyBinary(desktopDir, repoRoot);
	if (!found) {
		const localPath = join(desktopDir, "node_modules", "node-pty", "build", "Release", "pty.node");
		const hoistedPath = join(repoRoot, "node_modules", "node-pty", "build", "Release", "pty.node");
		throw new Error(
			`OFFICE_SKIP_NATIVE_REBUILD=1 was set, but prebuilt node-pty addon (pty.node) is missing or empty. ` +
				`Checked: ${localPath} and ${hoistedPath}.`,
		);
	}
	return found;
}

export function verifyPrebuiltFrontend(desktopDir = defaultDesktopDir) {
	const indexHtml = join(desktopDir, "dist", "index.html");
	if (!isNonEmptyFile(indexHtml)) {
		throw new Error(
			`OFFICE_SKIP_FRONTEND_BUILD=1 was set, but prebuilt frontend entry is missing or empty: ${indexHtml}.`,
		);
	}
	return indexHtml;
}

export function resolvePackageSteps(env = process.env, extraArgs = [], options) {
	if (options?.desktopDir) {
		const targetDesktopDir = options.desktopDir;
		const targetRepoRoot = options.repoRoot ?? resolve(targetDesktopDir, "..", "..");
		if (env?.OFFICE_SKIP_NATIVE_REBUILD === "1") {
			verifyNativePtyBinary(targetDesktopDir, targetRepoRoot);
		}
		if (env?.OFFICE_SKIP_FRONTEND_BUILD === "1") {
			verifyPrebuiltFrontend(targetDesktopDir);
		}
	}

	const steps = [];
	if (env?.OFFICE_SKIP_NATIVE_REBUILD !== "1") {
		steps.push({ command: "bun", args: ["run", "rebuild:native"] });
	}
	if (env?.OFFICE_SKIP_FRONTEND_BUILD !== "1") {
		steps.push({ command: "bun", args: ["run", "build"] });
	}
	steps.push(
		{ command: "bun", args: ["run", "stage:runtime"] },
		{ command: "bun", args: ["run", "check:runtime"] },
		{ command: "bun", args: ["scripts/package.mjs", ...extraArgs] },
	);
	return steps;
}

const isDirectRun = Boolean(
	process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url),
);

if (isDirectRun) {
	const extraArgs = process.argv.slice(2);
	if (process.env.OFFICE_SKIP_NATIVE_REBUILD === "1") {
		const ptyPath = verifyNativePtyBinary(defaultDesktopDir, defaultRepoRoot);
		console.log(`Skipping rebuild:native (using prebuilt ${ptyPath}).`);
	}
	if (process.env.OFFICE_SKIP_FRONTEND_BUILD === "1") {
		const indexHtml = verifyPrebuiltFrontend(defaultDesktopDir);
		console.log(`Skipping frontend build (using prebuilt ${indexHtml}).`);
	}

	const steps = resolvePackageSteps(process.env, extraArgs);
	for (const step of steps) {
		const result = spawnSync(step.command, step.args, {
			cwd: defaultDesktopDir,
			env: process.env,
			stdio: "inherit",
			windowsHide: true,
		});
		if (result.error) throw result.error;
		if (result.status !== 0) {
			process.exit(result.status ?? 1);
		}
	}
}
