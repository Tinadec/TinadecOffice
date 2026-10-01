import { createRequire } from "node:module";
import {
	existsSync,
	readFileSync,
	readdirSync,
	statSync,
} from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const { extractFile, listPackage } = require("@electron/asar");
const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = resolve(scriptsDir, "..");
const productName = "TinadecOffice";

function requireWindowsX64() {
	if (process.platform !== "win32" || process.arch !== "x64") {
		throw new Error("Windows package verification requires native Windows x64.");
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

function peMachine(path) {
	const bytes = readFileSync(requireFile(path, "Windows PE file"));
	if (bytes.length < 0x40 || bytes.readUInt16LE(0) !== 0x5a4d) {
		throw new Error(`Windows executable has no PE header: ${path}`);
	}
	const peOffset = bytes.readUInt32LE(0x3c);
	if (peOffset + 6 > bytes.length || bytes.readUInt32LE(peOffset) !== 0x00004550) {
		throw new Error(`Windows executable has no PE signature: ${path}`);
	}
	return bytes.readUInt16LE(peOffset + 4);
}

function requireAmd64File(path, label) {
	if (peMachine(path) !== 0x8664) {
		throw new Error(`${label} is not Windows x64: ${path}`);
	}
	return path;
}

function findSingleFile(directory, predicate, label) {
	const matches = readdirSync(directory, { withFileTypes: true })
		.filter((entry) => entry.isFile() && predicate(entry.name))
		.map((entry) => join(directory, entry.name));
	if (matches.length !== 1) {
		throw new Error(
			`Expected one ${label} in ${directory}; found ${matches.length}.`,
		);
	}
	return requireFile(matches[0], label);
}

function verifyInstallableArtifacts(releaseDir) {
	const installer = findSingleFile(
		releaseDir,
		(name) =>
			name.startsWith(`${productName}-`) &&
			name.endsWith("-win-x64-setup.exe"),
		"Windows NSIS setup",
	);
	const portable = findSingleFile(
		releaseDir,
		(name) =>
			name.startsWith(`${productName}-`) &&
			name.endsWith("-win-x64-portable.exe"),
		"Windows portable package",
	);
	const latestPath = join(releaseDir, "latest.yml");
	const blockmapPath = `${installer}.blockmap`;
	const latestExists = existsSync(latestPath);
	const blockmapExists = existsSync(blockmapPath);
	if (latestExists !== blockmapExists) {
		throw new Error(
			`Incomplete builder update metadata: ${latestExists ? blockmapPath : latestPath} is missing.`,
		);
	}
	if (latestExists) {
		requireFile(latestPath, "Builder latest.yml");
		requireFile(blockmapPath, "Builder NSIS blockmap");
		const latest = readFileSync(latestPath, "utf8");
		if (!latest.includes(installer.split(/[\\/]/u).at(-1))) {
			throw new Error(`latest.yml does not reference ${installer}.`);
		}
	}
	const installerName = installer.split(/[\\/]/u).at(-1);
	const versionMatch = installerName?.match(/^TinadecOffice-(.+)-win-x64-setup\.exe$/u);
	if (!versionMatch) throw new Error(`Cannot derive version from ${String(installerName)}.`);
	return {
		installer,
		portable,
		updateMetadata: latestExists,
		expectedVersion: versionMatch[1],
	};
}

function verifyAsar(resources, expectedVersion) {
	const appAsar = requireFile(join(resources, "app.asar"), "app.asar");
	const entries = new Set(
		listPackage(appAsar).map((entry) =>
			entry.replaceAll("\\", "/").replace(/^\/+/, ""),
		),
	);
	for (const entry of [
		"package.json",
		"dist/index.html",
		"electron/main.cjs",
		"electron/preload.cjs",
		"electron/serviceManager.cjs",
	]) {
		if (!entries.has(entry)) throw new Error(`app.asar is missing ${entry}.`);
	}
	if ([...entries].some((entry) => entry.endsWith(".test.cjs"))) {
		throw new Error("app.asar contains Electron test files.");
	}
	let manifest;
	try {
		manifest = JSON.parse(extractFile(appAsar, "package.json").toString("utf8"));
	} catch (error) {
		throw new Error(
			`Cannot read the app.asar package manifest: ${error instanceof Error ? error.message : String(error)}`,
		);
	}
	if (manifest.name !== "@tinadec/desktop") {
		throw new Error(`Unexpected app.asar package name: ${String(manifest.name)}`);
	}
	if (manifest.main !== "electron/main.cjs") {
		throw new Error(`Unexpected app.asar main entry: ${String(manifest.main)}`);
	}
	if (manifest.version !== expectedVersion) {
		throw new Error(
			`Unexpected app.asar version ${String(manifest.version)}; expected ${expectedVersion}.`,
		);
	}
	return { appAsar, entries: entries.size };
}

function verifyUnpackedNodePty(resources) {
	const root = requireDirectory(
		join(resources, "app.asar.unpacked", "node_modules", "node-pty"),
		"Unpacked node-pty module",
	);
	requireFile(join(root, "package.json"), "Unpacked node-pty manifest");
	const nativeRoot = requireDirectory(
		join(root, "prebuilds", "win32-x64"),
		"Unpacked node-pty win32-x64 prebuilds",
	);
	for (const file of [
		"pty.node",
		"conpty.node",
		"conpty_console_list.node",
		"winpty-agent.exe",
		"winpty.dll",
		join("conpty", "OpenConsole.exe"),
		join("conpty", "conpty.dll"),
	]) {
		const path = join(nativeRoot, file);
		requireAmd64File(path, `node-pty native file ${file}`);
	}
	return nativeRoot;
}

function verifyRuntime(resources) {
	const runtime = requireDirectory(join(resources, "runtime"), "Bundled runtime");
	const core = requireDirectory(join(runtime, "core"), "Bundled Core runtime");
	const gateway = requireDirectory(
		join(runtime, "gateway"),
		"Bundled Gateway runtime",
	);
	const tools = requireDirectory(join(runtime, "tools"), "Bundled Tools runtime");
	const native = requireDirectory(join(runtime, "native"), "Bundled native tools");
	const git = requireDirectory(join(runtime, "git"), "Bundled PortableGit");
	const required = [
		requireAmd64File(
			join(core, "TinadecCore.Api.exe"),
			"Core executable",
		),
		requireFile(join(core, "appsettings.json"), "Core appsettings"),
		requireFile(
			join(core, "Configuration", "default-agent-runtime.toml"),
			"Core runtime TOML",
		),
		requireAmd64File(
			join(gateway, "TinadecGateway.exe"),
			"Gateway executable",
		),
		requireAmd64File(
			join(tools, "TinadecTools.exe"),
			"TinadecTools executable",
		),
		requireFile(join(tools, "Nlog.config"), "TinadecTools config"),
		requireAmd64File(join(tools, "rg.exe"), "Tools ripgrep executable"),
		requireAmd64File(join(native, "rg", "rg.exe"), "Native ripgrep executable"),
		requireAmd64File(join(git, "cmd", "git.exe"), "PortableGit git executable"),
		requireAmd64File(
			join(git, "bin", "bash.exe"),
			"PortableGit Bash executable",
		),
	];
	return required;
}

export function verifyPackageOutput(releaseDir = join(desktopDir, "release")) {
	requireWindowsX64();
	const resolvedReleaseDir = resolve(releaseDir);
	requireDirectory(resolvedReleaseDir, "Electron Builder release directory");
	const unpackedRoot = requireDirectory(
		join(resolvedReleaseDir, "win-unpacked"),
		"Windows unpacked directory",
	);
	const resources = requireDirectory(
		join(unpackedRoot, "resources"),
		"Packaged resources directory",
	);
	requireAmd64File(
		join(unpackedRoot, `${productName}.exe`),
		"Packaged desktop executable",
	);
	requireFile(join(unpackedRoot, "resources.pak"), "Electron resources.pak");
	requireFile(join(unpackedRoot, "icudtl.dat"), "Electron ICU data");
	const locales = requireDirectory(
		join(unpackedRoot, "locales"),
		"Electron locales directory",
	);
	requireFile(join(locales, "en-US.pak"), "Electron en-US locale");
	const artifacts = verifyInstallableArtifacts(resolvedReleaseDir);
	const asar = verifyAsar(resources, artifacts.expectedVersion);
	const nodePty = verifyUnpackedNodePty(resources);
	const runtimeFiles = verifyRuntime(resources);
	console.log(
		`Windows package output passed: ${artifacts.installer}; ${artifacts.portable}; update metadata ${artifacts.updateMetadata ? "present" : "not produced"}.`,
	);
	console.log(
		`Verified app.asar (${asar.entries} entries), node-pty (${nodePty}), and ${runtimeFiles.length} runtime files.`,
	);
	for (const path of runtimeFiles) {
		console.log(`  ${relative(resources, path)}`);
	}
	return {
		releaseDir: resolvedReleaseDir,
		unpackedRoot,
		resources,
		executable: join(unpackedRoot, `${productName}.exe`),
		installer: artifacts.installer,
		portable: artifacts.portable,
		updateMetadata: artifacts.updateMetadata,
		appAsar: asar.appAsar,
		nodePty,
		runtimeFiles,
	};
}

const invokedAsScript = process.argv[1]
	? resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url))
	: false;
if (invokedAsScript) {
	try {
		verifyPackageOutput(process.argv[2]);
	} catch (error) {
		console.error(error instanceof Error ? error.message : String(error));
		process.exitCode = 1;
	}
}
