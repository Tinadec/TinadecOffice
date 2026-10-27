// Streaming Markdown cost: MarkdownRender re-parses the whole reply on every published update.
// Measures marked.parse + highlight of every fenced block (DOMPurify is excluded: it needs a DOM,
// so the real per-update cost is higher than this).
import { performance } from 'node:perf_hooks'
import { readFileSync } from 'node:fs'
import { marked } from 'marked'
import hljs from 'highlight.js/lib/core'
import typescript from 'highlight.js/lib/languages/typescript'
import csharp from 'highlight.js/lib/languages/csharp'

hljs.registerLanguage('typescript', typescript)
hljs.registerLanguage('csharp', csharp)

// A realistic long reply built from real repository code: prose, a table, and code fences.
const ts = readFileSync('apps/desktop/src/lib/sessionEventBus.ts', 'utf8')
const cs = readFileSync('TinadecCore/Runtime/TinaChatWakeService.cs', 'utf8')
const prose = '这一段解释改动的原因与边界，包含 `inline code` 与 **强调**，并引用 TinadecCore/Lifecycle/StorageLifecycleService.cs:523。\n\n'
const table = '| 项 | 改前 | 改后 |\n|---|---|---|\n' + Array.from({ length: 12 }, (_, i) => `| 指标 ${i} | ${i * 7} ms | ${i * 3} ms |`).join('\n') + '\n\n'
const reply = [prose.repeat(6), table, '```typescript\n' + ts + '\n```\n\n', prose.repeat(6), '```csharp\n' + cs + '\n```\n\n', prose.repeat(10)].join('')

function render(content) {
  const html = marked.parse(content, { breaks: true, gfm: true, async: false })
  // MarkdownRender highlights every <pre><code> block on every parse.
  for (const match of content.matchAll(/```(\w+)\n([\s\S]*?)(?:```|$)/g)) {
    if (hljs.getLanguage(match[1])) hljs.highlight(match[2], { language: match[1] })
  }
  return html
}

const CHUNK = 256 // Core flushes model output at 256 chars or 100 ms (ModelOutputStream.cs:45)
render(reply) // warm up JIT
let total = 0
let updates = 0
let worst = 0
for (let end = CHUNK; end < reply.length + CHUNK; end += CHUNK) {
  const start = performance.now()
  render(reply.slice(0, end))
  const spent = performance.now() - start
  total += spent
  worst = Math.max(worst, spent)
  updates++
}
const once = performance.now()
render(reply)
const single = performance.now() - once
console.log(`reply length         ${reply.length} chars`)
console.log(`streamed updates     ${updates}`)
console.log(`single final parse   ${single.toFixed(1)} ms`)
console.log(`last-update parse    ${worst.toFixed(1)} ms (worst)`)
console.log(`sum over the stream  ${total.toFixed(0)} ms  (${(total / single).toFixed(0)}x a single parse)`)
