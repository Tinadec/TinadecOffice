import assert from "node:assert/strict";
import { test } from "node:test";

import { AGENT_PACK_PRODUCT_ID, channelFacts, mergeChannelCatalogs } from "./officeChannel.mjs";
import { resolveRuntimeTarget, runtimeTargetKeys } from "./runtimeTargets.mjs";

const REQUIRED_TARGETS = runtimeTargetKeys();
const FAKE_SHA = "a".repeat(64);

function buildFragment(
	targetKey,
	{
		version,
		channel,
		releaseTag = `v${version}`,
		artifactLabel = version,
		releaseBaseUrl = `https://github.com/Tinadec/TinadecOffice/releases/download/${encodeURIComponent(releaseTag)}`,
		publishedAt = "2026-11-23T00:00:00.000Z",
	},
) {
	const target = resolveRuntimeTarget(targetKey);
	const facts = channelFacts(target);

	const desktopArtifacts = facts.installerAssets.map((asset) => {
		const file = `TinadecOffice-${artifactLabel}${asset.suffix}`;
		return {
			id: `tinadec-office-desktop-${version}-${asset.idSuffix}`,
			platform: facts.platform,
			architecture: facts.architecture,
			format: "executable",
			url: `${releaseBaseUrl}/${encodeURIComponent(file)}`,
			sizeBytes: 1024,
			sha256: FAKE_SHA,
		};
	});

	const moduleArtifact = (productId, component) => {
		const file = facts.moduleFile(component, version);
		return {
			id: `${productId}-${version}-${targetKey}`,
			platform: facts.platform,
			architecture: facts.architecture,
			format: facts.archiveFormat,
			url: `${releaseBaseUrl}/${encodeURIComponent(file)}`,
			sizeBytes: 512,
			sha256: FAKE_SHA,
		};
	};

	const agentPackFile = facts.agentPackFile(version);

	const products = [
		{
			id: "tinadec-office-desktop",
			name: "TinadecOffice Desktop",
			family: "app",
			productLine: "office",
			packageKind: "full-installer",
			description: "TinadecOffice desktop installer",
			delivery: "portable-exe",
			probe: "process-name",
			probeTarget: "TinadecOffice",
			expectedArtifact: "",
			allowMultipleInstances: true,
			supportsStandaloneLaunch: true,
			installable: true,
			releases: [
				{
					id: `tinadec-office-desktop-${version}`,
					version,
					channel,
					publishedAt,
					artifacts: desktopArtifacts,
					dependencies: [],
					packageMetadata: {
						schemaVersion: 1,
						kind: "full-installer",
						productLine: "office",
						productId: "tinadec-office-desktop",
						component: "desktop",
						releaseVersion: version,
						packageVersion: version,
						platform: facts.platform,
						architecture: facts.architecture,
					},
				},
			],
		},
		{
			id: "tinadec-office-tools",
			name: "TinadecOffice Tools",
			family: "tools",
			productLine: "office",
			packageKind: "runtime-module",
			runtimeRole: "tools",
			description: "Office Tools runtime",
			delivery: "native-exe",
			probe: "process-name",
			probeTarget: "TinadecTools",
			expectedArtifact: facts.entrypoint("tools"),
			allowMultipleInstances: false,
			supportsStandaloneLaunch: false,
			installable: true,
			releases: [
				{
					id: `tinadec-office-tools-${version}`,
					version,
					channel,
					publishedAt,
					artifacts: [moduleArtifact("tinadec-office-tools", "tools")],
					dependencies: [],
					packageMetadata: {
						schemaVersion: 1,
						kind: "runtime-module",
						productLine: "office",
						productId: "tinadec-office-tools",
						component: "tools",
						releaseVersion: version,
						packageVersion: version,
						platform: facts.platform,
						architecture: facts.architecture,
						entrypoint: facts.entrypoint("tools"),
						expectedFiles: facts.expectedFiles("tools"),
					},
				},
			],
		},
		{
			id: "tinadec-office-core",
			name: "TinadecOffice Core",
			family: "core",
			productLine: "office",
			packageKind: "runtime-module",
			runtimeRole: "core",
			description: "Office Core runtime",
			delivery: "dotnet-publish-dir",
			probe: "http-health",
			probeTarget: "http://127.0.0.1:48731/api/v1/health",
			expectedArtifact: facts.entrypoint("core"),
			allowMultipleInstances: false,
			supportsStandaloneLaunch: true,
			installable: true,
			releases: [
				{
					id: `tinadec-office-core-${version}`,
					version,
					channel,
					publishedAt,
					artifacts: [moduleArtifact("tinadec-office-core", "core")],
					dependencies: [{ productId: "tinadec-office-tools", versionRange: `>=${version}`, optional: false }],
					packageMetadata: {
						schemaVersion: 1,
						kind: "runtime-module",
						productLine: "office",
						productId: "tinadec-office-core",
						component: "core",
						releaseVersion: version,
						packageVersion: version,
						platform: facts.platform,
						architecture: facts.architecture,
						entrypoint: facts.entrypoint("core"),
						expectedFiles: facts.expectedFiles("core"),
					},
				},
			],
		},
		{
			id: "tinadec-office-gateway",
			name: "TinadecOffice Gateway",
			family: "gateway",
			productLine: "office",
			packageKind: "runtime-module",
			runtimeRole: "gateway",
			description: "Office Gateway runtime",
			delivery: "native-exe",
			probe: "http-health",
			probeTarget: "http://127.0.0.1:48730/api/v1/health",
			expectedArtifact: facts.entrypoint("gateway"),
			allowMultipleInstances: false,
			supportsStandaloneLaunch: true,
			installable: true,
			releases: [
				{
					id: `tinadec-office-gateway-${version}`,
					version,
					channel,
					publishedAt,
					artifacts: [moduleArtifact("tinadec-office-gateway", "gateway")],
					dependencies: [{ productId: "tinadec-office-core", versionRange: `>=${version}`, optional: false }],
					packageMetadata: {
						schemaVersion: 1,
						kind: "runtime-module",
						productLine: "office",
						productId: "tinadec-office-gateway",
						component: "gateway",
						releaseVersion: version,
						packageVersion: version,
						platform: facts.platform,
						architecture: facts.architecture,
						entrypoint: facts.entrypoint("gateway"),
						expectedFiles: facts.expectedFiles("gateway"),
					},
				},
			],
		},
		{
			id: AGENT_PACK_PRODUCT_ID,
			name: "TinadecOffice AgentPack",
			family: "agent-pack",
			productLine: "office",
			packageKind: "agent-pack",
			description: "Office GraphSeed AgentPack",
			delivery: "agent-pack",
			probe: "file-only",
			probeTarget: "",
			expectedArtifact: "manifest.json",
			allowMultipleInstances: false,
			supportsStandaloneLaunch: false,
			installable: true,
			releases: [
				{
					id: `tinadec-office-agentpack-${version}`,
					version,
					channel,
					publishedAt,
					artifacts: [
						{
							id: `tinadec-office-agentpack-${version}-${targetKey}`,
							platform: facts.platform,
							architecture: facts.architecture,
							format: facts.archiveFormat,
							url: `${releaseBaseUrl}/${encodeURIComponent(agentPackFile)}`,
							sizeBytes: 256,
							sha256: FAKE_SHA,
						},
					],
					dependencies: [{ productId: "tinadec-office-core", versionRange: ">=0.1.0", optional: false }],
					packageMetadata: {
						schemaVersion: 1,
						kind: "agent-pack",
						productLine: "office",
						productId: AGENT_PACK_PRODUCT_ID,
						component: "agentpack",
						releaseVersion: version,
						packageVersion: "0.1.0",
						platform: facts.platform,
						architecture: facts.architecture,
					},
				},
			],
		},
	];

	return {
		file: `catalog-${targetKey}.json`,
		catalog: {
			schemaVersion: 1,
			kind: "office-channel-fragment",
			target: targetKey,
			officeReleaseVersion: version,
			officeReleaseTag: releaseTag,
			officeReleaseChannel: channel,
			generatedAt: publishedAt,
			managerMinimumVersion: "0.1.0",
			releaseBaseUrl,
			products,
		},
	};
}

