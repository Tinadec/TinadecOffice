import { spawn, spawnSync } from "node:child_process";
import {
	appendFileSync,
	closeSync,
	existsSync,
	mkdirSync,
	openSync,
	readSync,
	rmSync,
	statSync,
	writeFileSync,
} from "node:fs";
import { createServer } from "node:net";
import { dirname, join, parse, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import {
	coreHealthUrl,
	coreReadinessUrl,
	gatewayHealthUrl,
	toolsManifestUrl,
	toolsReadinessUrl,
	harnessManifestUrl,
	smokePorts,
	newSmokeHostControlToken,
	smokeHeaders,
	SMOKE_HOST_CONTROL_ENV,
	validateCoreHealth,
	validateGatewayHealth,
	validateCoreReadiness,
	validateToolsManifest,
	validateToolsReadiness,
	validateHarnessManifest,
} from "./smokeAssertions.mjs";

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = resolve(scriptsDir, "..");
const maxLogBytes = 32_000;

function requireWindowsX64(label) {
	if (process.platform !== "win32" || process.arch !== "x64") {
		throw new Error(`${label} is available only on native Windows x64.`);
	}
}

function delay(milliseconds) {
	return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}

function canListen(port) {
	return new Promise((resolvePort, rejectPort) => {
		const server = createServer();
		server.once("error", (error) => {
			rejectPort(
				new Error(
					`Port ${port} is not free: ${error instanceof Error ? error.message : String(error)}`,
				),
			);
		});
		server.listen({ host: "0.0.0.0", port, exclusive: true }, () => {
			server.close((error) => {
				if (error) rejectPort(error);
				else resolvePort(true);
			});
		});
	});
}

async function assertPortsAvailable(ports, phase) {
	for (const port of ports) {
		try {
			await canListen(port);
		} catch (error) {
			throw new Error(
				`${phase}: ${error instanceof Error ? error.message : String(error)}`,
			);
		}
	}
}

async function probeJson(url, timeoutMs = 1_000, headers = null) {
	const controller = new AbortController();
	const timeout = setTimeout(() => controller.abort(), timeoutMs);
	try {
		const response = await fetch(url, {
			headers: headers ?? { accept: "application/json", "cache-control": "no-store" },
			signal: controller.signal,
		});
		let value = null;
		try {
			value = await response.json();
		} catch {}
		return { ok: response.ok, status: response.status, value };
	} catch {
		return { ok: false, status: 0, value: null };
	} finally {
		clearTimeout(timeout);
	}
}

async function waitForJson(url, validate, label, child, timeoutMs, headers = null) {
	const deadline = Date.now() + timeoutMs;
	let lastStatus = "unreachable";
	while (Date.now() < deadline) {
		if (child && (child.exitCode !== null || child.signalCode !== null)) {
			throw new Error(
				`Electron exited with code ${String(child.exitCode)} before ${label} was ready.`,
			);
		}
		const response = await probeJson(url, 1_000, headers);
		lastStatus = `HTTP ${response.status}`;
		if (response.ok && validate(response.value)) return response.value;
		await delay(250);
	}
	throw new Error(
		`Timed out after ${timeoutMs}ms waiting for ${label} at ${url}; last result: ${lastStatus}.`,
	);
}

function appendBounded(current, chunk) {
	const next = current + chunk.toString();
	return next.length > maxLogBytes ? next.slice(-maxLogBytes) : next;
}

function tailText(path, byteLimit = maxLogBytes) {
	if (!existsSync(path)) return "";
	const size = statSync(path).size;
	if (size === 0) return "";
	const length = Math.min(size, byteLimit);
	const buffer = Buffer.alloc(length);
	const descriptor = openSync(path, "r");
	try {
		readSync(descriptor, buffer, 0, length, size - length);
	} finally {
		closeSync(descriptor);
	}
	return buffer.toString("utf8");
}

function printDiagnostics(logPaths, label, electronPid, stdout, stderr) {
	const sections = [
		["Core log tail", tailText(logPaths.core)],
		["Gateway log tail", tailText(logPaths.gateway)],
		["Electron stdout tail", stdout || tailText(logPaths.electronStdout)],
		["Electron stderr tail", stderr || tailText(logPaths.electronStderr)],
	];
	console.error(`${label} diagnostics (Electron PID ${String(electronPid)}):`);
	for (const [name, value] of sections) {
		console.error(`\n--- ${name} ---\n${value.trim() || "<no log output>"}`);
	}
}

export function sanitizedEnvironment(profile, temporary, hostControlToken) {
	const systemRoot =
		process.env.SystemRoot ?? process.env.WINDIR ?? "C:\\Windows";
	const profileRoot = parse(profile).root;
	return {
		APPDATA: join(profile, "AppData", "Roaming"),
		COMSPEC:
			process.env.COMSPEC ?? join(systemRoot, "System32", "cmd.exe"),
		HOME: profile,
    TINADEC_HOME: join(profile, ".tinadec"),
    // The packaged host mints this and passes it to the Core/Gateway children it spawns; the
    // smoke supplies it so those services accept the smoke's own readiness probes.
    [SMOKE_HOST_CONTROL_ENV]: hostControlToken,
		HOMEDRIVE: profileRoot.slice(0, Math.max(2, profileRoot.length - 1)),
		HOMEPATH: profile.slice(profileRoot.length) || "\\",
		LOCALAPPDATA: join(profile, "AppData", "Local"),
		NO_PROXY: "127.0.0.1,localhost",
		PATHEXT: process.env.PATHEXT ?? ".COM;.EXE;.BAT;.CMD",
		PROCESSOR_ARCHITECTURE: "AMD64",
		SystemDrive: profileRoot.slice(0, Math.max(2, profileRoot.length - 1)),
		SystemRoot: systemRoot,
		TEMP: temporary,
		TINADEC_DISABLE_TRANSPARENCY: "1",
		TINADEC_GATEWAY_URL: "http://127.0.0.1:48730",
		TINADEC_TOOL_RUNTIME_URL: "http://127.0.0.1:48732",
		TMP: temporary,
		TMPDIR: temporary,
		USERPROFILE: profile,
		WINDIR: systemRoot,
		PATH: [
			join(systemRoot, "System32"),
			systemRoot,
			join(systemRoot, "System32", "Wbem"),
		].join(";"),
	};
}

async function stopProcessTree(child, label) {
	if (child?.pid) {
		spawnSync(
			"taskkill.exe",
			["/pid", String(child.pid), "/t", "/f"],
			{
				stdio: "ignore",
				timeout: 30_000,
				windowsHide: true,
			},
		);
	}
	if (child && child.exitCode === null && child.signalCode === null) {
		await new Promise((resolveClose) => {
			let settled = false;
			const finish = () => {
				if (settled) return;
				settled = true;
				clearTimeout(timer);
				child.removeListener("close", finish);
				child.removeListener("error", finish);
				resolveClose();
			};
			const timer = setTimeout(finish, 20_000);
			child.once("close", finish);
			child.once("error", finish);
		});
	}
	const deadline = Date.now() + 20_000;
	let lastError;
	while (Date.now() < deadline) {
		try {
			await assertPortsAvailable([48730, 48731], `${label} shutdown check`);
			return;
		} catch (error) {
			lastError = error;
			await delay(250);
		}
	}
	throw new Error(
		`${label} left ports 48730/48731 listening after terminating the Electron process tree: ${
			lastError instanceof Error ? lastError.message : String(lastError)
		}`,
	);
}

export async function runPackagedWindowsSmoke(options = {}) {
	requireWindowsX64("The packaged Windows smoke test");
	const releaseDir = resolve(
		options.releaseDir ?? process.argv[2] ?? join(desktopDir, "release"),
	);
	const executable = resolve(
		options.executable ??
			process.env.TINADEC_SMOKE_EXECUTABLE ??
			join(releaseDir, "win-unpacked", "TinadecOffice.exe"),
	);
	const smokeRoot = resolve(
		options.smokeRoot ??
			process.env.TINADEC_SMOKE_ROOT ??
			join(desktopDir, ".runtime-cache", `packaged smoke ${process.pid}`),
	);
	const label = options.label ?? process.env.TINADEC_SMOKE_LABEL ?? "Packaged Windows";
	const timeoutMs = Number(
		options.timeoutMs ??
			process.env.TINADEC_SMOKE_TIMEOUT_MS ??
			120_000,
	);
	const cleanup = options.cleanup ?? process.env.TINADEC_SMOKE_KEEP_ROOT !== "1";
	const hostControlToken = newSmokeHostControlToken();
	const userData = join(smokeRoot, "user-data");
	const profile = join(smokeRoot, "profile");
	const temporary = join(smokeRoot, "temp");
  const dataRoot = join(profile, ".tinadec");
  const logsDir = join(dataRoot, "logs", "host");
	const logPaths = {
		core: join(logsDir, "core.log"),
		gateway: join(logsDir, "gateway.log"),
		electronStdout: join(smokeRoot, "electron.stdout.log"),
		electronStderr: join(smokeRoot, "electron.stderr.log"),
		tools: join(temporary, "tinadec-tools", "logs"),
	};
	let child;
	let stdout = "";
	let stderr = "";
	let smokeError;
	const results = {};
	let coreHealth;
	let gatewayHealth;
	let coreReadiness;
	let toolsManifest;
	let toolsReadiness;
	let harnessManifest;

	await assertPortsAvailable(smokePorts, `${label} preflight`);
	if (!existsSync(executable) || !statSync(executable).isFile()) {
		throw new Error(`${label} executable is missing: ${executable}`);
	}
	rmSync(smokeRoot, {
		recursive: true,
		force: true,
		maxRetries: 20,
		retryDelay: 250,
	});
	for (const directory of [userData, profile, temporary, logsDir]) {
		mkdirSync(directory, { recursive: true });
	}
  mkdirSync(join(dataRoot, "config"), { recursive: true });
	writeFileSync(
    join(dataRoot, "config", "desktop.toml"),
    'gateway_url = "http://127.0.0.1:48730"\n',
	);
	writeFileSync(logPaths.electronStdout, "");
	writeFileSync(logPaths.electronStderr, "");

	try {
		child = spawn(
			executable,
			[
				`--user-data-dir=${userData}`,
				"--disable-gpu",
				"--no-first-run",
			],
			{
				cwd: dirname(executable),
				env: sanitizedEnvironment(profile, temporary, hostControlToken),
				stdio: ["ignore", "pipe", "pipe"],
				windowsHide: true,
			},
		);
		child.stdout.on("data", (chunk) => {
			stdout = appendBounded(stdout, chunk);
			appendFileSync(logPaths.electronStdout, chunk);
		});
		child.stderr.on("data", (chunk) => {
			stderr = appendBounded(stderr, chunk);
			appendFileSync(logPaths.electronStderr, chunk);
		});
		await new Promise((resolveSpawn, rejectSpawn) => {
			child.once("spawn", resolveSpawn);
			child.once("error", rejectSpawn);
		});
		const context = {
			label,
			executable,
			electronPid: child.pid,
			startedAt: new Date().toISOString(),
			smokeRoot,
			logPaths,
		};
		writeFileSync(
			join(smokeRoot, "smoke-context.json"),
			`${JSON.stringify(context, null, 2)}\n`,
		);
		console.log(`${label} Electron PID: ${child.pid}`);
		console.log(`Core log: ${logPaths.core}`);
		console.log(`Gateway log: ${logPaths.gateway}`);
		console.log(`Electron logs: ${logPaths.electronStdout}; ${logPaths.electronStderr}`);

		// Walked from the shared list rather than re-listing the calls here: a hand-copied sequence
		// is how one leg ends up asserting something different from the other, and it is how this
		// smoke kept probing protected routes with no credential after the host gate landed.
		for (const check of smokeChecks) {
			results[check.key] = await waitForJson(
				check.url,
				check.validate,
				check.label,
				child,
				timeoutMs,
				smokeHeaders(hostControlToken, { public: check.public }),
			);
		}
		coreHealth = results.coreHealth;
		gatewayHealth = results.gatewayHealth;
		coreReadiness = results.coreReadiness;
		toolsManifest = results.toolsManifest;
		toolsReadiness = results.toolsReadiness;
		harnessManifest = results.harnessManifest;
		console.log(
			`${label} smoke passed (Core ${coreHealth.version}, readiness ${coreReadiness.status}, tools ${toolsManifest.length}).`,
		);
	} catch (error) {
		smokeError = error;
		printDiagnostics(logPaths, label, child?.pid, stdout, stderr);
	} finally {
		try {
			await stopProcessTree(child, label);
		} catch (error) {
			smokeError ??= error;
			printDiagnostics(logPaths, label, child?.pid, stdout, stderr);
		}
	}

	if (smokeError) throw smokeError;
	const result = {
		label,
		executable,
		electronPid: child.pid,
		smokeRoot,
		logPaths,
		coreHealth,
		gatewayHealth,
		coreReadiness,
		toolsManifest,
		toolsReadiness,
		harnessManifest,
	};
	if (cleanup) {
		try {
			rmSync(smokeRoot, {
				recursive: true,
				force: true,
				maxRetries: 20,
				retryDelay: 500,
			});
		} catch (error) {
			console.warn(
				`Could not remove disposable smoke data ${smokeRoot}: ${
					error instanceof Error ? error.message : String(error)
				}`,
			);
		}
	}
	return result;
}

const invokedAsScript = process.argv[1]
	? resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))
	: false;
if (invokedAsScript) {
	try {
		await runPackagedWindowsSmoke();
	} catch (error) {
		console.error(error instanceof Error ? error.message : String(error));
		process.exitCode = 1;
	}
}
