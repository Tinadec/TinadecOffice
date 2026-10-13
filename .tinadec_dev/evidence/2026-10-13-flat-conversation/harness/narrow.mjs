import { chromium } from 'playwright'
const browser = await chromium.launch()
for (const width of [1169, 520, 380]) {
  const page = await browser.newPage({ viewport: { width: 1169, height: 1100 } })
  await page.addInitScript(() => { window.tinadec = { getAppConfig: async () => ({ debug_studio_enabled: true }), onDebugStudioEnabledChanged() {} } })
  await page.goto('http://127.0.0.1:5173/#/debug-studio', { waitUntil: 'networkidle' })
  await page.addStyleTag({ content: '.island-host { display: none !important; }' })
  await page.getByText('预览', { exact: true }).first().click()
  await page.waitForTimeout(400)
  await page.getByText('对话消息流', { exact: true }).first().click()
  await page.waitForSelector('[data-testid="conversation-view"]')
  if (width < 1169) { await page.setViewportSize({ width, height: 1100 }); await page.waitForTimeout(500) }
  await page.locator('.conversation-preview-scenario', { hasText: 'rich-answer' }).click()
  await page.waitForTimeout(2500)
  const facts = await page.evaluate(() => [...document.querySelectorAll('.markdown-diagram-svg')].map((box) => {
    const svg = box.querySelector('svg'); const label = box.querySelector('foreignObject')
    const scale = svg.getBoundingClientRect().width / parseFloat(svg.style.maxWidth)
    return { box: box.clientWidth, svg: Math.round(svg.getBoundingClientRect().width), natural: parseFloat(svg.style.maxWidth), scale: scale.toFixed(2), labelPx: (12 * scale).toFixed(1), scrolls: box.scrollWidth > box.clientWidth, left: Math.round(svg.getBoundingClientRect().left - box.getBoundingClientRect().left) }
  }))
  console.log(width, JSON.stringify(facts))
  const second = page.locator('.markdown-diagram-svg').nth(1)
  await second.scrollIntoViewIfNeeded()
  await page.waitForTimeout(300)
  await page.locator('.conversation-island').first().screenshot({ path: `/root/.tools/pw/out/narrow-w${width}.png` })
  await page.close()
}
await browser.close()
