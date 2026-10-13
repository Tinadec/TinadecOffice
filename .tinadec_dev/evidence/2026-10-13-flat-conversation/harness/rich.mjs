import { chromium } from 'playwright'
const OUT = '/root/.tools/pw/out'
const browser = await chromium.launch()
for (const [width, theme] of [[1169, 'dark'], [380, 'dark'], [1169, 'light']]) {
  const page = await browser.newPage({ viewport: { width: 1169, height: 1100 } })
  const errors = []
  page.on('pageerror', (e) => errors.push(e.message))
  page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()) })
  await page.addInitScript((theme) => { localStorage.setItem('tinadec-theme', theme); window.tinadec = { getAppConfig: async () => ({ debug_studio_enabled: true }), onDebugStudioEnabledChanged() {} } }, theme)
  await page.goto('http://127.0.0.1:5173/#/debug-studio', { waitUntil: 'networkidle' })
  await page.addStyleTag({ content: '.island-host { display: none !important; }' })
  await page.getByText('预览', { exact: true }).first().click()
  await page.waitForTimeout(400)
  await page.getByText('对话消息流', { exact: true }).first().click()
  await page.waitForSelector('[data-testid="conversation-view"]')
  if (width < 1169) { await page.setViewportSize({ width, height: 1100 }); await page.waitForTimeout(500) }
  await page.locator('.conversation-preview-scenario', { hasText: 'rich-answer' }).click()
  await page.waitForTimeout(2500)
  const facts = await page.evaluate(() => {
    const answers = [...document.querySelectorAll('[data-testid="assistant-answer"]')]
    return answers.map((a) => ({
      compact: a.classList.contains('is-compact'),
      height: Math.round(a.getBoundingClientRect().height),
      svgs: a.querySelectorAll('.markdown-diagram-svg svg').length,
      fallback: a.querySelectorAll('.markdown-diagram-fallback').length,
      imgs: [...a.querySelectorAll('img')].map((i) => ({ w: i.naturalWidth, shown: Math.round(i.getBoundingClientRect().width) })),
      tables: a.querySelectorAll('table').length,
      overflow: a.scrollWidth > a.clientWidth + 1,
      clipped: [...a.querySelectorAll('foreignObject')].filter((fo) => { const c = fo.firstElementChild; return c && (c.scrollWidth > fo.getBoundingClientRect().width + 1 || c.scrollHeight > fo.getBoundingClientRect().height + 1) }).map((fo) => { const c = fo.firstElementChild; const p = c.querySelector('p'); const cs = p ? getComputedStyle(p) : getComputedStyle(c); return { text: fo.textContent.trim(), fo: Math.round(fo.getBoundingClientRect().width), need: c.scrollWidth, fs: cs.fontSize, ff: cs.fontFamily.slice(0, 30), lh: cs.lineHeight, margin: cs.margin } }),
      ids: [...a.querySelectorAll('.markdown-diagram-svg svg')].map((s) => s.id),
    }))
  })
  console.log(width, theme, JSON.stringify(facts), 'errors', errors.slice(0, 3))
  await page.locator('[data-slot="message-scroller-viewport"]').first().evaluate((el) => { el.scrollTop = 0 })
  await page.waitForTimeout(300)
  await page.locator('.conversation-island').first().screenshot({ path: `${OUT}/rich-w${width}-${theme}.png` })
  await page.locator('[data-slot="message-scroller-viewport"]').first().evaluate((el) => { el.scrollTop = el.scrollHeight })
  await page.waitForTimeout(400)
  await page.locator('.conversation-island').first().screenshot({ path: `${OUT}/rich-w${width}-${theme}-end.png` })
  await page.close()
}
await browser.close()
