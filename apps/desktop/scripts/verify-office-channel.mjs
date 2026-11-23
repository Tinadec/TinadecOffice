import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import {
	existsSync,
	mkdtempSync,
	readFileSync,
	readdirSync,
	rmSync,
	statSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { assertBinaryFormat, readHead } from "./binaryFormat.mjs";
import { AGENT_PACK_PRODUCT_ID, ARCHIVE_MAGIC, channelFacts, matchesArchiveMagic } from "./officeChannel.mjs";
import { hostRuntimeTarget } from "./runtimeTargets.mjs";

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = resolve(scriptsDir, "..");
const releaseDir = resolve(process.argv[2] ?? join(desktopDir, "release"));
const channelDir = join(releaseDir, "office-channel");
// Reading an archive should use the binary the consumer uses: bsdtar on Windows (System32 ships it,
// while Git Bash's GNU tar sits earlier on PATH and cannot read zip at all), and the system tar on
// the POSIX legs, where the artifacts are tar.gz.
const systemRoot = process.env.SystemRoot ?? "C:\\Windows";
const archiveTool =
	process.platform === "win32" && existsSync(join(systemRoot, "System32", "tar.exe"))
		? join(systemRoot, "System32", "tar.exe")
		: "tar";
const HASH_RE = /^[a-f0-9]{64}$/u;
const SEMVER_RE = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/u;
const VERSION = (
	process.env.OFFICE_RELEASE_VERSION?.trim() ||
	(process.env.GITHUB_REF_TYPE === "tag" ? process.env.GITHUB_REF_NAME : undefined) ||
	JSON.parse(readFileSync(join(desktopDir, "package.json"), "utf8")).version
).replace(/^v/u, "");
const EXPECTED_TAG = process.env.OFFICE_RELEASE_TAG?.trim() || "";
const EXPECTED_CHANNEL = process.env.OFFICE_RELEASE_CHANNEL?.trim() || "stable";

const TARGET = hostRuntimeTarget();
const FACTS = channelFacts(TARGET);

function fail(message) {
	throw new Error(message);
}

function requireFile(path, label) {
	if (!existsSync(path) || !statSync(path).isFile() || statSync(path).size === 0) {
		fail(`${label} is missing or empty: ${path}`);
	}
	return path;
}

function readJson(path, label) {
	try {
		return JSON.parse(readFileSync(requireFile(path, label), "utf8"));
	} catch (error) {
		fail(`${label} is invalid JSON: ${error instanceof Error ? error.message : String(error)}`);
	}
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

function archiveEntries(archive) {
	const output = execFileSync(archiveTool, ["-tf", archive], {
		encoding: "utf8",
		windowsHide: true,
		maxBuffer: 64 * 1024 * 1024,
	});
	return output.split(/\r?\n/u).map((entry) => entry.trim()).filter(Boolean);
}

// Both archivers write a "./" prefix — bsdtar's zip and GNU tar's tar.gz list "./TinadecTools" — and
// a consumer extracts the plain relative path, so membership and safety checks see the normalized
// form while extraction asks for the listed one. Measured on this machine: GNU tar answers
// "tinadec-package.json: Not found in archive" (exit 2) for the plain name and only reads the stored
// "./" spelling, so an entry read has to go through `rawEntry`.
function normalizeEntry(entry) {
	let normalized = entry.replaceAll("\\", "/");
	while (normalized.startsWith("./")) normalized = normalized.slice(2);
	return normalized.replace(/\/+$/u, "");
}

function isSafeEntry(entry) {
	const normalized = normalizeEntry(entry);
	if (!normalized || normalized === ".") return true;
	if (normalized.startsWith("/") || /^[A-Za-z]:/u.test(normalized)) return false;
	return normalized.split("/").every((part) => part !== ".." && part.length > 0);
}

function rawEntry(entries, expected, archive) {
	const found = entries.find((entry) => normalizeEntry(entry) === expected);
	if (!found) fail(`${archive} is missing required entry ${expected}.`);
	return found;
}

function readEntryText(archive, entries, expected) {
	// Bounded because a self-contained TinadecTools is over 60 MB: the whole-entry `tar -xOf` slurp
	// this replaces died with ENOBUFS on a Windows runner. Only metadata and text resources are read
	// here; binaries are checked from the extracted copy below.
	return execFileSync(archiveTool, ["-xOf", archive, rawEntry(entries, expected, archive)], {
		encoding: "utf8",
		windowsHide: true,
		maxBuffer: 64 * 1024 * 1024,
	});
}

function readEntryJson(archive, entries, expected) {
	try {
		return JSON.parse(readEntryText(archive, entries, expected));
	} catch (error) {
		if (error instanceof Error && error.message.startsWith(`${archive} is missing`)) throw error;
		fail(`${archive}:${expected} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
	}
}

/// The catalog declares a format and a file name is not evidence of it: GNU tar given `-a -cf x.zip`
/// exits 0 having written a tar archive (first bytes `2e 2f 00 00`, not a PK header).
function requireArchiveMagic(archive, format) {
	const head = readHead(archive, 4);
	if (!matchesArchiveMagic(head, format)) {
		fail(
			`${archive} is declared as ${format} but starts with ${head.toString("hex")}; expected ` +
				`${Buffer.from(ARCHIVE_MAGIC[format] ?? []).toString("hex")}.`,
		);
	}
}

async function withExtraction(archive, run) {
	const extractDir = mkdtempSync(join(tmpdir(), `office-channel-verify-${FACTS.key}-`));
	try {
		return await run(extractArchive(archive, extractDir));
	} finally {
		rmSync(extractDir, { recursive: true, force: true, maxRetries: 10, retryDelay: 100 });
	}
}

function extractArchive(archive, destination) {
	execFileSync(archiveTool, ["-xf", archive, "-C", destination], {
		windowsHide: true,
		stdio: ["ignore", "ignore", "inherit"],
	});
	return destination;
}

function walkFileHashes(root) {
	const files = new Map();
	const visit = (directory, prefix) => {
		for (const entry of readdirSync(directory, { withFileTypes: true })) {
			const relative = prefix ? `${prefix}/${entry.name}` : entry.name;
			const full = join(directory, entry.name);
			if (entry.isDirectory()) visit(full, relative);
			else if (entry.isFile()) files.set(relative, sha256(full));
		}
	};
	visit(root, "");
	return files;
}

/// Map a runtime-module archive entry onto its path inside the full app's bundled runtime. Tools
/// merges the native (and, on Windows, PortableGit) staging directories into its root; core and
/// gateway map one-to-one onto their component directory.
function bundledRuntimePath(component, entry) {
	if (component === "tools") {
		const segments = entry.split("/");
		if (segments[0] === "native" || segments[0] === "git") return entry;
		return `tools/${entry}`;
	}
	return `${component}/${entry}`;
}

/// One pass per runtime module: the archive's own metadata, the bytes a consumer lands, and the
/// machine code and execute bit of every entry point. The module archive and the full installer's
/// bundled runtime must be identical — a file in one and not the other is exactly the "full build
/// runs, module package is missing files" drift this channel cannot ship.
async function validateRuntimeModule({ archive, component, bundled }) {
	const label = archive.split(/[\\/]/u).pop();
	const entries = archiveEntries(archive);
	for (const entry of entries) {
		if (!isSafeEntry(entry)) fail(`${label} contains an unsafe archive entry: ${entry}`);
	}
	requireEntryExists(entries, "tinadec-package.json", label);
	const metadata = readEntryJson(archive, entries, "tinadec-package.json");
	const expected = bundled.expectedPackageMetadata;
	for (const key of ["schemaVersion", "kind", "productLine", "productId", "component", "releaseVersion", "packageVersion", "platform", "architecture", "entrypoint", "expectedFiles"]) {
		if (JSON.stringify(metadata[key]) !== JSON.stringify(expected[key])) {
			fail(`${label} metadata field ${key} does not match its expected value.`);
		}
	}
	// Not "windows x64": this is the leg that built it. A fragment that claims another platform is how
	// a Windows runtime would be installed onto a Linux machine.
	if (
		metadata.kind !== "runtime-module" ||
		metadata.productLine !== "office" ||
		metadata.platform !== FACTS.platform ||
		metadata.architecture !== FACTS.architecture
	) {
		fail(`${label} is not an Office ${FACTS.platform} ${FACTS.architecture} runtime module.`);
	}
	for (const expectedFile of metadata.expectedFiles) requireEntryExists(entries, expectedFile, label);

	const covered = new Set();
	await withExtraction(archive, async (root) => {
		const packaged = walkFileHashes(root);
		packaged.delete("tinadec-package.json");
		for (const [entry, digest] of packaged) {
			const bundledEntry = bundledRuntimePath(component, entry);
			if (!bundled.files.has(bundledEntry)) {
				fail(`${label} ships ${entry}, which the full app's bundled runtime does not carry.`);
			}
			if (bundled.files.get(bundledEntry) !== digest) {
				fail(`${label} file ${entry} differs from the full app's bundled runtime copy.`);
			}
			covered.add(bundledEntry);
		}
		for (const expectedFile of FACTS.expectedFiles(component)) {
			if (!packaged.has(expectedFile)) fail(`${label} does not extract ${expectedFile} into place.`);
		}
		for (const executable of FACTS.executables(component)) {
			const path = join(root, ...executable.split("/"));
			requireFile(path, `${label}:${executable}`);
			// Headers are read from the extracted file in bounded pieces: a self-contained
			// TinadecTools is over 60 MB, and `tar -xOf` of it used to die with ENOBUFS.
			await assertBinaryFormat(path, TARGET, `${label}:${executable}`);
			if (TARGET.platform !== "win32" && (statSync(path).mode & 0o100) === 0) {
				fail(
					`${label}:${executable} extracts without an owner-execute bit (mode ${statSync(path).mode.toString(8)}); ` +
						"the Manager would install a module that cannot be spawned.",
				);
			}
		}
	});
	return { entries, metadata, covered };
}

