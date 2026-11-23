import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import {
	chmodSync,
	copyFileSync,
	existsSync,
	mkdirSync,
	readdirSync,
	readFileSync,
	rmSync,
	statSync,
	writeFileSync,
} from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { AGENT_PACK_PRODUCT_ID, CHANNEL_COMPONENTS, channelFacts } from "./officeChannel.mjs";
import { hostRuntimeTarget } from "./runtimeTargets.mjs";

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = resolve(scriptsDir, "..");
const rootDir = resolve(desktopDir, "..", "..");
const releaseDir = join(desktopDir, "release");
const channelDir = join(releaseDir, "office-channel");
const runtimeDir = join(desktopDir, "runtime");
const agentPackManifestPath = join(
	desktopDir,
	"src",
	"agentPacks",
	"GraphSeedPack",
	"manifest.json",
);
const defaultRepository = "Tinadec/TinadecOffice";

// The archiver is bsdtar wherever it is needed: on Windows System32 always ships it, while GNU tar
// from Git Bash sits earlier on PATH, cannot create zip archives at all, and treats drive-letter
// paths as remote host specs. On the POSIX legs the archive is tar.gz, which the system tar writes
// natively — and there it *must* be the system tar, because that is the same binary the Manager
// will use to read the artifact back.
const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
const archiveTool =
	process.platform === "win32" && existsSync(join(systemRoot, "System32", "tar.exe"))
		? join(systemRoot, "System32", "tar.exe")
		: "tar";

function fail(message) {
	throw new Error(message);
}

function requireFile(path, label) {
	if (!existsSync(path) || !statSync(path).isFile() || statSync(path).size === 0) {
		fail(`${label} is missing or empty: ${path}`);
	}
	return path;
}

function requireDirectory(path, label) {
	if (!existsSync(path) || !statSync(path).isDirectory()) {
		fail(`${label} is missing: ${path}`);
	}
	return path;
}

