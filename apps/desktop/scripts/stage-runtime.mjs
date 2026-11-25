import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
	chmodSync,
	copyFileSync,
	cpSync,
	existsSync,
	mkdirSync,
	readFileSync,
	readdirSync,
	renameSync,
	rmSync,
	statSync,
	writeFileSync,
} from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import {
	ripgrepStagedName,
	ripgrepTarget,
} from "../../../scripts/ripgrep-pin.mjs";
import { stageBubblewrap } from "../../../scripts/bubblewrap-pin.mjs";
import {
	hostRuntimeTarget,
	resolveRuntimeTarget,
	runtimeBinaryName,
} from "./runtimeTargets.mjs";

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const rootDir = resolve(desktopDir, "..", "..");
const runtimeDir = join(desktopDir, "runtime");
const cacheDir = join(desktopDir, ".runtime-cache");
const stagingDir = join(cacheDir, "runtime.staging");

const PORTABLE_GIT_VERSION = "2.55.0.windows.3";
const PORTABLE_GIT_ASSET = "PortableGit-2.55.0.3-64-bit.7z.exe";
export const PORTABLE_GIT_SHA256 = "ab00566336b5472120f9a52d34f2e79c5406535792acb0548001ffd0bd090e5d";

function nonEmptyEnv(value) {
	if (typeof value !== "string") return undefined;
	const trimmed = value.trim();
	return trimmed.length > 0 ? trimmed : undefined;
}

export function resolveDotnetCacheEnv(env = process.env, targetCacheDir = cacheDir) {
	const tempDir = join(targetCacheDir, "temp");
	return {
		DOTNET_CLI_HOME: nonEmptyEnv(env?.DOTNET_CLI_HOME) ?? join(targetCacheDir, "dotnet"),
		NUGET_PACKAGES: nonEmptyEnv(env?.NUGET_PACKAGES) ?? join(targetCacheDir, "nuget"),
		NUGET_HTTP_CACHE_PATH:
			nonEmptyEnv(env?.NUGET_HTTP_CACHE_PATH) ?? join(targetCacheDir, "nuget-http"),
		NUGET_SCRATCH: nonEmptyEnv(env?.NUGET_SCRATCH) ?? tempDir,
		TEMP: tempDir,
		TMP: tempDir,
		DOTNET_NOLOGO: nonEmptyEnv(env?.DOTNET_NOLOGO) ?? "1",
		DOTNET_CLI_TELEMETRY_OPTOUT: nonEmptyEnv(env?.DOTNET_CLI_TELEMETRY_OPTOUT) ?? "1",
		DOTNET_SKIP_FIRST_TIME_EXPERIENCE:
			nonEmptyEnv(env?.DOTNET_SKIP_FIRST_TIME_EXPERIENCE) ?? "1",
	};
}

export function portableGitCacheDir(targetCacheDir = cacheDir) {
	return join(targetCacheDir, "portable-git", PORTABLE_GIT_SHA256);
}

function isNonEmptyFile(path) {
	try {
		const stats = statSync(path);
		return stats.isFile() && stats.size > 0;
	} catch {
		return false;
	}
}

export function hasExtractedPortableGit(dir) {
	if (!dir) return false;
	return (
		isNonEmptyFile(join(dir, "cmd", "git.exe")) &&
		isNonEmptyFile(join(dir, "bin", "bash.exe"))
	);
}

