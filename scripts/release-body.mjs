#!/usr/bin/env node
/**
 * 组装并发布 GitHub Release 正文：头图 → 手写正文 → 折叠的提交记录 → 折叠的发布信息。
 *
 * 为什么要有这个脚本：正文有两个作者，分开管。
 *   - 机械部分（海报、安装表、平台边界）每版一模一样，由 `.github/release-header.md` 与
 *     `.github/release-footer.md` 提供；
 *   - 人话部分（这版到底变好了什么）每版都要重新读懂 diff 才写得出来，由发版前后的 AI
 *     复盘后写进 `docs/releases/<tag>.md`。
 * AI 换一版只改一个 md，模板与拼接逻辑不用碰；自动生成的 PR 标题列表则降级成折叠的
 * 「完整提交记录」，不再占据正文主体。
 *
 * 单一实现：CI 的 desktop-release.yml 和本地补写调用同一个函数，避免两处拼接慢慢走样。
 *
 * 用法：
 *   node scripts/release-body.mjs v0.3.0 --out release-body.md      # 只生成
 *   node scripts/release-body.mjs v0.3.0 --update                   # 生成并推送到线上 release
 *   node scripts/release-body.mjs v0.3.0 --auto-notes auto-notes.md # 跳过 gh 调用
 *   node scripts/release-body.mjs v0.3.0 --repo Tinadec/TinadecOffice
 *   node scripts/release-body.mjs nightly-723c7d5 --nightly --commit <sha> --display-version nightly-723c7d5 --out release-body.md
 */
import { execFileSync } from 'node:child_process'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')

/** 把模板里的 `__GH_REPO__` / `__VERSION__` 换成本次发布的值（`v0.3.0` → `0.3.0`，或显式 `displayVersion`）。 */
export function fillTemplate(text, { repo, tag, displayVersion = null }) {
  const version = displayVersion || tag.replace(/^v/, '')
  // replaceAll 而非正则：仓库名里的 `.` `+` 之类不该被当成模式。
  return text.replaceAll('__GH_REPO__', repo).replaceAll('__VERSION__', version)
}

