import { chromium } from 'playwright'
const browser = await chromium.launch()
const page = await browser.newPage({ viewport: { width: 1169, height: 900 } })
await page.addInitScript(() => { window.tinadec = { getAppConfig: async () => ({ debug_studio_enabled: true }), onDebugStudioEnabledChanged() {} } })
await page.goto('http://127.0.0.1:5173/#/debug-studio', { waitUntil: 'networkidle' })
await page.addStyleTag({ content: '.island-host { display: none !important; }' })
await page.getByText('预览', { exact: true }).first().click()
await page.waitForTimeout(400)
await page.getByText('对话消息流', { exact: true }).first().click()
await page.locator('.conversation-preview-scenario', { hasText: 'parallel-sub-agents' }).click()
await page.waitForTimeout(400)
// Is the shimmering text covered by its gradient throughout the cycle? Sample 8 phases.
const coverage = await page.evaluate(async () => {
  const el = document.querySelector('.conversation-island .conversation-sub-agent-name.shimmer')
  if (!el) return 'missing'
  const out = []
  for (const anim of el.getAnimations()) {
    for (let i = 0; i < 8; i += 1) {
      anim.currentTime = (anim.effect.getComputedTiming().duration / 8) * i
      const pos = getComputedStyle(el).backgroundPositionX
      out.push(pos)
    }
  }
  return out
})
console.log('positions over the cycle', JSON.stringify(coverage))
for (const [i, t] of [[0, 0], [1, 700], [2, 1400]]) {
  await page.evaluate((t) => { for (const a of document.getAnimations()) if (a.animationName === 'shimmer-sweep') { a.pause(); a.currentTime = t } }, t)
  await page.locator('.conversation-island').first().screenshot({ path: `/root/.tools/pw/out/shimmer-phase-${i}.png` })
}
await browser.close()
