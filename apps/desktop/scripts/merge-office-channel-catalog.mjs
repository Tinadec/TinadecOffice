import { createHash } from "node:crypto";
import { existsSync, readFileSync, readdirSync, statSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { readHead } from "./binaryFormat.mjs";
import { mergeChannelCatalogs, matchesArchiveMagic } from "./officeChannel.mjs";
import { runtimeTargetKeys } from "./runtimeTargets.mjs";

/// Join the three legs' catalog fragments into the one `catalog.json` a release publishes.
///
/// No single runner can produce this file: staging is native-only, so the Windows leg has never seen
/// a Linux runtime and the Linux leg cannot read a zip. The release job is the first place all three
/// artifacts exist together, which makes it the only honest place to write the manifest that points
/// at them — and the only place a cross-platform mix-up can be caught before a user downloads it.
const scriptsDir = dirname(fileURLToPath(import.meta.url));
const desktopDir = resolve(scriptsDir, "..");
const channelArg = process.argv[2];
const channelDir = resolve(channelArg ?? join(desktopDir, "release", "office-channel"));

const VERSION = (
	process.env.OFFICE_RELEASE_VERSION?.trim() ||
	(process.env.GITHUB_REF_TYPE === "tag" ? process.env.GITHUB_REF_NAME : undefined) ||
	JSON.parse(readFileSync(join(desktopDir, "package.json"), "utf8")).version
).replace(/^v/u, "");
const EXPECTED_TAG = process.env.OFFICE_RELEASE_TAG?.trim() || "";
const EXPECTED_CHANNEL = process.env.OFFICE_RELEASE_CHANNEL?.trim() || "";

function sha256(path) {
	return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function readJson(path) {
	try {
		return JSON.parse(readFileSync(path, "utf8"));
	} catch (error) {
		throw new Error(`${path} is not valid JSON: ${error instanceof Error ? error.message : String(error)}`);
	}
}

function fail(messages) {
	for (const message of messages) console.error(`merge: ${message}`);
	throw new Error(`Office channel catalog merge failed with ${messages.length} problem(s).`);
}

const requiredTargets = runtimeTargetKeys();
const fragments = [];
for (const listing of readdirSync(channelDir)) {
	if (!/^catalog-(.+)\.json$/u.test(listing)) continue;
	const file = join(channelDir, listing);
	const catalog = readJson(file);
	fragments.push({ file, catalog });
}
// A fragment for a target that is not in the shipping matrix is a stale file from an older runner
// image, and merging it would publish an artifact nobody built in this release.
for (const fragment of fragments) {
	const target = fragment.catalog?.target;
	if (target && !requiredTargets.includes(target)) {
		fail([`${fragment.file} claims target ${target}, which is not a shipping target (${requiredTargets.join(", ")}).`]);
	}
}

const merged = mergeChannelCatalogs(fragments, {
	version: VERSION,
	generatedAt: new Date().toISOString(),
	requiredTargets,
});
if (merged.issues.length !== 0) fail(merged.issues);

/// Artifacts are published flat: installers sit next to the extracted download, module archives
/// inside `office-channel/`. The catalog URL only carries the file name, so resolving it means
/// looking in both.
function resolveArtifact(name) {
	const candidates = [join(channelDir, name), join(dirname(channelDir), name)];
	return candidates.find((path) => existsSync(path) && statSync(path).isFile());
}

const problems = [];
if (EXPECTED_TAG) {
	const expectedSuffix = `/releases/download/${encodeURIComponent(EXPECTED_TAG)}`;
	if (typeof merged.catalog.releaseBaseUrl !== "string" || !merged.catalog.releaseBaseUrl.endsWith(expectedSuffix)) {
		problems.push(
			`Merged catalog releaseBaseUrl ${JSON.stringify(merged.catalog.releaseBaseUrl)} does not match expected tag ${EXPECTED_TAG}.`,
		);
	}
}
if (EXPECTED_CHANNEL) {
	for (const product of merged.catalog.products) {
		const actualChannel = product.releases?.[0]?.channel;
		if (actualChannel !== EXPECTED_CHANNEL) {
			problems.push(
				`${product.id} release declares channel ${JSON.stringify(actualChannel)}, expected ${JSON.stringify(EXPECTED_CHANNEL)}.`,
			);
		}
	}
}
let checked = 0;
for (const product of merged.catalog.products) {
	for (const release of product.releases) {
		for (const artifact of release.artifacts) {
			const name = decodeURIComponent(artifact.url).split("/").pop();
			const path = resolveArtifact(name);
			if (!path) {
				problems.push(`${product.id} artifact ${artifact.id} points at ${name}, which is not in this release.`);
				continue;
			}
			if (statSync(path).size !== artifact.sizeBytes) {
				problems.push(`${name} is ${statSync(path).size} bytes but ${product.id}/${artifact.id} declares ${artifact.sizeBytes}.`);
			}
			if (sha256(path) !== artifact.sha256) {
				problems.push(`${name} SHA-256 does not match ${product.id}/${artifact.id}.`);
			}
			if (artifact.format === "zip" || artifact.format === "tar-gz") {
				const head = readHead(path, 4);
				if (!matchesArchiveMagic(head, artifact.format)) {
					problems.push(`${name} is declared as ${artifact.format} but starts with ${head.toString("hex")}.`);
				}
			}
			checked += 1;
		}
	}
}
if (problems.length !== 0) fail(problems);

const output = join(channelDir, "catalog.json");
writeFileSync(output, `${JSON.stringify(merged.catalog, null, 2)}\n`, "utf8");
const platforms = new Set(
	merged.catalog.products.flatMap((product) =>
		product.releases.flatMap((release) => release.artifacts.map((artifact) => `${artifact.platform}/${artifact.architecture}`)),
	),
);
console.log(
	`Merged ${fragments.length} channel fragments into ${output}: ${merged.catalog.products.length} products, ` +
		`${checked} artifacts verified on disk across ${[...platforms].sort().join(", ")}.`,
);
