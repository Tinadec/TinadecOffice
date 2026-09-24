import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
	chmodSync,
	copyFileSync,
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

for (const key of Object.keys(process.env)) {
	if (key.toLowerCase() === "version" || key.toLowerCase() === "ice-version") {
		delete process.env[key];
	}
}

const desktopDir = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const rootDir = resolve(desktopDir, "..", "..");
const runtimeDir = join(desktopDir, "runtime");
const cacheDir = join(desktopDir, ".runtime-cache");
const stagingDir = join(cacheDir, "runtime.staging");
const npmCli = process.env.npm_execpath;
const rid = "win-x64";
const RIPGREP_VERSION = "15.2.0";
const RIPGREP_ASSET = `ripgrep-${RIPGREP_VERSION}-x86_64-pc-windows-msvc.zip`;
const RIPGREP_SHA256 =
	"71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5";
const PORTABLE_GIT_VERSION = "2.55.0.windows.3";
const PORTABLE_GIT_ASSET = "PortableGit-2.55.0.3-64-bit.7z.exe";
const PORTABLE_GIT_SHA256 =
	"ab00566336b5472120f9a52d34f2e79c5406535792acb0548001ffd0bd090e5d";

if (process.platform !== "win32" || process.arch !== "x64") {
	throw new Error("stage:runtime must run on native Windows x64.");
}
if (!npmCli) {
	throw new Error("stage:runtime must be launched through npm.");
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

const dotnetCacheEnv = {
	DOTNET_CLI_HOME: join(cacheDir, "dotnet"),
	NUGET_PACKAGES: join(cacheDir, "nuget"),
	NUGET_HTTP_CACHE_PATH: join(cacheDir, "nuget-http"),
	NUGET_SCRATCH: join(cacheDir, "temp"),
	TEMP: join(cacheDir, "temp"),
	TMP: join(cacheDir, "temp"),
};

function run(command, args, cwd = rootDir, extraEnv = {}, timeout) {
	const result = spawnSync(command, args, {
		cwd,
		env: { ...process.env, ...extraEnv },
		stdio: "inherit",
		windowsHide: true,
		...(timeout ? { timeout } : {}),
	});
	if (result.error) throw result.error;
	if (result.status !== 0) {
		throw new Error(`${command} exited with code ${result.status}`);
	}
}

function sha256(path) {
	return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function isNonEmptyFile(path) {
	return existsSync(path) && statSync(path).isFile() && statSync(path).size > 0;
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

function extractZip(archive, destination) {
	run(
		"powershell.exe",
		[
			"-NoProfile",
			"-NonInteractive",
			"-Command",
			`Expand-Archive -LiteralPath ${quotePowerShell(archive)} -DestinationPath ${quotePowerShell(destination)} -Force`,
		],
		rootDir,
	);
}

async function stageRipgrep() {
	const archive = await downloadPinned(
		`ripgrep ${RIPGREP_VERSION}`,
		`https://github.com/BurntSushi/ripgrep/releases/download/${RIPGREP_VERSION}/${RIPGREP_ASSET}`,
		RIPGREP_ASSET,
		RIPGREP_SHA256,
	);
	const extractDir = join(stagingDir, "extract", "ripgrep");
	removeTree(extractDir);
	mkdirSync(extractDir, { recursive: true });
	extractZip(archive, extractDir);
	const extracted = requireFile(
		join(extractDir, RIPGREP_ASSET.replace(/\.zip$/, ""), "rg.exe"),
		"Extracted ripgrep executable",
	);
	const native = join(stagingDir, "native", "rg", "rg.exe");
	const tools = join(stagingDir, "tools", "rg.exe");
	mkdirSync(dirname(native), { recursive: true });
	mkdirSync(dirname(tools), { recursive: true });
	copyFileSync(extracted, native);
	copyFileSync(extracted, tools);
	removeTree(join(stagingDir, "extract"));
	return native;
}

function extractPortableGit(archive, gitDir) {
	return new Promise((resolve, reject) => {
		const child = spawn(archive, ["-y", `-o${gitDir}`], {
			cwd: rootDir,
			stdio: "inherit",
			windowsHide: true,
		});
		let settled = false;
		const finish = (error) => {
			if (settled) return;
			settled = true;
			clearInterval(timer);
			if (child.exitCode === null) {
				child.kill();
				spawnSync("taskkill.exe", ["/pid", String(child.pid), "/t", "/f"], {
					stdio: "ignore",
					windowsHide: true,
				});
			}
			if (error) reject(error);
			else resolve();
		};
		const required = [
			join(gitDir, "cmd", "git.exe"),
			join(gitDir, "bin", "bash.exe"),
		];
		const started = Date.now();
		const timer = setInterval(() => {
			if (required.every(isNonEmptyFile)) {
				finish();
				return;
			}
			if (Date.now() - started >= 300_000) {
				finish(new Error("PortableGit extraction timed out."));
			}
		}, 500);
		child.once("error", (error) => finish(error));
		child.once("exit", (code) => {
			if (required.every(isNonEmptyFile)) finish();
			else finish(new Error(`PortableGit extractor exited with code ${code}.`));
		});
	});
}

async function stagePortableGit() {
	const archive = await downloadPinned(
		`PortableGit ${PORTABLE_GIT_VERSION}`,
		`https://github.com/git-for-windows/git/releases/download/v${PORTABLE_GIT_VERSION}/${PORTABLE_GIT_ASSET}`,
		PORTABLE_GIT_ASSET,
		PORTABLE_GIT_SHA256,
	);
	const gitDir = join(stagingDir, "git");
	removeTree(gitDir);
	console.log("Extracting PortableGit...");
	await extractPortableGit(archive, gitDir);
	console.log("PortableGit extraction completed.");
	requireFile(join(gitDir, "cmd", "git.exe"), "PortableGit git executable");
	requireFile(join(gitDir, "bin", "bash.exe"), "PortableGit Bash executable");
	return gitDir;
}

function stageCore() {
	const output = join(stagingDir, "core");
	console.log("Publishing Core...");
	run(
		"dotnet",
		[
			"publish",
			join(rootDir, "TinadecCore", "Api", "TinadecCore.Api.csproj"),
			"-c",
			"Release",
			"-r",
			rid,
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
	requireFile(join(output, "TinadecCore.Api.exe"), "Core executable");
	requireFile(join(output, "appsettings.json"), "Core appsettings");
	requireFile(config, "Core runtime TOML");
	console.log("Core publish completed.");
}

function stageGateway() {
	const output = join(stagingDir, "gateway", "TinadecGateway.exe");
	mkdirSync(dirname(output), { recursive: true });
	console.log("Compiling Gateway...");
	run(
		"bun",
		[
			"build",
			"src/index.ts",
			"--compile",
			"--target=bun-windows-x64",
			"--outfile",
			output,
		],
		join(rootDir, "TinadecGateway"),
	);
	requireFile(output, "Gateway executable");
	console.log("Gateway compile completed.");
}

function stageTools(ripgrep) {
	const output = join(stagingDir, "tools");
	const project = join(rootDir, "TinadecTools", "TinadecTools.csproj");
	console.log("Publishing TinadecTools...");
	run(
		"dotnet",
		[
			"publish",
			project,
			"-c",
			"Release",
			"-r",
			rid,
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
	copyFileSync(ripgrep, join(output, "rg.exe"));
	const toolsConfig = join(output, "Nlog.config");
	if (!existsSync(toolsConfig)) {
		copyFileSync(join(rootDir, "TinadecTools", "Nlog.config"), toolsConfig);
	}
	requireFile(join(output, "TinadecTools.exe"), "TinadecTools executable");
	requireFile(join(output, "Nlog.config"), "TinadecTools Nlog config");
	console.log("TinadecTools publish completed.");
}

removeTree(stagingDir);
removeTree(runtimeDir);
mkdirSync(join(stagingDir, "core"), { recursive: true });
mkdirSync(join(stagingDir, "gateway"), { recursive: true });
mkdirSync(join(stagingDir, "tools"), { recursive: true });
mkdirSync(join(stagingDir, "native"), { recursive: true });
mkdirSync(join(stagingDir, "git"), { recursive: true });
for (const directory of Object.values(dotnetCacheEnv)) {
	mkdirSync(directory, { recursive: true });
}

try {
	const ripgrep = await stageRipgrep();
	await stagePortableGit();
	stageCore();
	stageGateway();
	stageTools(ripgrep);
	removeFilesByExtension(stagingDir, ".pdb");
	removeTree(runtimeDir);
	renameSync(stagingDir, runtimeDir);
	console.log(`Staged win-x64 runtime at ${runtimeDir}.`);
} catch (error) {
	removeTree(stagingDir);
	throw error;
}