function requireEntryExists(entries, expected, label) {
	if (!entries.some((entry) => normalizeEntry(entry) === expected)) {
		fail(`${label} is missing required entry ${expected}.`);
	}
}

function validateAgentPackArchive(archive, expectedMetadata) {
	const label = archive.split(/[\\/]/u).pop();
	const entries = archiveEntries(archive);
	for (const entry of entries) {
		if (!isSafeEntry(entry)) fail(`${label} contains an unsafe archive entry: ${entry}`);
	}
	for (const expected of ["tinadec-package.json", "manifest.json", "envelope.json"]) {
		requireEntryExists(entries, expected, label);
	}
	const metadata = readEntryJson(archive, entries, "tinadec-package.json");
	const manifest = readEntryJson(archive, entries, "manifest.json");
	const envelope = readEntryJson(archive, entries, "envelope.json");
	const digest = createHash("sha256").update(canonicalize(manifest)).digest("hex");
	if (metadata.kind !== "agent-pack" || metadata.productLine !== "office") {
		fail(`${label} is not an Office AgentPack archive.`);
	}
	if (
		metadata.platform !== FACTS.platform ||
		metadata.architecture !== FACTS.architecture
	) {
		fail(`${label} is not an AgentPack for ${FACTS.platform} ${FACTS.architecture}.`);
	}
	if (metadata.agentPack?.digest !== digest || envelope.integrity?.digest !== digest) {
		fail(`${label} AgentPack digest does not match manifest.json.`);
	}
	if (metadata.agentPack?.packId !== manifest.metadata?.pack_id) {
		fail(`${label} AgentPack pack id does not match manifest.json.`);
	}
	if (metadata.packageVersion !== manifest.metadata?.version) {
		fail(`${label} AgentPack version does not match manifest.json.`);
	}
	if (!HASH_RE.test(digest) || !SEMVER_RE.test(String(metadata.packageVersion))) {
		fail(`${label} AgentPack identity is malformed.`);
	}
	for (const key of ["schemaVersion", "kind", "productId", "component", "releaseVersion", "packageVersion", "platform", "architecture", "agentPack"]) {
		if (JSON.stringify(metadata[key]) !== JSON.stringify(expectedMetadata[key])) {
			fail(`${label} metadata field ${key} does not match its expected value.`);
		}
	}
	return { entries, metadata, digest };
}

