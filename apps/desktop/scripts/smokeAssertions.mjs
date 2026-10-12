/// The facts a packaged build has to prove, shared by the Windows and POSIX smokes.
///
/// These predicates are the smoke's contract with Core and Gateway. Two copies of them would drift
/// the same way the platform paths did: one leg keeps passing while the other asserts something
/// subtly different, and the difference only shows up as a red run on the platform nobody built on.
///
/// The URLs are the same on every platform because the packaged app owns the loopback topology:
/// Gateway on 48730, Core on 48731, and the tool runtime on 48732.

import { randomBytes } from "node:crypto";

export const coreHealthUrl = "http://127.0.0.1:48731/api/v1/health";
export const coreReadinessUrl = "http://127.0.0.1:48731/api/v1/readiness";
export const gatewayHealthUrl = "http://127.0.0.1:48730/api/v1/health";
export const toolsManifestUrl = "http://127.0.0.1:48730/api/v1/tools";
export const toolsReadinessUrl = "http://127.0.0.1:48730/api/v1/tool-layer-readiness";
export const harnessManifestUrl = "http://127.0.0.1:48730/api/v1/harness/manifest";

/// The ports a smoke run must have to itself, and must hand back when it is done. 48732 is the tool
/// runtime the packaged app spawns, so "ports freed" is only a real teardown proof if it is in the
/// list.
export const smokePorts = [48730, 48731, 48732];

/// The credential the packaged host mints for its own child services.
///
/// Every mounted API request is a host operation, and Core refuses one that cannot present this
/// (`host_authorization_required`). A smoke that probes readiness without it does not measure a
/// broken build: it measures its own omission, and every leg times out on HTTP 403 while Core and
/// Gateway are demonstrably up in their logs.
///
/// Core and Gateway inherit the token from the Electron main process that spawned them, so the
/// smoke supplies it in the child environment and echoes the same value back in the header.
export const SMOKE_HOST_CONTROL_ENV = "TINADEC_HOST_CONTROL_TOKEN";

/// The header name, spelled the way Core reads it.
export const SMOKE_HOST_CONTROL_HEADER = "x-tinadec-host-control";

/// A fresh 32-byte key per run, in the shape `createHostControl` accepts.
export function newSmokeHostControlToken() {
	return randomBytes(32).toString("base64url");
}

/// Request headers for a probe that is a trusted host operation. Health stays public, so it is
/// deliberately not gated on the token — that is what lets a smoke distinguish "Core never came up"
/// from "Core is up and refused us".
export function smokeHeaders(token, { public: isPublic = false } = {}) {
	return {
		accept: "application/json",
		"cache-control": "no-store",
		...(token && !isPublic ? { [SMOKE_HOST_CONTROL_HEADER]: token } : {}),
	};
}

export function validateCoreHealth(value) {
	return (
		value?.name === "tinadec-core" &&
		value?.status === "ok" &&
		value?.version === "0.1.0"
	);
}

export function validateGatewayHealth(value) {
	return value?.gateway === "ok" && value?.core_status === "ready";
}

export function validateCoreReadiness(value) {
	return (
		["ready", "degraded", "blocked"].includes(value?.status) &&
		Array.isArray(value?.items)
	);
}

export function validateToolsManifest(value) {
	return Array.isArray(value) && value.length > 0;
}

export function validateToolsReadiness(value) {
	return (
		Array.isArray(value?.tools) &&
		value.tools.length > 0 &&
		Number(value?.tool_count) > 0 &&
		value?.status === "ready"
	);
}

export function validateHarnessManifest(value) {
	return (
		typeof value?.runtime === "string" &&
		value.runtime.length > 0 &&
		value?.tool_registry &&
		typeof value.tool_registry === "object" &&
		Array.isArray(value?.modules)
	);
}

/// The ordered checks, so a new leg cannot skip one by forgetting to copy a call. Each entry names
/// itself the way the log should, because "timeout waiting for HTTP 200" tells a builder nothing.
///
/// `public: true` marks the two health routes Core leaves ungated. They come first and stay open on
/// purpose: if every probe were authenticated, a wrong token and a dead Core would both read as
/// "timed out", and the smoke could not say which. Credentialed checks come after, so a 403 there
/// means the token is wrong, not that the product is broken.
export const smokeChecks = [
	{ url: coreHealthUrl, validate: validateCoreHealth, label: "Core health", key: "coreHealth", public: true },
	{ url: gatewayHealthUrl, validate: validateGatewayHealth, label: "Gateway health", key: "gatewayHealth", public: true },
	{ url: coreReadinessUrl, validate: validateCoreReadiness, label: "Core readiness", key: "coreReadiness" },
	{ url: toolsManifestUrl, validate: validateToolsManifest, label: "bundled tools manifest", key: "toolsManifest" },
	{ url: toolsReadinessUrl, validate: validateToolsReadiness, label: "bundled tools readiness", key: "toolsReadiness" },
	{ url: harnessManifestUrl, validate: validateHarnessManifest, label: "bundled harness manifest", key: "harnessManifest" },
];