test("mergeChannelCatalogs merges Stable fragments across all three shipping legs", () => {
	const fragments = REQUIRED_TARGETS.map((targetKey) =>
		buildFragment(targetKey, {
			version: "0.5.0",
			channel: "stable",
			releaseTag: "v0.5.0",
			artifactLabel: "0.5.0",
		}),
	);

	const merged = mergeChannelCatalogs(fragments, {
		version: "0.5.0",
		generatedAt: "2026-11-23T01:00:00.000Z",
		requiredTargets: REQUIRED_TARGETS,
	});

	assert.deepEqual(merged.issues, []);
	assert.ok(merged.catalog);
	assert.equal(
		merged.catalog.releaseBaseUrl,
		"https://github.com/Tinadec/TinadecOffice/releases/download/v0.5.0",
	);
	assert.equal(merged.catalog.products.length, 5);

	const byId = new Map(merged.catalog.products.map((p) => [p.id, p]));
	assert.equal(byId.get("tinadec-office-desktop").expectedArtifact, "");
	assert.equal(byId.get("tinadec-office-tools").expectedArtifact, "TinadecTools*");
	assert.equal(byId.get("tinadec-office-core").expectedArtifact, "TinadecCore.Api*");
	assert.equal(byId.get("tinadec-office-gateway").expectedArtifact, "TinadecGateway*");
	assert.equal(byId.get(AGENT_PACK_PRODUCT_ID).expectedArtifact, "manifest.json");

	for (const product of merged.catalog.products) {
		assert.equal(product.releases.length, 1);
		assert.equal(product.releases[0].version, "0.5.0");
		assert.equal(product.releases[0].channel, "stable");
		const platforms = new Set(product.releases[0].artifacts.map((a) => a.platform));
		assert.deepEqual([...platforms].sort(), ["linux", "macos", "windows"]);
	}
});

