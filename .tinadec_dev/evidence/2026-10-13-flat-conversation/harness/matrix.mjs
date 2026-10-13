import { chromium } from 'playwright'
import { writeFileSync } from 'node:fs'
const OUT = '/root/.tools/pw/out'
const browser = await chromium.launch()
const report = []

async function openPreview({ width, theme = 'dark', panel = { effect: 'blur', opacity: 46, blur: 14 }, reducedMotion = 'no-preference' }) {
  const context = await browser.newContext({ viewport: { width: Math.max(width, 1169), height: 900 }, reducedMotion, deviceScaleFactor: 1 })
  const page = await context.newPage()
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  await page.addInitScript(([theme, panel]) => {
    localStorage.setItem('tinadec-theme', JSON.stringify(theme).replace(/"/g, ''))
    localStorage.setItem('tinadec-panel-style', JSON.stringify(panel))
    window.tinadec = { getAppConfig: async () => ({ debug_studio_enabled: true }), onDebugStudioEnabledChanged() {} }
  }, [theme, panel])
  await page.goto('http://127.0.0.1:5173/#/debug-studio', { waitUntil: 'networkidle' })
  await page.getByText('预览', { exact: true }).first().click()
  await page.waitForTimeout(500)
  await page.getByText('对话消息流', { exact: true }).first().click()
  await page.waitForSelector('[data-testid="conversation-view"]')
  // Harness only: without a Gateway the notification island and the host-mismatch banner
  // float over the preview; they are not part of the feed being captured.
  await page.addStyleTag({ content: '.island-host { display: none !important; }' })
  await page.evaluate(() => {
    for (const el of document.querySelectorAll('body *')) {
      if (el.children.length < 6 && el.textContent?.startsWith('桌面与界面版本不一致') && getComputedStyle(el).position !== 'static') el.style.display = 'none'
    }
    const banner = [...document.querySelectorAll('body *')].find((el) => el.textContent?.trim().startsWith('桌面与界面版本不一致') && el.querySelector('svg'))
    if (banner) (banner.closest('[role="alert"],[role="status"],aside,section,div') ?? banner).style.display = 'none'
  })
  // Narrow widths: Debug Studio's own tab bar hides the Preview tab below ~800px, so the
  // preview is opened wide and the viewport then narrowed, which also exercises reflow.
  if (width < 1169) {
    await page.setViewportSize({ width, height: 900 })
    await page.waitForTimeout(500)
  }
  return { context, page, errors }
}

async function shoot(page, name, label) {
  await page.locator('.conversation-preview-scenario', { hasText: name }).click()
  await page.waitForTimeout(400)
  const island = page.locator('.conversation-island').first()
  const file = `${OUT}/${label}-${name}.png`
  await island.screenshot({ path: file })
  return file
}

const SCENARIOS = ['simple-answer', 'explore-edit-shell', 'pending-approval', 'parallel-sub-agents', 'failed-run', 'interrupted-run', 'compacted-run']
const KEY = ['explore-edit-shell', 'pending-approval', 'parallel-sub-agents']

{ // all scenarios, 1169, dark, blur
  const { context, page, errors } = await openPreview({ width: 1169 })
  for (const name of SCENARIOS) report.push(await shoot(page, name, 'w1169-dark'))
  // expanded state of a settled turn: open the work fold and the first tool row
  await page.locator('.conversation-preview-scenario', { hasText: 'explore-edit-shell' }).click()
  await page.waitForTimeout(300)
  await page.locator('[data-testid="turn-work"] > [data-slot="collapsible-trigger"]').first().click()
  await page.waitForTimeout(400)
  await page.locator('[data-testid="activity-group"] > [data-slot="collapsible-trigger"]').first().click()
  await page.waitForTimeout(400)
  await page.locator('.conversation-island').first().screenshot({ path: `${OUT}/w1169-dark-explore-edit-shell-expanded.png` })
  report.push(`${OUT}/w1169-dark-explore-edit-shell-expanded.png`)
  if (errors.length) report.push('errors(1169): ' + errors.slice(0, 5).join(' | '))
  await context.close()
}
for (const width of [800, 520, 380]) {
  const { context, page } = await openPreview({ width })
  for (const name of KEY) report.push(await shoot(page, name, `w${width}-dark`))
  await context.close()
}
{
  const { context, page } = await openPreview({ width: 1169, theme: 'light' })
  for (const name of KEY) report.push(await shoot(page, name, 'w1169-light'))
  await context.close()
}
{
  const { context, page } = await openPreview({ width: 1169, panel: { effect: 'opaque', opacity: 100, blur: 0 } })
  for (const name of ['pending-approval', 'parallel-sub-agents']) report.push(await shoot(page, name, 'w1169-opaque'))
  await context.close()
}
{ // reduced motion: computed animation of shimmer and fold content, and spinner
  const { context, page } = await openPreview({ width: 1169, reducedMotion: 'reduce' })
  await page.locator('.conversation-preview-scenario', { hasText: 'parallel-sub-agents' }).click()
  await page.waitForTimeout(400)
  const reduce = await page.evaluate(() => {
    const pick = (sel) => { const el = document.querySelector(sel); return el ? getComputedStyle(el).animationName : 'missing' }
    return { shimmer: pick('.conversation-island .shimmer'), spinner: pick('.conversation-island [data-slot="spinner"]'), fold: pick('.conversation-island .conversation-fold-content[data-state="open"]') }
  })
  report.push('reduced-motion animations: ' + JSON.stringify(reduce))
  await context.close()
  const normal = await openPreview({ width: 1169 })
  await normal.page.locator('.conversation-preview-scenario', { hasText: 'parallel-sub-agents' }).click()
  await normal.page.waitForTimeout(400)
  const anim = await normal.page.evaluate(() => {
    const pick = (sel) => { const el = document.querySelector(sel); return el ? getComputedStyle(el).animationName : 'missing' }
    return { shimmer: pick('.conversation-island .shimmer'), spinner: pick('.conversation-island [data-slot="spinner"]') }
  })
  report.push('normal animations: ' + JSON.stringify(anim))
  await normal.context.close()
}
writeFileSync(`${OUT}/report.txt`, report.join('\n'))
console.log(report.join('\n'))
await browser.close()
