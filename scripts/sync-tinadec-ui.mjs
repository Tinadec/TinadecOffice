#!/usr/bin/env node
/* Sync the reusable TinadecUI surface from TinadecOffice into the standalone
   display repo (default: ../TinadecUI). Copies the 30 barrel primitives, the
   ui barrel, lib/utils.ts and logo assets; rewrites '@/lib/utils' imports to
   the relative path used in the standalone layout. tokens.css / fonts.css are
   curated derivatives and intentionally NOT synced. */

import { cpSync, existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const here = path.dirname(fileURLToPath(import.meta.url))
const officeRoot = path.resolve(here, '..')
const target = process.env.TINADEC_UI_TARGET
  ? path.resolve(process.env.TINADEC_UI_TARGET)
  : path.resolve(officeRoot, '../TinadecUI')
const dryRun = process.argv.includes('--dry-run')

const srcUi = path.join(officeRoot, 'apps/desktop/src/components/ui')
const srcLib = path.join(officeRoot, 'apps/desktop/src/lib')
const srcLogo = path.join(officeRoot, 'apps/desktop/public')

const PRIMITIVES = [
  'alert', 'avatar', 'badge', 'breadcrumb', 'button', 'calendar', 'card', 'chart',
  'checkbox', 'collapsible', 'command', 'dropdown-menu', 'input', 'label', 'menubar',
  'pagination', 'popover', 'progress', 'scroll-area', 'select', 'separator', 'sheet',
  'skeleton', 'switch', 'table', 'tabs', 'textarea', 'toggle-group', 'toggle', 'tooltip',
]

const LOGOS = ['tinadec-logo.svg', 'Tinadec-calligraphy.svg', 'favicon.svg', 'tinadec-logo.png']

const copied = []

function copyWithRewrite(from, to) {
  const content = readFileSync(from, 'utf8')
  const rewritten = content.replace(/from '@\/lib\/utils'/g, "from '../../lib/utils'")
  if (!dryRun) writeFileSync(to, rewritten)
  copied.push(path.relative(target, to))
}

function run() {
  if (!existsSync(srcUi)) {
    console.error(`[sync-tinadec-ui] source not found: ${srcUi}`)
    process.exit(1)
  }
  const dstUi = path.join(target, 'src/components/ui')
  const dstLib = path.join(target, 'src/lib')
  const dstLogo = path.join(target, 'src/assets/logo')
  if (!dryRun) {
    mkdirSync(dstUi, { recursive: true })
    mkdirSync(dstLib, { recursive: true })
    mkdirSync(dstLogo, { recursive: true })
  }

  for (const name of PRIMITIVES) {
    copyWithRewrite(path.join(srcUi, `${name}.vue`), path.join(dstUi, `${name}.vue`))
  }
  // barrel + utils are copied verbatim
  if (!dryRun) {
    cpSync(path.join(srcUi, 'index.ts'), path.join(dstUi, 'index.ts'))
    cpSync(path.join(srcLib, 'utils.ts'), path.join(dstLib, 'utils.ts'))
  } else {
    copied.push('src/components/ui/index.ts', 'src/lib/utils.ts')
  }
  for (const logo of LOGOS) {
    if (!existsSync(path.join(srcLogo, logo))) {
      console.warn(`[sync-tinadec-ui] missing logo, skipped: ${logo}`)
      continue
    }
    if (!dryRun) cpSync(path.join(srcLogo, logo), path.join(dstLogo, logo))
    copied.push(`src/assets/logo/${logo}`)
  }

  console.log(`[sync-tinadec-ui] ${dryRun ? 'DRY RUN — would copy' : 'copied'} ${copied.length} files → ${target}`)
  for (const f of copied) console.log(`  ${f}`)
}

run()
