---
name: tinadec-market
description: Browse, preview, install and remove skills or MCP servers from Tinadec market sources. Use when the user asks to install an extension, add a market source, or diagnose a failed installation.
version: 1.0.0
---

# Tinadec market

The market is one catalog filtered by kind (`skill`, `mcp-server`, `acp-adapter`). Sources are governed configuration documents; installs never write directly — they queue a user-tool action that a human approves.

## Procedure

1. Read the sources: `tinadec_inspect` with `target: "market"`, or the market page.
2. Add a source only with the user's URL and kind. Supported kinds come from Core; an unsupported kind is refused, not silently ignored.
3. Preview first. The preview pins the version/commit, package digest, target path, env names and warnings. It writes nothing.
4. Apply the proposal. It creates an approval; the installation status changes only after approval and the follow-up poll.
5. Verify: the catalog row shows installed, the Settings → Tools list shows the resource, and a new run's skill index or `mcp_list` sees it.

## Failure codes

| Code | Meaning |
| --- | --- |
| `unsupported_market_source_kind` | this build cannot serve that source kind |
| `target_unresolved` | the package would leave its project root |
| `skill_hash_conflict` | the reviewed bytes changed; preview again |
| `mcp_id_conflict` | a server with that id already exists in the scope |
| `package_changed` | the installed package was edited outside Core; reinstall |

## Boundaries

- A project skill cannot be uninstalled from the market; disable it or remove it with governed file tools.
- A shared skill is content-addressed and immutable per version; reinstall to change it.
- Uninstalling never deletes user files it did not install.
- The market does not execute skill scripts. Installation only puts files in place.
