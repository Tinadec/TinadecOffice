import { spawnSync } from "node:child_process";
import {
	existsSync,
	mkdirSync,
	mkdtempSync,
	readdirSync,
	rmSync,
	statSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { runPackagedWindowsSmoke } from "./smoke-packaged-windows.mjs";

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = resolve(scriptsDir, "..");
const productName = "TinadecOffice";

function requireWindowsX64() {
	if (process.platform !== "win32" || process.arch !== "x64") {
		throw new Error("The installed Windows smoke test requires native Windows x64.");
	}
}

function requireDirectory(path, label) {
	if (!existsSync(path) || !statSync(path).isDirectory()) {
		throw new Error(`${label} is missing: ${path}`);
	}
	return path;
}

function requireFile(path, label) {
	if (!existsSync(path) || !statSync(path).isFile() || statSync(path).size === 0) {
		throw new Error(`${label} is missing or empty: ${path}`);
	}
	return path;
}

function delay(milliseconds) {
	return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}

export function findWindowsInstaller(releaseDir) {
	const directory = requireDirectory(
		resolve(releaseDir),
		"Electron Builder release directory",
	);
	const matches = readdirSync(directory, { withFileTypes: true })
		.filter(
			(entry) =>
				entry.isFile() &&
				entry.name.startsWith(`${productName}-`) &&
				entry.name.endsWith("-win-x64-setup.exe"),
		)
		.map((entry) => join(directory, entry.name));
	if (matches.length !== 1) {
		throw new Error(
			`Expected one Windows NSIS setup in ${directory}; found ${matches.length}.`,
		);
	}
	return requireFile(matches[0], "Windows NSIS setup");
}

function runCommand(label, command, args, options) {
	const result = spawnSync(command, args, {
		stdio: "inherit",
		timeout: 120_000,
		windowsHide: true,
		...options,
	});
	if (result.error) {
		throw new Error(`${label} failed: ${result.error.message}`);
	}
	if (result.status !== 0) {
		throw new Error(`${label} exited with code ${result.status}.`);
	}
}

async function waitForFiles(paths, timeoutMs) {
	const deadline = Date.now() + timeoutMs;
	while (Date.now() < deadline) {
		if (
			paths.every(
				(path) => existsSync(path) && statSync(path).isFile() && statSync(path).size > 0,
			)
		) {
			return;
		}
		await delay(250);
	}
	throw new Error(
		`Timed out waiting for installed files: ${paths.filter((path) => !existsSync(path)).join(", ")}`,
	);
}

async function waitForRemoval(path, timeoutMs) {
	const deadline = Date.now() + timeoutMs;
	while (Date.now() < deadline) {
		if (!existsSync(path)) return;
		await delay(250);
	}
	throw new Error(`Installed executable remained after uninstall: ${path}`);
}

export async function runInstalledWindowsSmoke(options = {}) {
	requireWindowsX64();
	const releaseDir = resolve(
		options.releaseDir ?? process.argv[2] ?? join(desktopDir, "release"),
	);
	const installRoot = resolve(
		options.installRoot ??
			mkdtempSync(join(tmpdir(), `${productName} install smoke `)),
	);
	const installDir = join(installRoot, "app");
	const smokeRoot = join(installRoot, "application smoke");
	const executable = join(installDir, `${productName}.exe`);
	const uninstaller = join(installDir, `Uninstall ${productName}.exe`);
	const installer = findWindowsInstaller(releaseDir);
	const installerTemp = join(installRoot, "installer temp");
	let uninstalled = false;
	let smokeResult;

	if (!installDir.includes(" ")) {
		throw new Error(`Install smoke path must contain spaces: ${installDir}`);
	}
	mkdirSync(installRoot, { recursive: true });
	mkdirSync(installerTemp, { recursive: true });

	try {
		runCommand("Silent NSIS install", installer, ["/S", `/D=${installDir}`], {
			env: {
				...process.env,
				TEMP: installerTemp,
				TMP: installerTemp,
				TMPDIR: installerTemp,
			},
			timeout: 1_500_000,
			windowsVerbatimArguments: true,
		});
		await waitForFiles([executable, uninstaller], 300_000);
		requireFile(executable, "Installed application executable");
		requireFile(uninstaller, "Installed application uninstaller");
		smokeResult = await runPackagedWindowsSmoke({
			executable,
			label: "Installed Windows",
			smokeRoot,
		});
		runCommand("Silent NSIS uninstall", uninstaller, ["/S"], {
			cwd: installDir,
			timeout: 300_000,
		});
		await waitForRemoval(executable, 300_000);
		uninstalled = true;
		console.log(
			`Installed Windows smoke passed and uninstalled cleanly (Electron PID ${smokeResult.electronPid}).`,
		);
		return {
			...smokeResult,
			installer,
			installDir,
			uninstaller,
		};
	} finally {
		if (!uninstalled && existsSync(uninstaller)) {
			spawnSync(uninstaller, ["/S"], {
				cwd: installDir,
				stdio: "ignore",
				timeout: 300_000,
				windowsHide: true,
			});
		}
		if (options.cleanup ?? process.env.TINADEC_SMOKE_KEEP_ROOT !== "1") {
			try {
				rmSync(installRoot, {
					recursive: true,
					force: true,
				maxRetries: 20,
				retryDelay: 500,
				});
			} catch (error) {
				console.warn(
					`Could not remove disposable install-smoke data ${installRoot}: ${
						error instanceof Error ? error.message : String(error)
					}`,
				);
			}
		}
	}
}

const invokedAsScript = process.argv[1]
	? resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))
	: false;
if (invokedAsScript) {
	try {
		await runInstalledWindowsSmoke();
	} catch (error) {
		console.error(error instanceof Error ? error.message : String(error));
		process.exitCode = 1;
	}
}
