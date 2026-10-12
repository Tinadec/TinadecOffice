import { spawn, spawnSync } from "node:child_process";
import {
	appendFileSync,
	existsSync,
	mkdirSync,
	readdirSync,
	rmSync,
	statSync,
	writeFileSync,
} from "node:fs";
import { createServer } from "node:net";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { hostRuntimeTarget } from "./runtimeTargets.mjs";
import {
	smokeChecks,
	smokePorts,
	newSmokeHostControlToken,
	smokeHeaders,
	SMOKE_HOST_CONTROL_ENV,
} from "./smokeAssertions.mjs";

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = resolve(scriptsDir, "..");
const maxLogBytes = 32_000;

/// Where a packaged build actually lives, per target.
///
/// electron-builder lays the two out differently: Linux gets an unpacked directory whose binary is
/// named after `linux.executableName`, macOS gets a `.app` bundle whose binary is named after the
/// product. Guessing either one produces "executable is missing" on a leg that packaged fine.
export function packagedExecutablePath(releaseDir, target) {
	if (target.platform === "linux") {
		return join(releaseDir, "linux-unpacked", "TinadecOffice");
	}
	if (target.platform === "darwin") {
		return join(releaseDir, "mac-arm64", "TinadecOffice.app", "Contents", "MacOS", "TinadecOffice");
	}
	throw new Error(`No packaged layout known for ${target.key}.`);
}

/// The per-user data root the packaged app will pick, mirroring `officeRootFor()` in
/// `electron/serviceManager.cjs`. The smoke reads the Core/Gateway logs from here, so a wrong guess
/// is not a crash — it is a failure message with no logs attached.
export function posixDataRoot(profile, target) {
  return join(profile, ".tinadec");
}

/// Flags the app needs to start at all on a build machine.
///
/// `--no-sandbox` is Linux-only and deliberate: Chromium's sandbox wants a setuid helper that the
/// runner image does not have, and this smoke is about the product's own process tree, not the
/// renderer sandbox. macOS must not get it — that is where the app's own seatbelt backend is
/// exercised by the Core-side tests, and a flag that changes process privileges would make the run
/// prove something else.
export function electronSmokeArgs(userDataDir, target) {
	const args = [`--user-data-dir=${userDataDir}`, "--disable-gpu", "--no-first-run"];
	if (target.platform === "linux") args.push("--no-sandbox");
	return args;
}

/// A profile with nothing in it but the app's own paths. `HOME` is redirected so the packaged app
/// cannot read the developer's or the runner's real settings, caches, or model credentials.
export function sanitizedPosixEnvironment({ profile, temporary, inheritedPath, display, xauthority, hostControlToken }) {
	return {
		HOME: profile,
    TINADEC_HOME: join(profile, ".tinadec"),
    // The packaged host mints this and passes it to the Core/Gateway children it spawns; the
    // smoke supplies it so those services accept the smoke's own readiness probes.
    ...(hostControlToken ? { [SMOKE_HOST_CONTROL_ENV]: hostControlToken } : {}),
		USER: process.env.USER ?? "tinadec-smoke",
		LOGNAME: process.env.USER ?? "tinadec-smoke",
		XDG_DATA_HOME: join(profile, ".local", "share"),
		XDG_CACHE_HOME: join(profile, ".cache"),
		XDG_CONFIG_HOME: join(profile, ".config"),
		TMPDIR: temporary,
		NO_PROXY: "127.0.0.1,localhost",
		TINADEC_GATEWAY_URL: "http://127.0.0.1:48730",
		TINADEC_TOOL_RUNTIME_URL: "http://127.0.0.1:48732",
		TINADEC_DISABLE_TRANSPARENCY: "1",
		// Unlike the Windows smoke this keeps a real system PATH: the packaged app probes for git at
		// runtime on POSIX (there is no bundled PortableGit), and a stripped PATH would report "no
		// git" as if that were a product fact.
		PATH: inheritedPath ?? "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/usr/local/bin:/bin",
		...(display ? { DISPLAY: display } : {}),
		// A real desktop session authorizes X through $HOME/.Xauthority or $XAUTHORITY; the
		// redirected HOME breaks the first, so the second has to survive.
		...(xauthority ? { XAUTHORITY: xauthority } : {}),
	};
}

export function parseHdiutilOutput(text) {
	// hdiutil prints device, content hint and mount point separated by runs of whitespace, and the
	// mount point itself may contain spaces ("TinadecOffice 0.1.1"), so the columns are split on
	// two-or-more rather than on every space.
	const lines = text.split("\n").map((line) => line.trim()).filter(Boolean);
	for (const line of [...lines].reverse()) {
		const mount = line.split(/\s{2,}|\t/).at(-1)?.trim();
		if (mount?.startsWith("/Volumes/")) return mount;
	}
	return null;
}

function delay(milliseconds) {
	return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}

function canListen(port) {
	return new Promise((resolvePort, rejectPort) => {
		const server = createServer();
		server.once("error", (error) => rejectPort(new Error(`Port ${port} is not free: ${error.message}`)));
		server.listen({ host: "0.0.0.0", port, exclusive: true }, () => {
			server.close((error) => (error ? rejectPort(error) : resolvePort(true)));
		});
	});
}

