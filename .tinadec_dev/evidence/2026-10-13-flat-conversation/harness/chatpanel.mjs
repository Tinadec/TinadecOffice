import { chromium } from 'playwright'
const browser = await chromium.launch()
for (const width of [1169, 380]) {
  const page = await browser.newPage({ viewport: { width: 1169, height: 900 } })
  await page.addInitScript(() => { window.tinadec = { getAppConfig: async () => ({ debug_studio_enabled: true }), onDebugStudioEnabledChanged() {} } })
  await page.goto('http://127.0.0.1:5173/#/debug-studio', { waitUntil: 'networkidle' })
  await page.addStyleTag({ content: '.island-host { display: none !important; }' })
  await page.getByText('预览', { exact: true }).first().click()
  await page.waitForTimeout(400)
  await page.getByText('聊天面板', { exact: true }).first().click()
  await page.waitForTimeout(1200)
  if (width < 1169) { await page.setViewportSize({ width, height: 900 }); await page.waitForTimeout(800) }
  const klass = await page.evaluate(() => document.querySelector('.conversation')?.className ?? 'missing')
  console.log(width, 'conversation classes:', klass, 'views:', await page.locator('[data-testid="conversation-view"]').count())
  await page.locator('.chat-preview').first().screenshot({ path: `/root/.tools/pw/out/chatpanel-w${width}.png` })
  await page.close()
}
await browser.close()
