// APP-HOME-108 follow-up: date line, compact interim answer, retry, skeleton (Vite renderer only).
import { chromium } from 'playwright'
const OUT = '/root/.tools/pw/out'
const browser = await chromium.launch()
for (const width of [1169, 380, 300]) {
  const page = await browser.newPage({ viewport: { width: 1169, height: 900 } })
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  await page.addInitScript(() => { window.tinadec = { getAppConfig: async () => ({ debug_studio_enabled: true }), onDebugStudioEnabledChanged() {} } })
  await page.goto('http://127.0.0.1:5173/#/debug-studio', { waitUntil: 'networkidle' })
  await page.addStyleTag({ content: '.island-host { display: none !important; }' })
  await page.getByText('预览', { exact: true }).first().click()
  await page.waitForTimeout(400)
  await page.getByText('对话消息流', { exact: true }).first().click()
  await page.waitForSelector('[data-testid="conversation-view"]')
  if (width < 1169) { await page.setViewportSize({ width, height: 900 }); await page.waitForTimeout(500) }
  await page.locator('.conversation-preview-scenario', { hasText: 'next-day-stopped' }).click()
  await page.waitForTimeout(600)
  const island = page.locator('.conversation-island').first()
  const facts = await page.evaluate(() => ({
    separators: [...document.querySelectorAll('[data-testid="day-separator"]')].map((el) => el.textContent?.trim()),
    compact: [...document.querySelectorAll('.conversation-answer.is-compact')].map((el) => {
      const body = el.querySelector('.message-content')
      return { lines: Math.round(body.getBoundingClientRect().height / parseFloat(getComputedStyle(body).lineHeight || '18')), clamp: getComputedStyle(body).webkitLineClamp }
    }),
    retry: document.querySelectorAll('[data-testid="turn-retry"]').length,
    overflowX: document.querySelector('[data-testid="conversation-view"] [data-slot="message-scroller-viewport"]')?.scrollWidth
      > document.querySelector('[data-testid="conversation-view"] [data-slot="message-scroller-viewport"]')?.clientWidth,
  }))
  console.log(width, JSON.stringify(facts))
  await island.screenshot({ path: `${OUT}/w${width}-next-day-stopped.png` })
  await page.getByRole('switch', { name: '加载中' }).click()
  await page.waitForTimeout(350)
  await island.screenshot({ path: `${OUT}/w${width}-loading-skeleton.png` })
  console.log(width, 'skeleton', await page.locator('[data-testid="conversation-skeleton"]').count(), 'errors', errors)
  await page.close()
}
await browser.close()