/** 自动 changelog 收进折叠块，并把它的 H2 降一级，免得和正文标题打架。 */
function foldCommitLog(autoNotes) {
  const nested = autoNotes.trim().replace(/^## /gm, '### ')
  return [
    '<details>',
    '<summary><b>Full commit log</b> &nbsp;·&nbsp; every pull request in this release</summary>',
    '',
    '<br>',
    '',
    nested,
    '',
    '</details>',
  ].join('\n')
}

/**
 * 纯函数式组装，不碰网络也不写文件，方便测试。
 * `autoNotes` 为空时只输出头图 + 正文（或 Nightly 说明）+ 折叠的发布信息。
 */
export function composeReleaseBody({
  root,
  tag,
  repo,
  autoNotes,
  nightly = false,
  commit = null,
  displayVersion = null,
}) {
  const read = (relative) => readFileSync(path.join(root, relative), 'utf8')
  const substitute = (text) => fillTemplate(text, { repo, tag, displayVersion }).trimEnd()

  const header = substitute(read('.github/release-header.md'))
  const footer = substitute(read('.github/release-footer.md'))

  const parts = [header]

  if (nightly) {
    const shortSha = commit ? commit.slice(0, 7) : tag.replace(/^nightly-/, '')
    const commitRef = commit ? `[\`${shortSha}\`](https://github.com/${repo}/commit/${commit})` : `\`${shortSha}\``
    parts.push(
      [
        '## Nightly Preview Build',
        '',
        `This is an automated Nightly preview prerelease built from the \`main\` branch at commit ${commitRef}.`,
        'It is intended for early testing and feedback; for stable usage, prefer the latest official release.',
      ].join('\n'),
    )
    if (autoNotes?.trim()) parts.push(foldCommitLog(autoNotes))
    parts.push(footer)

    return { body: `${parts.join('\n\n')}\n`, hasNotes: true, isNightly: true }
  }

  const notesPath = path.join(root, 'docs', 'releases', `${tag}.md`)
  const notes = existsSync(notesPath) ? readFileSync(notesPath, 'utf8').trim() : ''

  if (notes) {
    parts.push(notes)
  } else {
    // 保留成注释而不是可见文案：本版还没写复盘时，页面上不该出现一句"请补充说明"，
    // 但 `gh release view` 和 grep 要能看出缺了什么。
    parts.push(`<!-- release-notes: docs/releases/${tag}.md is missing -->`)
  }
  if (autoNotes?.trim()) parts.push(foldCommitLog(autoNotes))
  parts.push(footer)

  return { body: `${parts.join('\n\n')}\n`, hasNotes: Boolean(notes) }
}

function resolveRepo(explicit) {
  if (explicit) return explicit
  if (process.env.GH_REPO) return process.env.GH_REPO
  return execFileSync('gh', ['repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner'], {
    encoding: 'utf8',
  }).trim()
}

function fetchAutoNotes(repo, tag, { commit, previousTag } = {}) {
  // gh release create 的 --notes-file 与 --generate-notes 互斥，所以先单独取回自动 changelog。
  const apiArgs = ['api', `repos/${repo}/releases/generate-notes`, '-f', `tag_name=${tag}`]
  if (commit) apiArgs.push('-f', `target_commitish=${commit}`)
  if (previousTag) apiArgs.push('-f', `previous_tag_name=${previousTag}`)
  apiArgs.push('--jq', '.body')
  return execFileSync('gh', apiArgs, { encoding: 'utf8', maxBuffer: 32 * 1024 * 1024 })
}

export function parseArgs(argv) {
  const args = {
    tag: null,
    out: null,
    autoNotes: null,
    repo: null,
    update: false,
    nightly: false,
    commit: null,
    displayVersion: null,
    previousTag: null,
  }
  const readValue = (flag, index) => {
    if (index + 1 >= argv.length) throw new Error(`missing value for ${flag}`)
    return argv[index + 1]
  }
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i]
    if (arg === '--out') {
      args.out = readValue(arg, i)
      i += 1
    } else if (arg === '--auto-notes') {
      args.autoNotes = readValue(arg, i)
      i += 1
    } else if (arg === '--repo') {
      args.repo = readValue(arg, i)
      i += 1
    } else if (arg === '--commit') {
      args.commit = readValue(arg, i)
      i += 1
    } else if (arg === '--display-version') {
      args.displayVersion = readValue(arg, i)
      i += 1
    } else if (arg === '--previous-tag') {
      args.previousTag = readValue(arg, i)
      i += 1
    } else if (arg === '--update') {
      args.update = true
    } else if (arg === '--nightly') {
      args.nightly = true
    } else if (!args.tag && !arg.startsWith('--')) {
      args.tag = arg
    } else {
      throw new Error(`unexpected argument: ${arg}`)
    }
  }
  if (!args.tag) {
    throw new Error(
      'usage: release-body.mjs <tag> [--out <file>] [--update] [--auto-notes <file>] [--repo <owner/repo>] [--nightly] [--commit <sha>] [--display-version <label>] [--previous-tag <tag>]',
    )
  }
  return args
}

function main() {
  const args = parseArgs(process.argv.slice(2))
  const repo = resolveRepo(args.repo)
  const autoNotes = args.autoNotes
    ? readFileSync(path.resolve(args.autoNotes), 'utf8')
    : fetchAutoNotes(repo, args.tag, { commit: args.commit, previousTag: args.previousTag })

  const { body, hasNotes } = composeReleaseBody({
    root: repoRoot,
    tag: args.tag,
    repo,
    autoNotes,
    nightly: args.nightly,
    commit: args.commit,
    displayVersion: args.displayVersion,
  })

  if (!hasNotes) {
    const message = `docs/releases/${args.tag}.md is missing — this release has no hand-written notes.`
    // Actions 里以 ::warning:: 升起，页面上能直接看到这条，而不是埋进日志。
    console.warn(process.env.GITHUB_ACTIONS ? `::warning::${message}` : `warning: ${message}`)
  }

  if (args.out) writeFileSync(path.resolve(args.out), body)
  else if (!args.update) process.stdout.write(body)

  if (args.update) {
    const staging = path.join(os.tmpdir(), `tinadec-release-body-${args.tag}.md`)
    writeFileSync(staging, body)
    execFileSync('gh', ['release', 'edit', args.tag, '--repo', repo, '--notes-file', staging], {
      stdio: 'inherit',
    })
    console.log(`updated release ${args.tag} on ${repo}`)
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    main()
  } catch (error) {
    console.error(`release-body: ${error.message}`)
    process.exitCode = 1
  }
}
