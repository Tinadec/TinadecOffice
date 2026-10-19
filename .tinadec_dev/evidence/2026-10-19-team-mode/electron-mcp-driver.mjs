import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const mode = process.argv[2] ?? "list";
const electron = new Client({ name: "tinadec-team-audit", version: "1.0.0" }, { capabilities: {} });
const transport = new StdioClientTransport({
  command: process.platform === "win32" ? "npx.cmd" : "npx",
  args: ["-y", "electron-mcp-server"],
  env: { ...process.env, SECURITY_LEVEL: "balanced" },
});

try {
  await electron.connect(transport);
  if (mode === "list") {
    console.log(JSON.stringify(await electron.listTools(), null, 2));
  } else if (mode === "call") {
    const name = process.argv[3];
    const args = JSON.parse(process.argv[4] ?? "{}");
    if (!name) throw new Error("Tool name is required for call mode.");
    console.log(JSON.stringify(await electron.callTool({ name, arguments: args }), null, 2));
  } else {
    throw new Error(`Unknown mode: ${mode}`);
  }
} finally {
  await electron.close();
}