function removeTree(path) {
	rmSync(path, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
}

function sha256(path) {
	return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function canonicalize(value) {
	if (value === null || typeof value === "boolean" || typeof value === "string") {
		return JSON.stringify(value);
	}
	if (typeof value === "number") {
		if (!Number.isFinite(value)) fail("AgentPack manifest contains a non-finite number.");
		return JSON.stringify(value);
	}
	if (Array.isArray(value)) return `[${value.map(canonicalize).join(",")}]`;
	return `{${Object.keys(value)
		.sort()
		.map((key) => `${JSON.stringify(key)}:${canonicalize(value[key])}`)
		.join(",")}}`;
}

function readJson(path, label) {
	try {
		return JSON.parse(readFileSync(requireFile(path, label), "utf8"));
	} catch (error) {
		fail(`${label} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
	}
}

function releaseVersion() {
	const requested =
		process.env.OFFICE_RELEASE_VERSION?.trim() ||
		(process.env.GITHUB_REF_TYPE === "tag" ? process.env.GITHUB_REF_NAME : undefined);
	const raw = (requested || readJson(join(desktopDir, "package.json"), "Desktop package.json").version)
		.replace(/^v/u, "");
	if (!/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u.test(raw)) {
		fail(`Invalid Office release version: ${raw}`);
	}
	return raw;
}

function packageRoot(name) {
	const path = join(channelDir, name);
	removeTree(path);
	mkdirSync(path, { recursive: true });
	return path;
}

function copyTree(source, destination) {
	requireDirectory(source, `Source directory for ${relative(rootDir, source)}`);
	mkdirSync(destination, { recursive: true });
	for (const entry of readdirSync(source, { withFileTypes: true })) {
		const sourcePath = join(source, entry.name);
		const destinationPath = join(destination, entry.name);
		if (entry.isDirectory()) copyTree(sourcePath, destinationPath);
		else if (entry.isFile()) {
			copyFileSync(sourcePath, destinationPath);
			// Copy the mode deliberately: `stage-runtime.mjs` chmods every staged binary to 0755, and
			// an archive records whatever mode the file has when the archiver runs. Whether a plain
			// file copy carries that bit over is host-dependent — so the channel copy states it, and
			// the verifier's execute-bit check on the *extracted* copy is the reading that decides.
			chmodSync(destinationPath, statSync(sourcePath).mode);
		}
	}
}

function writeJson(path, value) {
	writeFileSync(path, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function createArchive(source, output) {
	removeTree(output);
	mkdirSync(dirname(output), { recursive: true });
	execFileSync(archiveTool, [...FACTS.tarCreateArgs, output, "-C", source, "."], {
		cwd: rootDir,
		stdio: "inherit",
		windowsHide: true,
	});
	requireFile(output, `Office channel archive ${output}`);
}

function packageMetadata({ productId, component, packageVersion, entrypoint, expectedFiles, agentPack }) {
	return {
		schemaVersion: 1,
		kind: agentPack ? "agent-pack" : "runtime-module",
		productLine: "office",
		productId,
		component,
		releaseVersion: VERSION,
		packageVersion,
		platform: FACTS.platform,
		architecture: FACTS.architecture,
		...(entrypoint ? { entrypoint } : {}),
		...(expectedFiles ? { expectedFiles } : {}),
		...(agentPack ? { agentPack } : {}),
	};
}

function artifactMetadata({ id, file, format = FACTS.archiveFormat, sourceDir = channelDir }) {
	const sourcePath = join(sourceDir, file);
	return {
		id,
		platform: FACTS.platform,
		architecture: FACTS.architecture,
		format,
		url: `${releaseBaseUrl}/${encodeURIComponent(file)}`,
		sizeBytes: statSync(sourcePath).size,
		sha256: sha256(sourcePath),
	};
}

function runtimePackage({ component, source, extraSources, entrypoint, expectedFiles }) {
	const productId = CHANNEL_COMPONENTS[component].productId;
	const root = packageRoot(productId);
	copyTree(source, root);
	for (const [directory, sourcePath] of extraSources ?? []) {
		copyTree(sourcePath, join(root, directory));
	}
	const metadata = packageMetadata({ productId, component, packageVersion: VERSION, entrypoint, expectedFiles });
	writeJson(join(root, "tinadec-package.json"), metadata);
	const file = FACTS.moduleFile(component, VERSION);
	createArchive(root, join(channelDir, file));
	return { file, metadata };
}

function agentPackPackage() {
	const root = packageRoot(AGENT_PACK_PRODUCT_ID);
	const manifest = readJson(agentPackManifestPath, "GraphSeedPack manifest");
	const digest = createHash("sha256").update(canonicalize(manifest)).digest("hex");
	const packId = manifest?.metadata?.pack_id;
	const packVersion = manifest?.metadata?.version;
	const minimumCoreVersion = manifest?.compatibility?.minimum_core_version;
	if (typeof packId !== "string" || typeof packVersion !== "string" || typeof minimumCoreVersion !== "string") {
		fail("GraphSeedPack manifest is missing pack identity or Core compatibility.");
	}
	copyFileSync(agentPackManifestPath, join(root, "manifest.json"));
	writeJson(join(root, "envelope.json"), {
		manifest,
		integrity: { algorithm: "sha256", digest },
	});
	const metadata = packageMetadata({
		productId: AGENT_PACK_PRODUCT_ID,
		component: "agentpack",
		packageVersion: packVersion,
		agentPack: {
			packId,
			digest,
			minimumCoreVersion,
			manifestPath: "manifest.json",
			envelopePath: "envelope.json",
		},
	});
	writeJson(join(root, "tinadec-package.json"), metadata);
	const file = FACTS.agentPackFile(VERSION);
	createArchive(root, join(channelDir, file));
	return { file, metadata, packId, packVersion, digest, minimumCoreVersion };
}

function findReleaseArtifact(suffix, label) {
	const matches = readdirSync(releaseDir).filter(
		(name) => name.endsWith(suffix) && statSync(join(releaseDir, name)).isFile(),
	);
	if (matches.length !== 1) fail(`Expected one ${label} ending in ${suffix}, found ${matches.length}.`);
	return matches[0];
}

const RELEASE_TOKEN_RE = /^[0-9A-Za-z._-]+$/u;

function releaseTag(version) {
	const raw = process.env.OFFICE_RELEASE_TAG?.trim() || `v${version}`;
	if (!RELEASE_TOKEN_RE.test(raw)) {
		fail(`Invalid Office release tag: ${raw}`);
	}
	return raw;
}

function releaseChannel() {
	const raw = process.env.OFFICE_RELEASE_CHANNEL?.trim() || "stable";
	if (!RELEASE_TOKEN_RE.test(raw)) {
		fail(`Invalid Office release channel: ${raw}`);
	}
	return raw;
}

const VERSION = releaseVersion();
const RELEASE_TAG = releaseTag(VERSION);
const RELEASE_CHANNEL = releaseChannel();
const TARGET = hostRuntimeTarget();
const FACTS = channelFacts(TARGET);
const repository = process.env.GITHUB_REPOSITORY ?? defaultRepository;
const releaseBaseUrl = `https://github.com/${repository}/releases/download/${encodeURIComponent(RELEASE_TAG)}`;
requireDirectory(runtimeDir, `Staged ${TARGET.key} desktop runtime`);
requireDirectory(releaseDir, "Desktop release directory");
removeTree(channelDir);
mkdirSync(channelDir, { recursive: true });

const desktopPackage = readJson(join(desktopDir, "package.json"), "Desktop package.json");
if (desktopPackage.version !== VERSION) {
	fail(`Desktop package version ${desktopPackage.version} does not match Office release ${VERSION}.`);
}
const corePackage = runtimePackage({
	component: "core",
	source: join(runtimeDir, "core"),
	entrypoint: FACTS.entrypoint("core"),
	expectedFiles: FACTS.expectedFiles("core"),
});
const gatewayPackage = runtimePackage({
	component: "gateway",
	source: join(runtimeDir, "gateway"),
	entrypoint: FACTS.entrypoint("gateway"),
	expectedFiles: FACTS.expectedFiles("gateway"),
});
const toolsPackage = runtimePackage({
	component: "tools",
	source: join(runtimeDir, "tools"),
	extraSources: FACTS.extraSources("tools").map(([directory, name]) => [directory, join(runtimeDir, name)]),
	entrypoint: FACTS.entrypoint("tools"),
	expectedFiles: FACTS.expectedFiles("tools"),
});
const agentPack = agentPackPackage();
const installerAssets = FACTS.installerAssets.map((asset) => ({
	asset,
	file: findReleaseArtifact(asset.suffix, `${asset.idSuffix} installer`),
}));

const products = [
	{
		id: "tinadec-office-desktop",
		name: "TinadecOffice Desktop",
		family: "app",
		productLine: "office",
		packageKind: "full-installer",
		description: "完整 TinadecOffice 工作台，一键安装 Core、Gateway、Tools 和桌面端。",
		delivery: "portable-exe",
		probe: "process-name",
		probeTarget: "TinadecOffice",
		// No platform-neutral post-extract marker exists for a single-file installer: the Manager
		// stages it as `staging/<artifact.id>`, so a release-asset name can never match. The asset
		// names are in `artifacts[].url`, which is where they belong.
		expectedArtifact: "",
		allowMultipleInstances: true,
		supportsStandaloneLaunch: true,
		installable: true,
		releases: [{
			id: `tinadec-office-desktop-${VERSION}`,
			version: VERSION,
			channel: RELEASE_CHANNEL,
			publishedAt: new Date().toISOString(),
			artifacts: installerAssets.map(({ asset, file }) =>
				artifactMetadata({ id: `tinadec-office-desktop-${VERSION}-${asset.idSuffix}`, file, format: "executable", sourceDir: releaseDir }),
			),
			dependencies: [],
			packageMetadata: {
				schemaVersion: 1,
				kind: "full-installer",
				productLine: "office",
				productId: "tinadec-office-desktop",
				component: "desktop",
				releaseVersion: VERSION,
				packageVersion: desktopPackage.version,
				platform: FACTS.platform,
				architecture: FACTS.architecture,
			},
		}],
	},
	{
		id: "tinadec-office-tools",
		name: "TinadecOffice Tools",
		family: "tools",
		productLine: "office",
		packageKind: "runtime-module",
		runtimeRole: "tools",
		description: "Office 渠道的工具宿主、ripgrep 运行时；Windows 一份还携带 PortableGit。",
		delivery: "native-exe",
		probe: "process-name",
		probeTarget: "TinadecTools",
		expectedArtifact: FACTS.entrypoint("tools"),
		allowMultipleInstances: false,
		supportsStandaloneLaunch: false,
		installable: true,
		releases: [{
			id: `tinadec-office-tools-${VERSION}`,
			version: VERSION,
			channel: RELEASE_CHANNEL,
			publishedAt: new Date().toISOString(),
			artifacts: [artifactMetadata({ id: `tinadec-office-tools-${VERSION}-${TARGET.key}`, file: toolsPackage.file })],
			dependencies: [],
			packageMetadata: toolsPackage.metadata,
		}],
	},
	{
		id: "tinadec-office-core",
		name: "TinadecOffice Core",
		family: "core",
		productLine: "office",
		packageKind: "runtime-module",
		runtimeRole: "core",
		description: "Office 渠道的 TinadecCore 本地服务运行时。",
		delivery: "dotnet-publish-dir",
		probe: "http-health",
		probeTarget: "http://127.0.0.1:48731/api/v1/health",
		expectedArtifact: FACTS.entrypoint("core"),
		allowMultipleInstances: false,
		supportsStandaloneLaunch: true,
		installable: true,
		releases: [{
			id: `tinadec-office-core-${VERSION}`,
			version: VERSION,
			channel: RELEASE_CHANNEL,
			publishedAt: new Date().toISOString(),
			artifacts: [artifactMetadata({ id: `tinadec-office-core-${VERSION}-${TARGET.key}`, file: corePackage.file })],
			dependencies: [{ productId: "tinadec-office-tools", versionRange: `>=${VERSION}`, optional: false }],
			packageMetadata: corePackage.metadata,
		}],
	},
	{
		id: "tinadec-office-gateway",
		name: "TinadecOffice Gateway",
		family: "gateway",
		productLine: "office",
		packageKind: "runtime-module",
		runtimeRole: "gateway",
		description: "Office 渠道的 Gateway 本地代理运行时。",
		delivery: "native-exe",
		probe: "http-health",
		probeTarget: "http://127.0.0.1:48730/api/v1/health",
		expectedArtifact: FACTS.entrypoint("gateway"),
		allowMultipleInstances: false,
		supportsStandaloneLaunch: true,
		installable: true,
		releases: [{
			id: `tinadec-office-gateway-${VERSION}`,
			version: VERSION,
			channel: RELEASE_CHANNEL,
			publishedAt: new Date().toISOString(),
			artifacts: [artifactMetadata({ id: `tinadec-office-gateway-${VERSION}-${TARGET.key}`, file: gatewayPackage.file })],
			dependencies: [{ productId: "tinadec-office-core", versionRange: `>=${VERSION}`, optional: false }],
			packageMetadata: gatewayPackage.metadata,
		}],
	},
	{
		id: AGENT_PACK_PRODUCT_ID,
		name: "TinadecOffice AgentPack",
		family: "agent-pack",
		productLine: "office",
		packageKind: "agent-pack",
		description: "Office 渠道的 GraphSeed AgentPack，安装后由 TinadecOffice 交给 Core 工作区安装。",
		delivery: "agent-pack",
		probe: "file-only",
		probeTarget: "",
		expectedArtifact: "manifest.json",
		allowMultipleInstances: false,
		supportsStandaloneLaunch: false,
		installable: true,
		releases: [{
			id: `tinadec-office-agentpack-${VERSION}`,
			version: VERSION,
			channel: RELEASE_CHANNEL,
			publishedAt: new Date().toISOString(),
			artifacts: [artifactMetadata({ id: `tinadec-office-agentpack-${VERSION}-${TARGET.key}`, file: agentPack.file })],
			dependencies: [{ productId: "tinadec-office-core", versionRange: `>=${agentPack.minimumCoreVersion}`, optional: false }],
			packageMetadata: agentPack.metadata,
		}],
	},
];

// One fragment per leg: three runners build three platforms, and only the release job can see all
// of them. Writing `catalog.json` here would have put three same-named files into one artifact set.
writeJson(join(channelDir, `catalog-${TARGET.key}.json`), {
	schemaVersion: 1,
	kind: "office-channel-fragment",
	target: TARGET.key,
	officeReleaseVersion: VERSION,
	officeReleaseTag: RELEASE_TAG,
	officeReleaseChannel: RELEASE_CHANNEL,
	generatedAt: new Date().toISOString(),
	managerMinimumVersion: "0.1.0",
	releaseBaseUrl,
	products,
});
writeFileSync(
	join(channelDir, "SHA256SUMS"),
	readdirSync(channelDir)
		.filter((name) => name.endsWith(FACTS.archiveExt) || name === `catalog-${TARGET.key}.json`)
		.sort()
		.map((name) => `${sha256(join(channelDir, name))}  ${name}`)
		.join("\n") + "\n",
	"utf8",
);
console.log(`Created ${TARGET.key} Office channel fragments for ${VERSION} in ${channelDir}.`);
for (const name of readdirSync(channelDir).filter(
	(entry) => entry.endsWith(FACTS.archiveExt) || entry === `catalog-${TARGET.key}.json`,
)) {
	console.log(`  ${name}: ${statSync(join(channelDir, name)).size} bytes`);
}