function removeTree(path) {
	if (!existsSync(path)) return;
	try {
		chmodSync(path, 0o777);
	} catch {}
	if (statSync(path).isDirectory()) {
		for (const entry of readdirSync(path)) removeTree(join(path, entry));
	}
	try {
		chmodSync(path, 0o666);
	} catch {}
	rmSync(path, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
}

function removeFilesByExtension(root, extension) {
	for (const entry of readdirSync(root, { withFileTypes: true })) {
		const path = join(root, entry.name);
		if (entry.isDirectory()) removeFilesByExtension(path, extension);
		else if (entry.isFile() && path.toLowerCase().endsWith(extension.toLowerCase())) {
			rmSync(path, { force: true });
		}
	}
}

async function renameWithRetry(source, destination) {
	for (let attempt = 1; attempt <= 5; attempt += 1) {
		try {
			renameSync(source, destination);
			return;
		} catch (error) {
			const retryable =
				error &&
				typeof error === "object" &&
				["EPERM", "EACCES", "EBUSY"].includes(error.code);
			if (!retryable || attempt === 5) throw error;
			await new Promise((resolveWait) => setTimeout(resolveWait, attempt * 200));
		}
	}
}

function pipePrefixedLines(stream, label, writer) {
	if (!stream) return;
	let buffer = "";
	stream.setEncoding("utf8");
	stream.on("data", (chunk) => {
		buffer += chunk;
		const lines = buffer.split(/\r?\n/);
		buffer = lines.pop() ?? "";
		for (const line of lines) {
			writer.write(`[${label}] ${line}\n`);
		}
	});
	stream.on("end", () => {
		if (buffer.length > 0) {
			writer.write(`[${label}] ${buffer}\n`);
			buffer = "";
		}
	});
}

const activeChildren = new Set();

function terminateActiveChildren() {
	for (const child of activeChildren) {
		if (child.exitCode !== null) continue;
		try {
			child.kill();
		} catch {}
		if (process.platform === "win32" && child.pid) {
			spawnSync("taskkill.exe", ["/pid", String(child.pid), "/t", "/f"], {
				stdio: "ignore",
				windowsHide: true,
			});
		}
	}
	activeChildren.clear();
}

function runAsync(label, command, args, cwd = rootDir, extraEnv = {}) {
	return new Promise((resolveRun, rejectRun) => {
		const child = spawn(command, args, {
			cwd,
			env: { ...process.env, ...extraEnv },
			stdio: ["ignore", "pipe", "pipe"],
			windowsHide: true,
		});
		activeChildren.add(child);
		pipePrefixedLines(child.stdout, label, process.stdout);
		pipePrefixedLines(child.stderr, label, process.stderr);
		child.once("error", (error) => {
			activeChildren.delete(child);
			rejectRun(error);
		});
		child.once("close", (code, signal) => {
			activeChildren.delete(child);
			if (code === 0) {
				resolveRun();
				return;
			}
			rejectRun(
				new Error(
					signal
						? `[${label}] ${command} exited via signal ${signal}`
						: `[${label}] ${command} exited with code ${code}`,
				),
			);
		});
	});
}

function sha256(path) {
	return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function requireFile(path, label) {
	if (!isNonEmptyFile(path)) {
		throw new Error(`${label} is missing or empty: ${path}`);
	}
	return path;
}

function quotePowerShell(value) {
	return `'${value.replaceAll("'", "''")}'`;
}

async function downloadPinned(label, url, asset, expectedSha256) {
	const cacheArchive = join(cacheDir, "downloads", asset);
	mkdirSync(dirname(cacheArchive), { recursive: true });
	if (existsSync(cacheArchive) && sha256(cacheArchive) !== expectedSha256) {
		console.warn(`Discarding invalid cached ${label} archive.`);
		removeTree(cacheArchive);
	}
	if (existsSync(cacheArchive)) return cacheArchive;

	const partial = `${cacheArchive}.${process.pid}.partial`;
	console.log(`Downloading ${label}...`);
	for (let attempt = 1; attempt <= 3; attempt += 1) {
		removeTree(partial);
		try {
			const response = await fetch(url, {
				signal: AbortSignal.timeout(120_000),
			});
			if (!response.ok) {
				throw new Error(`HTTP ${response.status}`);
			}
			writeFileSync(partial, Buffer.from(await response.arrayBuffer()));
			const actual = sha256(partial);
			if (actual !== expectedSha256) {
				throw new Error(
					`checksum mismatch: expected ${expectedSha256}, got ${actual}`,
				);
			}
			renameSync(partial, cacheArchive);
			return cacheArchive;
		} catch (error) {
			removeTree(partial);
			if (attempt === 3) {
				throw new Error(
					`Failed to download ${label} after ${attempt} attempts: ${error instanceof Error ? error.message : String(error)}`,
				);
			}
			console.warn(`Retrying ${label} download after attempt ${attempt}.`);
			await new Promise((resolveWait) => setTimeout(resolveWait, attempt * 1000));
		}
	}
}

/// Archives come in two shapes: ripgrep's Windows asset is a zip, its Linux assets are tar.gz
/// (the musl builds, so the packaged binary does not care about the host glibc), and the macOS
/// assets are tar.gz/zip. On Windows, prefer System32's bsdtar (GNU tar from Git Bash cannot read
/// zips, and legacy Windows PowerShell 5.1 Expand-Archive can crash on ExtractToDirectory overloads
/// on windows-2022 runners); fall back to pwsh only if System32 tar.exe is absent.
async function extractArchive(label, archive, destination, asset) {
	if (asset.endsWith(".zip")) {
		if (process.platform === "win32") {
			const systemTar = join(process.env.SystemRoot ?? "C:\\Windows", "System32", "tar.exe");
			if (existsSync(systemTar)) {
				await runAsync(label, systemTar, ["-xf", archive, "-C", destination]);
				return;
			}
			await runAsync(label, "pwsh", [
				"-NoProfile",
				"-NonInteractive",
				"-Command",
				`Expand-Archive -LiteralPath ${quotePowerShell(archive)} -DestinationPath ${quotePowerShell(destination)} -Force`,
			]);
			return;
		}
		await runAsync(label, "tar", ["-xf", archive, "-C", destination]);
		return;
	}
	if (asset.endsWith(".tar.gz")) {
		await runAsync(label, "tar", ["-xzf", archive, "-C", destination]);
		return;
	}
	throw new Error(`stage:runtime has no extractor for '${asset}'.`);
}

function markExecutable(path) {
	if (process.platform === "win32") return;
	chmodSync(path, 0o755);
}

async function stageRipgrep(target) {
	// The pin table is keyed by the same target names the runtime matrix uses, and it owns the
	// version, the URL and the checksum — staging must not grow its own copies.
	const pin = ripgrepTarget(target.key);
	const staged = pin.binary;
	const archive = await downloadPinned(`ripgrep for ${target.key}`, pin.url, pin.asset, pin.sha256);
	const extractDir = join(stagingDir, "extract", "ripgrep");
	removeTree(extractDir);
	mkdirSync(extractDir, { recursive: true });
	await extractArchive("ripgrep", archive, extractDir, pin.asset);

	const inside = pin.asset.replace(/\.(tar\.gz|zip)$/, "");
	const extracted = requireFile(join(extractDir, inside, staged), "Extracted ripgrep executable");
	const native = join(stagingDir, "native", "rg", staged);
	mkdirSync(dirname(native), { recursive: true });
	copyFileSync(extracted, native);
	markExecutable(native);
	removeTree(extractDir);
	return native;
}

function extractPortableGit(archive, gitDir) {
	return new Promise((resolveExtract, rejectExtract) => {
		const child = spawn(archive, ["-y", `-o${gitDir}`], {
			cwd: rootDir,
			stdio: ["ignore", "pipe", "pipe"],
			windowsHide: true,
		});
		activeChildren.add(child);
		pipePrefixedLines(child.stdout, "portable-git", process.stdout);
		pipePrefixedLines(child.stderr, "portable-git", process.stderr);
		let settled = false;
		const finish = (error) => {
			if (settled) return;
			settled = true;
			clearInterval(timer);
			activeChildren.delete(child);
			if (child.exitCode === null) {
				try {
					child.kill();
				} catch {}
				if (child.pid) {
					spawnSync("taskkill.exe", ["/pid", String(child.pid), "/t", "/f"], {
						stdio: "ignore",
						windowsHide: true,
					});
				}
			}
			if (error) rejectExtract(error);
			else resolveExtract();
		};
		const started = Date.now();
		const timer = setInterval(() => {
			if (hasExtractedPortableGit(gitDir)) {
				finish();
				return;
			}
			if (Date.now() - started >= 300_000) {
				finish(new Error("PortableGit extraction timed out."));
			}
		}, 500);
		child.once("error", (error) => finish(error));
		child.once("exit", (code) => {
			if (hasExtractedPortableGit(gitDir)) finish();
			else finish(new Error(`PortableGit extractor exited with code ${code}.`));
		});
	});
}

async function stagePortableGit() {
	const cachedDir = portableGitCacheDir(cacheDir);
	const gitDir = join(stagingDir, "git");
	removeTree(gitDir);
	mkdirSync(dirname(gitDir), { recursive: true });

	if (hasExtractedPortableGit(cachedDir)) {
		console.log(`[portable-git] Restoring cached PortableGit from ${cachedDir}...`);
		cpSync(cachedDir, gitDir, { recursive: true });
		requireFile(join(gitDir, "cmd", "git.exe"), "PortableGit git executable");
		requireFile(join(gitDir, "bin", "bash.exe"), "PortableGit Bash executable");
		console.log("[portable-git] PortableGit cache restore completed.");
		return gitDir;
	}

	const archive = await downloadPinned(
		`PortableGit ${PORTABLE_GIT_VERSION}`,
		`https://github.com/git-for-windows/git/releases/download/v${PORTABLE_GIT_VERSION}/${PORTABLE_GIT_ASSET}`,
		PORTABLE_GIT_ASSET,
		PORTABLE_GIT_SHA256,
	);
	const tempExtractDir = join(
		cacheDir,
		"portable-git",
		`${PORTABLE_GIT_SHA256}.${process.pid}.tmp`,
	);
	removeTree(tempExtractDir);
	mkdirSync(dirname(tempExtractDir), { recursive: true });
	try {
		console.log("[portable-git] Extracting PortableGit...");
		await extractPortableGit(archive, tempExtractDir);
		requireFile(join(tempExtractDir, "cmd", "git.exe"), "PortableGit git executable");
		requireFile(join(tempExtractDir, "bin", "bash.exe"), "PortableGit Bash executable");

		removeTree(cachedDir);
		mkdirSync(dirname(cachedDir), { recursive: true });
		await renameWithRetry(tempExtractDir, cachedDir);
	} catch (error) {
		removeTree(tempExtractDir);
		throw error;
	}

	cpSync(cachedDir, gitDir, { recursive: true });
	requireFile(join(gitDir, "cmd", "git.exe"), "PortableGit git executable");
	requireFile(join(gitDir, "bin", "bash.exe"), "PortableGit Bash executable");
	console.log("[portable-git] PortableGit extraction completed.");
	return gitDir;
}

async function stageCore(target, coreExe, dotnetCacheEnv) {
	const output = join(stagingDir, "core");
	console.log(`[core] Publishing Core for ${target.rid}...`);
	await runAsync(
		"core",
		"dotnet",
		[
			"publish",
			join(rootDir, "TinadecCore", "Api", "TinadecCore.Api.csproj"),
			"-c",
			"Release",
			"-r",
			target.rid,
			"--self-contained",
			"true",
			"-p:PublishSingleFile=true",
			"-p:IncludeNativeLibrariesForSelfExtract=true",
			"-p:EnableCompressionInSingleFile=true",
			"-o",
			output,
		],
		rootDir,
		dotnetCacheEnv,
	);
	const config = join(output, "Configuration", "default-agent-runtime.toml");
	if (!existsSync(config)) {
		mkdirSync(dirname(config), { recursive: true });
		copyFileSync(
			join(rootDir, "TinadecCore", "DmaEA", "Configuration", "default-agent-runtime.toml"),
			config,
		);
	}
	requireFile(join(output, coreExe), "Core executable");
	requireFile(join(output, "appsettings.json"), "Core appsettings");
	requireFile(config, "Core runtime TOML");
	console.log("[core] Core publish completed.");
}

async function stageGateway(target, gatewayExe) {
	const output = join(stagingDir, "gateway", gatewayExe);
	mkdirSync(dirname(output), { recursive: true });
	console.log(`[gateway] Compiling Gateway for ${target.bunTarget}...`);
	await runAsync(
		"gateway",
		"bun",
		[
			"build",
			"src/index.ts",
			"--compile",
			`--target=${target.bunTarget}`,
			"--outfile",
			output,
		],
		join(rootDir, "TinadecGateway"),
	);
	requireFile(output, "Gateway executable");
	console.log("[gateway] Gateway compile completed.");
}

async function stageTools(target, toolsExe, dotnetCacheEnv, ripgrepTask) {
	const output = join(stagingDir, "tools");
	const project = join(rootDir, "TinadecTools", "TinadecTools.csproj");
	console.log(`[tools] Publishing TinadecTools for ${target.rid}...`);
	await runAsync(
		"tools",
		"dotnet",
		[
			"publish",
			project,
			"-c",
			"Release",
			"-r",
			target.rid,
			"--self-contained",
			"true",
			"-p:PublishAot=false",
			"-p:PublishSingleFile=true",
			"-o",
			output,
		],
		rootDir,
		dotnetCacheEnv,
	);
	const ripgrep = await ripgrepTask;
	const stagedRipgrep = join(output, ripgrepStagedName(target.key));
	copyFileSync(ripgrep, stagedRipgrep);
	markExecutable(stagedRipgrep);
	const toolsConfig = join(output, "Nlog.config");
	if (!existsSync(toolsConfig)) {
		copyFileSync(join(rootDir, "TinadecTools", "Nlog.config"), toolsConfig);
	}
	requireFile(join(output, toolsExe), "TinadecTools executable");
	requireFile(join(output, "Nlog.config"), "TinadecTools Nlog config");
	requireFile(stagedRipgrep, "Tools ripgrep executable");
	stageBubblewrap({
		rootDir,
		destinationDirectory: output,
		platform: target.platform,
	});
	console.log("[tools] TinadecTools publish completed.");
}

function resolveStageTarget(argv = process.argv) {
	// `--target` selects the shipping matrix entry; the default is whatever this machine can honestly
	// build. Cross-staging was never possible anyway: the Bun standalone and electron-builder both run
	// natively, so a runtime assembled on the wrong OS is one nobody executed.
	const index = argv.indexOf("--target");
	if (index === -1) {
		const inline = argv.find((entry) => entry.startsWith("--target="));
		if (!inline) return hostRuntimeTarget();
		const value = inline.slice("--target=".length);
		if (!value) {
			throw new Error("stage:runtime --target needs a value (for example: --target=linux-x64).");
		}
		return resolveRuntimeTarget(value);
	}
	const requested = argv[index + 1];
	if (!requested || requested.startsWith("--")) {
		throw new Error("stage:runtime --target needs a value (for example: --target=linux-x64).");
	}
	return resolveRuntimeTarget(requested.replace(/^--target=/, "").replace(/^=/, ""));
}

async function main() {
	for (const key of Object.keys(process.env)) {
		if (key.toLowerCase() === "version" || key.toLowerCase() === "ice-version") {
			delete process.env[key];
		}
	}

	const target = resolveStageTarget();
	if (target.platform !== process.platform || target.arch !== process.arch) {
		throw new Error(
			`stage:runtime target '${target.key}' needs a ${target.platform}/${target.arch} host, ` +
				`this machine is ${process.platform}/${process.arch}.`,
		);
	}

	const coreExe = runtimeBinaryName("TinadecCore.Api", target);
	const gatewayExe = runtimeBinaryName("TinadecGateway", target);
	const toolsExe = runtimeBinaryName("TinadecTools", target);
	const dotnetCacheEnv = resolveDotnetCacheEnv(process.env, cacheDir);

	removeTree(stagingDir);
	removeTree(runtimeDir);
	mkdirSync(join(stagingDir, "core"), { recursive: true });
	mkdirSync(join(stagingDir, "gateway"), { recursive: true });
	mkdirSync(join(stagingDir, "tools"), { recursive: true });
	mkdirSync(join(stagingDir, "native"), { recursive: true });
	for (const key of [
		"DOTNET_CLI_HOME",
		"NUGET_PACKAGES",
		"NUGET_HTTP_CACHE_PATH",
		"NUGET_SCRATCH",
		"TEMP",
		"TMP",
	]) {
		mkdirSync(dotnetCacheEnv[key], { recursive: true });
	}

	try {
		const ripgrepTask = stageRipgrep(target);
		const portableGitTask = target.portableGit
			? stagePortableGit()
			: (() => {
					// POSIX ships system git; the service host probes it on PATH and reports the result in
					// diagnostics (electron/serviceManager.cjs). Staging a PortableGit here would be a Windows
					// artifact inside a Linux package.
					console.log(`Skipping PortableGit for ${target.key}: system git is probed at runtime.`);
					return Promise.resolve(null);
				})();
		const gatewayTask = stageGateway(target, gatewayExe);
		const coreTask = stageCore(target, coreExe, dotnetCacheEnv);
		const toolsTask = stageTools(target, toolsExe, dotnetCacheEnv, ripgrepTask);

		await Promise.all([
			ripgrepTask,
			portableGitTask,
			gatewayTask,
			coreTask,
			toolsTask,
		]);

		removeTree(join(stagingDir, "extract"));
		removeFilesByExtension(stagingDir, ".pdb");
		removeTree(runtimeDir);
		await renameWithRetry(stagingDir, runtimeDir);
		console.log(`Staged ${target.key} runtime at ${runtimeDir}.`);
	} catch (error) {
		terminateActiveChildren();
		removeTree(stagingDir);
		throw error;
	}
}

const isDirectRun = Boolean(
	process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url),
);

if (isDirectRun) {
	await main();
}