function verifyArtifactHash(artifact, archive) {
	const actualSize = statSync(archive).size;
	const actualHash = sha256(archive);
	if (actualSize !== artifact.sizeBytes) fail(`${archive} size ${actualSize} does not match catalog ${artifact.sizeBytes}.`);
	if (actualHash !== artifact.sha256) fail(`${archive} SHA-256 ${actualHash} does not match catalog ${artifact.sha256}.`);
}

function catalogRelease(catalog, productId) {
	const product = catalog.products.find((candidate) => candidate.id === productId);
	if (!product) fail(`Catalog is missing product ${productId}.`);
	const release = product.releases.find((candidate) => candidate.version === VERSION);
	if (!release) fail(`Catalog is missing ${productId}@${VERSION}.`);
	return { product, release };
}

function catalogArtifact(catalog, productId, archiveName) {
	const { product, release } = catalogRelease(catalog, productId);
	const artifact = release.artifacts.find((candidate) => decodeURIComponent(candidate.url).endsWith(archiveName));
	if (!artifact) fail(`Catalog is missing artifact ${archiveName} for ${productId}.`);
	return { product, release, artifact };
}

if (!existsSync(channelDir) || !statSync(channelDir).isDirectory()) fail(`Office channel directory is missing: ${channelDir}`);
if (!SEMVER_RE.test(VERSION)) fail(`Office release version is missing or invalid: ${VERSION}`);