test("mergeChannelCatalogs merges Nightly fragments with nightly channel and nightly-723c7d5 releaseBaseUrl", () => {
	const releaseBaseUrl = "https://github.com/Tinadec/TinadecOffice/releases/download/nightly-723c7d5";
	const fragments = REQUIRED_TARGETS.map((targetKey) =>
		buildFragment(targetKey, {
			version: "0.0.0-nightly.723c7d5",
			channel: "nightly",
			releaseTag: "nightly-723c7d5",
			artifactLabel: "nightly-723c7d5",
			releaseBaseUrl,
		}),
	);

	const merged = mergeChannelCatalogs(fragments, {
		version: "0.0.0-nightly.723c7d5",
		generatedAt: "2026-11-23T01:00:00.000Z",
		requiredTargets: REQUIRED_TARGETS,
	});

	assert.deepEqual(merged.issues, []);
	assert.ok(merged.catalog);
	assert.equal(merged.catalog.releaseBaseUrl, releaseBaseUrl);
	assert.equal(merged.catalog.products.length, 5);

	for (const product of merged.catalog.products) {
		const release = product.releases[0];
		assert.equal(release.version, "0.0.0-nightly.723c7d5");
		assert.equal(release.channel, "nightly");
		for (const artifact of release.artifacts) {
			assert.ok(
				artifact.url.startsWith(`${releaseBaseUrl}/`),
				`expected ${artifact.url} to start with ${releaseBaseUrl}/`,
			);
			assert.ok(artifact.packageMetadata);
		}
	}

	const desktop = merged.catalog.products.find((p) => p.id === "tinadec-office-desktop");
	assert.equal(desktop.releases[0].artifacts.length, 4); // setup, portable, deb, dmg
	assert.ok(
		desktop.releases[0].artifacts.some((a) => a.url.endsWith("/TinadecOffice-nightly-723c7d5-win-x64-setup.exe")),
	);
	assert.ok(
		desktop.releases[0].artifacts.some((a) => a.url.endsWith("/TinadecOffice-nightly-723c7d5-linux-x64.deb")),
	);
	assert.ok(
		desktop.releases[0].artifacts.some((a) => a.url.endsWith("/TinadecOffice-nightly-723c7d5-osx-arm64.dmg")),
	);
});