async function assertPortsAvailable(ports, phase) {
	for (const port of ports) {
		try {
			await canListen(port);
		} catch (error) {
			throw new Error(`${phase}: ${error instanceof Error ? error.message : String(error)}`);
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

async function waitForJson(check, child, timeoutMs, headers = null) {
	const deadline = Date.now() + timeoutMs;
	let lastStatus = "unreachable";
	while (Date.now() < deadline) {
		if (child && (child.exitCode !== null || child.signalCode !== null)) {
			throw new Error(
				`Electron exited with code ${String(child.exitCode)}${
					child.signalCode ? ` (signal ${child.signalCode})` : ""
				} before ${check.label} was ready.`,
			);
		}
		const response = await probeJson(check.url, 1_000, headers);
		lastStatus = `HTTP ${response.status}`;
		if (response.ok && check.validate(response.value)) return response.value;
		await delay(250);
	}
	throw new Error(
		`Timed out after ${timeoutMs}ms waiting for ${check.label} at ${check.url}; last result: ${lastStatus}.`,
	);
}

function appendBounded(current, chunk) {
	const next = current + chunk.toString();
	return next.length > maxLogBytes ? next.slice(-maxLogBytes) : next;
}

function tailText(path) {
	if (!existsSync(path)) return "(missing)";
	return statSync(path).size === 0 ? "(empty)" : "see log";
}

function printDiagnostics(logPaths, label, pid, stdout, stderr) {
	console.error(`\n=== ${label} diagnostics (Electron PID ${pid ?? "never started"}) ===`);
	console.error(`--- Core log (${logPaths.core}): ${existsSync(logPaths.core) ? "present" : "absent"}`);
	console.error(`--- Gateway log (${logPaths.gateway}): ${existsSync(logPaths.gateway) ? "present" : "absent"}`);
	console.error(`--- Electron stderr (${logPaths.electronStderr}): ${tailText(logPaths.electronStderr)}`);
	if (stderr) console.error(`stderr tail: ${stderr}`);
	if (stdout) console.error(`stdout tail: ${stdout}`);
}

/// The packaged app spawns Core and Gateway `detached`, so its pid is its process-group id and one
/// signal reaches the whole tree — including the TinadecTools child Core starts later. Killing only
/// the Electron pid would leave those listening, and the ports-freed check below is what proves this
/// actually worked.
async function stopProcessTree(child, label) {
	if (child?.pid) {
		try {
			process.kill(-child.pid, "SIGTERM");
		} catch (error) {
			if (error?.code !== "ESRCH") console.warn(`SIGTERM to process group ${child.pid} failed: ${error.message}`);
		}
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
			const timer = setTimeout(() => {
				try {
					process.kill(-child.pid, "SIGKILL");
				} catch {}
				finish();
			}, 20_000);
			child.once("close", finish);
			child.once("error", finish);
		});
	}
	const deadline = Date.now() + 20_000;
	let lastError;
	while (Date.now() < deadline) {
		try {
			await assertPortsAvailable(smokePorts, `${label} shutdown check`);
			return;
		} catch (error) {
			lastError = error;
			await delay(250);
		}
	}
	throw new Error(
		`${label} left a service listening after terminating the Electron process tree: ${
			lastError instanceof Error ? lastError.message : String(lastError)
		}`,
	);
}

function findDmg(releaseDir) {
	if (!existsSync(releaseDir)) return null;
	const dmg = readdirSync(releaseDir).filter((name) => name.endsWith("-osx-arm64.dmg")).sort().pop();
	return dmg ? join(releaseDir, dmg) : null;
}

/// CLI shape: `smoke-packaged-posix.mjs [releaseDir] [--executable=<path>] [--label=<text>]`.
///
/// The executable override exists for one reason: `dpkg -i` puts the launcher under `/opt`, and the
/// thing a user actually runs is that installed file, not the `linux-unpacked` directory that fed the
/// packager. Without the override the smoke could only ever prove the latter.
export function parseSmokeArgs(argv) {
	const parsed = {};
	for (const entry of argv) {
		const option = /^--(executable|label)=(.+)$/u.exec(entry);
		if (option) {
			parsed[option[1]] = option[2];
			continue;
		}
		if (entry.startsWith("--")) {
			throw new Error(`Unknown smoke option ${entry}. Known: --executable=, --label=`);
		}
		parsed.releaseDir ??= entry;
	}
	return parsed;
}

export async function runPackagedPosixSmoke(options = {}) {
	const target = hostRuntimeTarget(options.platform ?? process.platform, options.arch ?? process.arch);
	if (target.platform === "win32") {
		throw new Error("The POSIX smoke does not run on Windows; use smoke:packaged (Windows).");
	}

	const cli = parseSmokeArgs(process.argv.slice(2));
	const releaseDir = resolve(options.releaseDir ?? cli.releaseDir ?? join(desktopDir, "release"));
	const smokeRoot = resolve(
		options.smokeRoot ??
			process.env.TINADEC_SMOKE_ROOT ??
			join(desktopDir, ".runtime-cache", `packaged smoke ${process.pid}`),
	);
	const label = options.label ?? cli.label ?? `${target.key} packaged`;
	const timeoutMs = Number(options.timeoutMs ?? process.env.TINADEC_SMOKE_TIMEOUT_MS ?? 180_000);
	const cleanup = options.cleanup ?? process.env.TINADEC_SMOKE_KEEP_ROOT !== "1";

	const userData = join(smokeRoot, "user-data");
	const profile = join(smokeRoot, "profile");
	const temporary = join(smokeRoot, "temp");
	const dataRoot = posixDataRoot(profile, target);
  const logsDir = join(dataRoot, "logs", "host");
	const logPaths = {
		core: join(logsDir, "core.log"),
		gateway: join(logsDir, "gateway.log"),
		electronStdout: join(smokeRoot, "electron.stdout.log"),
		electronStderr: join(smokeRoot, "electron.stderr.log"),
	};

	// macOS ships a dmg, and a dmg nobody mounted has never been executed by anyone. Attach it and
	// run the binary inside, so this proves the shipped artifact rather than the directory that fed
	// the packager. The mount point comes from hdiutil's own answer, and the attach happens after the
	// smoke root is recreated — a volume mounted under it would be wiped by that very step.
	let mountedVolume = null;
	const requestedExecutable = options.executable ?? cli.executable;
	let executable = requestedExecutable ? resolve(requestedExecutable) : null;

	await assertPortsAvailable(smokePorts, `${label} preflight`);
	rmSync(smokeRoot, { recursive: true, force: true });
	mkdirSync(userData, { recursive: true });
	mkdirSync(profile, { recursive: true });
	mkdirSync(temporary, { recursive: true });
	mkdirSync(logsDir, { recursive: true });
  mkdirSync(join(dataRoot, "config"), { recursive: true });
	writeFileSync(
    join(dataRoot, "config", "desktop.toml"),
    'gateway_url = "http://127.0.0.1:48730"\n',
	);
	writeFileSync(logPaths.electronStdout, "");
	writeFileSync(logPaths.electronStderr, "");

	if (!executable && target.platform === "darwin") {
		const dmg = findDmg(releaseDir);
		if (dmg) {
			const attached = spawnSync("hdiutil", ["attach", "-nobrowse", dmg], { encoding: "utf8", timeout: 120_000 });
			if (attached.status !== 0) {
				throw new Error(`hdiutil attach failed for ${dmg}: ${attached.stderr || attached.stdout || `exit ${attached.status}`}`);
			}
			mountedVolume = parseHdiutilOutput(attached.stdout ?? "");
			if (!mountedVolume) {
				throw new Error(`hdiutil attached ${dmg} but reported no /Volumes mount point: ${JSON.stringify(attached.stdout)}`);
			}
			executable = join(mountedVolume, "TinadecOffice.app", "Contents", "MacOS", "TinadecOffice");
		}
	}
	executable ??= packagedExecutablePath(releaseDir, target);

	if (!existsSync(executable) || !statSync(executable).isFile()) {
		throw new Error(`${label} executable is missing: ${executable} (mounted=${mountedVolume ?? "none"})`);
	}

	let child;
	let stdout = "";
	let stderr = "";
	let smokeError;
	const results = {};
	const hostControlToken = newSmokeHostControlToken();

	try {
		child = spawn(executable, electronSmokeArgs(userData, target), {
			cwd: dirname(executable),
			env: sanitizedPosixEnvironment({ profile, temporary, inheritedPath: process.env.PATH, display: process.env.DISPLAY, xauthority: process.env.XAUTHORITY, hostControlToken }),
			stdio: ["ignore", "pipe", "pipe"],
			detached: true,
		});
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
		console.log(`${label} Electron PID ${child.pid} from ${executable}`);
		console.log(`Data root: ${dataRoot}`);
		console.log(`Core log: ${logPaths.core}`);

		for (const check of smokeChecks) {
			results[check.key] = await waitForJson(check, child, timeoutMs, smokeHeaders(hostControlToken, { public: check.public }));
		}
		console.log(
			`${label} smoke passed (Core ${results.coreHealth.version}, readiness ${results.coreReadiness.status}, tools ${results.toolsManifest.length}).`,
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
		if (mountedVolume) {
			spawnSync("hdiutil", ["detach", "-force", mountedVolume], { stdio: "ignore", timeout: 60_000 });
		}
	}

	if (smokeError) throw smokeError;
	if (cleanup) {
		try {
			rmSync(smokeRoot, { recursive: true, force: true });
		} catch (error) {
			console.warn(`Could not remove disposable smoke data ${smokeRoot}: ${error.message}`);
		}
	}
	return { label, executable, dataRoot, logPaths, ...results };
}

const invokedAsScript = process.argv[1]
	? resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))
	: false;
if (invokedAsScript) {
	try {
		await runPackagedPosixSmoke();
	} catch (error) {
		console.error(error instanceof Error ? error.message : String(error));
		process.exitCode = 1;
	}
}