const fragmentFile = `catalog-${FACTS.key}.json`;
const fragment = readJson(join(channelDir, fragmentFile), "Office channel fragment");
if (fragment.schemaVersion !== 1 || fragment.kind !== "office-channel-fragment") {
	fail(`${fragmentFile} is not a v1 Office channel fragment.`);
}
if (fragment.target !== FACTS.key) fail(`${fragmentFile} describes target ${fragment.target}, but this is the ${FACTS.key} leg.`);
if (fragment.officeReleaseVersion !== VERSION) {
	fail(`${fragmentFile} was packaged for ${fragment.officeReleaseVersion}, verifying ${VERSION}.`);
}
if (!Array.isArray(fragment.products)) fail(`${fragmentFile} has no products array.`);
if (EXPECTED_TAG) {
	const expectedSuffix = `/releases/download/${encodeURIComponent(EXPECTED_TAG)}`;
	if (typeof fragment.releaseBaseUrl !== "string" || !fragment.releaseBaseUrl.endsWith(expectedSuffix)) {
		fail(
			`${fragmentFile} releaseBaseUrl ${JSON.stringify(fragment.releaseBaseUrl)} does not match expected tag ${EXPECTED_TAG}.`,
		);
	}
}

const moduleNames = {
	tools: FACTS.moduleFile("tools", VERSION),
	core: FACTS.moduleFile("core", VERSION),
	gateway: FACTS.moduleFile("gateway", VERSION),
};
const agentPackName = FACTS.agentPackFile(VERSION);
const checksums = readFileSync(requireFile(join(channelDir, "SHA256SUMS"), "Office channel checksums"), "utf8");
for (const name of [...Object.values(moduleNames), agentPackName]) {
	const archive = requireFile(join(channelDir, name), "Office channel archive");
	if (!checksums.includes(`${sha256(archive)}  ${name}`)) fail(`SHA256SUMS does not contain ${name}.`);
	requireArchiveMagic(archive, FACTS.archiveFormat);
}

const picks = {
	tools: catalogArtifact(fragment, "tinadec-office-tools", moduleNames.tools),
	core: catalogArtifact(fragment, "tinadec-office-core", moduleNames.core),
	gateway: catalogArtifact(fragment, "tinadec-office-gateway", moduleNames.gateway),
	agentPack: catalogArtifact(fragment, AGENT_PACK_PRODUCT_ID, agentPackName),
};
for (const [name, pick] of Object.entries({
	[moduleNames.tools]: picks.tools,
	[moduleNames.core]: picks.core,
	[moduleNames.gateway]: picks.gateway,
	[agentPackName]: picks.agentPack,
})) {
	verifyArtifactHash(pick.artifact, join(channelDir, name));
}