test("mergeChannelCatalogs fails when channels disagree across legs", () => {
	const fragments = [
		buildFragment("win-x64", {
			version: "0.0.0-nightly.723c7d5",
			channel: "nightly",
			releaseTag: "nightly-723c7d5",
			artifactLabel: "nightly-723c7d5",
		}),
		buildFragment("linux-x64", {
			version: "0.0.0-nightly.723c7d5",
			channel: "stable",
			releaseTag: "nightly-723c7d5",
			artifactLabel: "nightly-723c7d5",
		}),
		buildFragment("osx-arm64", {
			version: "0.0.0-nightly.723c7d5",
			channel: "nightly",
			releaseTag: "nightly-723c7d5",
			artifactLabel: "nightly-723c7d5",
		}),
	];

	const merged = mergeChannelCatalogs(fragments, {
		version: "0.0.0-nightly.723c7d5",
		generatedAt: "2026-11-23T01:00:00.000Z",
		requiredTargets: REQUIRED_TARGETS,
	});

	assert.equal(merged.catalog, null);
	assert.ok(
		merged.issues.some((issue) => /channel differs between win-x64 \("nightly"\) and linux-x64 \("stable"\)/.test(issue)),
		`expected channel disagreement issue, got: ${merged.issues.join("; ")}`,
	);
});

test("mergeChannelCatalogs fails when releaseBaseUrl disagrees across legs", () => {
	const fragments = [
		buildFragment("win-x64", {
			version: "0.0.0-nightly.723c7d5",
			channel: "nightly",
			releaseTag: "nightly-723c7d5",
			artifactLabel: "nightly-723c7d5",
			releaseBaseUrl: "https://github.com/Tinadec/TinadecOffice/releases/download/nightly-723c7d5",
		}),
		buildFragment("linux-x64", {
			version: "0.0.0-nightly.723c7d5",
			channel: "nightly",
			releaseTag: "nightly-723c7d5",
			artifactLabel: "nightly-723c7d5",
			releaseBaseUrl: "https://github.com/Tinadec/TinadecOffice/releases/download/nightly-723c7d5",
		}),
		buildFragment("osx-arm64", {
			version: "0.0.0-nightly.723c7d5",
			channel: "nightly",
			releaseTag: "v0.0.0-nightly.723c7d5",
			artifactLabel: "nightly-723c7d5",
			releaseBaseUrl: "https://github.com/Tinadec/TinadecOffice/releases/download/v0.0.0-nightly.723c7d5",
		}),
	];

	const merged = mergeChannelCatalogs(fragments, {
		version: "0.0.0-nightly.723c7d5",
		generatedAt: "2026-11-23T01:00:00.000Z",
		requiredTargets: REQUIRED_TARGETS,
	});

	assert.equal(merged.catalog, null);
	assert.ok(
		merged.issues.some((issue) => /releaseBaseUrl differs between win-x64 .* and osx-arm64/.test(issue)),
		`expected releaseBaseUrl disagreement issue, got: ${merged.issues.join("; ")}`,
	);
});

test("mergeChannelCatalogs fails when a required shipping leg is missing", () => {
	const fragments = ["win-x64", "linux-x64"].map((targetKey) =>
		buildFragment(targetKey, {
			version: "0.5.0",
			channel: "stable",
			releaseTag: "v0.5.0",
		}),
	);

	const merged = mergeChannelCatalogs(fragments, {
		version: "0.5.0",
		generatedAt: "2026-11-23T01:00:00.000Z",
		requiredTargets: REQUIRED_TARGETS,
	});

	assert.equal(merged.catalog, null);
	assert.ok(merged.issues.some((issue) => issue.includes("osx-arm64")));
});
