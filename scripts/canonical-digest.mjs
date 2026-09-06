#!/usr/bin/env node
/**
 * 规范化 JSON（键名递归排序、无空白、UTF-8）后输出 sha256 hex。
 *
 * 与 TinadecCore AgentPackService 的 envelope 完整性摘要使用同一规范形
 * （System.Text.Json Utf8JsonWriter Indented=false + 属性名 Ordinal 排序），
 * 供 e2e-local-loop.ps1 在安装 Agent Pack 前计算 integrity.digest。
 *
 * 用法：node scripts/canonical-digest.mjs <path-to-json>
 */
import { readFileSync } from "node:fs";
import { createHash } from "node:crypto";

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value && typeof value === "object") {
    const out = {};
    for (const key of Object.keys(value).sort()) out[key] = canonical(value[key]);
    return out;
  }
  return value;
}

const file = process.argv[2];
if (!file) {
  console.error("usage: node scripts/canonical-digest.mjs <path-to-json>");
  process.exit(1);
}
// PowerShell 5.1 `Out-File -Encoding utf8` writes a BOM; JSON.parse rejects it.
const raw = readFileSync(file, "utf8").replace(/^\uFEFF/, "");
const parsed = JSON.parse(raw);
const serialized = JSON.stringify(canonical(parsed));
console.log(createHash("sha256").update(serialized, "utf8").digest("hex"));