// The installers are the other half of the release: the fragment must point at the files this leg
// actually produced, with the bytes they actually have.
const desktop = catalogRelease(fragment, "tinadec-office-desktop");
if (desktop.release.artifacts.length !== FACTS.installerAssets.length) {
	fail(
		`Desktop product carries ${desktop.release.artifacts.length} artifacts, expected ${FACTS.installerAssets.length} on ${FACTS.key}.`,
	);
}
for (const artifact of desktop.release.artifacts) {
	const file = decodeURIComponent(artifact.url).split("/").pop();
	if (!FACTS.installerAssets.some((asset) => file.endsWith(asset.suffix))) {
		fail(`Desktop artifact ${artifact.id} points at ${file}, which the ${FACTS.key} leg does not ship.`);
	}
	verifyArtifactHash(artifact, requireFile(join(releaseDir, file), `Installer ${file}`));
}

const bundledRuntimeDir = FACTS.unpackedRuntimeDir(releaseDir);
if (!existsSync(bundledRuntimeDir)) {
	// Name what the leg actually produced: a missing path is either a wrong table entry or a packager
	// that wrote somewhere else, and the listing tells them apart without another run.
	const listing = existsSync(releaseDir) ? readdirSync(releaseDir).slice(0, 12).join(", ") : "(release directory missing)";
	fail(
		`Full app bundled runtime is missing, so module archives cannot be cross-checked: ${bundledRuntimeDir} ` +
			`— ${releaseDir} holds: ${listing}`,
	);
}
const bundledFiles = walkFileHashes(bundledRuntimeDir);
const covered = new Set();
const modules = {};
for (const component of ["tools", "core", "gateway"]) {
	const result = await validateRuntimeModule({
		archive: join(channelDir, moduleNames[component]),
		component,
		bundled: { files: bundledFiles, expectedPackageMetadata: picks[component].release.packageMetadata },
	});
	modules[component] = result;
	for (const entry of result.covered) covered.add(entry);
}
const agentPackValidation = validateAgentPackArchive(join(channelDir, agentPackName), picks.agentPack.release.packageMetadata);

const missing = [...bundledFiles.keys()].filter((entry) => !covered.has(entry)).sort();
if (missing.length !== 0) {
	fail(
		`Bundled runtime files missing from the module archives: ${missing.slice(0, 10).join(", ")}${missing.length > 10 ? ` (+${missing.length - 10} more)` : ""}`,
	);
}

if (picks.tools.release.dependencies.length !== 0) fail("Tools must not have runtime dependencies.");
if (!picks.core.release.dependencies.some((dependency) => dependency.productId === "tinadec-office-tools")) {
	fail("Core must depend on Office Tools.");
}
if (!picks.gateway.release.dependencies.some((dependency) => dependency.productId === "tinadec-office-core")) {
	fail("Gateway must depend on Office Core.");
}
if (!picks.agentPack.release.dependencies.some((dependency) => dependency.productId === "tinadec-office-core")) {
	fail("AgentPack must declare a Core dependency.");
}
for (const product of fragment.products) {
	for (const release of product.releases) {
		if (release.channel !== EXPECTED_CHANNEL) {
			fail(
				`${product.id} release ${release.id} declares channel ${JSON.stringify(release.channel)}, expected ${JSON.stringify(EXPECTED_CHANNEL)}.`,
			);
		}
		for (const artifact of release.artifacts) {
			if (artifact.platform !== FACTS.platform || artifact.architecture !== FACTS.architecture) {
				fail(
					`${product.id} artifact ${artifact.id} claims ${artifact.platform}/${artifact.architecture} on the ${FACTS.key} leg.`,
				);
			}
			if (EXPECTED_TAG && (typeof artifact.url !== "string" || !artifact.url.startsWith(`${fragment.releaseBaseUrl}/`))) {
				fail(
					`${product.id} artifact ${artifact.id} URL ${JSON.stringify(artifact.url)} does not start with ${fragment.releaseBaseUrl}/.`,
				);
			}
		}
	}
}

console.log(
	`Office channel verification passed for v${VERSION} on ${FACTS.key}: ${modules.tools.entries.length} Tools entries, ` +
		`${FACTS.archiveFormat} archives, AgentPack ${agentPackValidation.digest}, ` +
		`${bundledFiles.size} bundled runtime files matched byte-for-byte.`,
);
