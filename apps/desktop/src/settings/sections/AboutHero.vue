<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Check, CircleDashed, GitBranch, GitCommitHorizontal, ListChecks, LoaderCircle, MessageSquareText, X } from '@lucide/vue'
import BrandLogo from '@/components/BrandLogo.vue'
import TinadecCalligraphy from '@/components/TinadecCalligraphy.vue'

/**
 * The About page's banner, after the "Agent Teamwork" poster: the brand and tagline over a
 * gradient, a soft beam of light, film grain and drifting points of light, with a few glass
 * cards showing what agents do here. The cards are illustration (hidden from assistive tech).
 *
 * Every colour comes from the theme: the gradient and beam mix the accent tokens into the
 * background tokens, and the particles are drawn in the resolved accent colours. So the banner
 * follows light/dark, the accent preset or custom colour, and a wallpaper-derived palette.
 */
defineProps<{ version: string; development: boolean }>()
const { t } = useI18n()
/** The brand's local name ("缇纳蒂丝"); empty where the locale has none. */
const brandLocal = computed(() => {
  const name = t('aboutPage.hero.brandLocal')
  return name === 'aboutPage.hero.brandLocal' ? '' : name
})

const canvas = ref<HTMLCanvasElement | null>(null)
const accentProbe = ref<HTMLElement | null>(null)
const glowProbe = ref<HTMLElement | null>(null)

interface Particle { x: number; y: number; r: number; vx: number; vy: number; alpha: number; phase: number; glow: boolean }
let particles: Particle[] = []
let colors = { accent: 'rgb(46, 196, 182)', glow: 'rgb(255, 255, 255)' }
let frame = 0
let running = false
let visible = true
let reducedMotion = false
let lastTime = 0
const cleanups: Array<() => void> = []

/** Theme tokens can be expressions (color-mix, var()); a probe element lets the browser resolve them. */
function readColors() {
  if (accentProbe.value) colors.accent = getComputedStyle(accentProbe.value).color || colors.accent
  if (glowProbe.value) colors.glow = getComputedStyle(glowProbe.value).color || colors.glow
}

function seed(width: number, height: number) {
  const count = Math.max(18, Math.min(64, Math.round((width * height) / 9000)))
  particles = Array.from({ length: count }, () => ({
    x: Math.random(), y: Math.random(),
    r: 0.6 + Math.random() * 1.8,
    vx: (Math.random() - 0.5) * 0.004, vy: -(0.003 + Math.random() * 0.008),
    alpha: 0.25 + Math.random() * 0.55, phase: Math.random() * Math.PI * 2,
    glow: Math.random() < 0.35,
  }))
}

function draw(time: number) {
  const element = canvas.value
  const context = element?.getContext('2d')
  if (!element || !context) return
  const { width, height } = element
  const scale = window.devicePixelRatio || 1
  context.clearRect(0, 0, width, height)
  for (const particle of particles) {
    const twinkle = 0.65 + 0.35 * Math.sin(time / 900 + particle.phase)
    context.globalAlpha = particle.alpha * twinkle
    context.fillStyle = particle.glow ? colors.glow : colors.accent
    context.shadowColor = context.fillStyle
    context.shadowBlur = particle.r * 6 * scale
    context.beginPath()
    context.arc(particle.x * width, particle.y * height, particle.r * scale, 0, Math.PI * 2)
    context.fill()
  }
  context.globalAlpha = 1
  context.shadowBlur = 0
}

function step(time: number) {
  if (!running) return
  const seconds = Math.min(0.05, (time - (lastTime || time)) / 1000)
  lastTime = time
  for (const particle of particles) {
    particle.x += particle.vx * seconds * 6
    particle.y += particle.vy * seconds * 6
    if (particle.y < -0.05) { particle.y = 1.05; particle.x = Math.random() }
    if (particle.x < -0.05) particle.x = 1.05
    if (particle.x > 1.05) particle.x = -0.05
  }
  draw(time)
  frame = requestAnimationFrame(step)
}

function resume() {
  const shouldRun = visible && !reducedMotion && !document.hidden
  if (shouldRun && !running) { running = true; lastTime = 0; frame = requestAnimationFrame(step) }
  else if (!shouldRun && running) { running = false; cancelAnimationFrame(frame) }
  if (!shouldRun) draw(performance.now())
}

function resize() {
  const element = canvas.value
  if (!element) return
  const rect = element.getBoundingClientRect()
  const scale = window.devicePixelRatio || 1
  element.width = Math.max(1, Math.round(rect.width * scale))
  element.height = Math.max(1, Math.round(rect.height * scale))
  if (!particles.length) seed(rect.width, rect.height)
  draw(performance.now())
}

onMounted(() => {
  const element = canvas.value
  // No 2D context (a test DOM, a locked-down renderer): the banner simply has no particles.
  if (!element || !element.getContext('2d')) return
  readColors()
  resize()
  const motion = window.matchMedia?.('(prefers-reduced-motion: reduce)')
  reducedMotion = Boolean(motion?.matches)
  const onMotion = (event: MediaQueryListEvent) => { reducedMotion = event.matches; resume() }
  motion?.addEventListener?.('change', onMotion)
  cleanups.push(() => motion?.removeEventListener?.('change', onMotion))
  // The palette lands on <html> (style, data-theme, class); re-read and repaint when it changes.
  const themeObserver = new MutationObserver(() => { readColors(); if (!running) draw(performance.now()) })
  themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['style', 'class', 'data-theme'] })
  cleanups.push(() => themeObserver.disconnect())
  if (typeof ResizeObserver !== 'undefined') {
    const sizeObserver = new ResizeObserver(resize)
    sizeObserver.observe(element)
    cleanups.push(() => sizeObserver.disconnect())
  }
  if (typeof IntersectionObserver !== 'undefined') {
    const viewObserver = new IntersectionObserver(([entry]) => { visible = Boolean(entry?.isIntersecting); resume() })
    viewObserver.observe(element)
    cleanups.push(() => viewObserver.disconnect())
  }
  const onVisibility = () => resume()
  document.addEventListener('visibilitychange', onVisibility)
  cleanups.push(() => document.removeEventListener('visibilitychange', onVisibility))
  resume()
})

onBeforeUnmount(() => {
  running = false
  cancelAnimationFrame(frame)
  for (const cleanup of cleanups.splice(0)) cleanup()
})
</script>

<template>
  <header class="about-hero" data-testid="about-hero">
    <div class="about-hero-glow" aria-hidden="true" />
    <div class="about-hero-beam" aria-hidden="true" />
    <canvas ref="canvas" class="about-hero-particles" aria-hidden="true" />
    <div class="about-hero-grain" aria-hidden="true" />
    <span ref="accentProbe" class="about-hero-probe about-hero-probe--accent" aria-hidden="true" />
    <span ref="glowProbe" class="about-hero-probe about-hero-probe--glow" aria-hidden="true" />

    <div class="about-hero-copy">
      <div class="about-hero-brand">
        <BrandLogo :size="22" />
        <TinadecCalligraphy :size="22" />
        <span v-if="brandLocal" class="about-hero-brand-local">{{ brandLocal }}</span>
      </div>
      <p class="about-hero-tagline">{{ t('aboutPage.hero.tagline') }}</p>
      <p class="about-hero-title">Agent<br>Teamwork</p>
      <p class="about-hero-version" data-testid="about-hero-version">
        TinadecOffice {{ version }}<span v-if="development"> · {{ t('aboutPage.hero.development') }}</span>
      </p>
    </div>

    <div class="about-hero-cards" aria-hidden="true">
      <section class="about-hero-card about-hero-card--tasks">
        <header><ListChecks :size="13" />{{ t('aboutPage.hero.tasks') }}</header>
        <ul>
          <li><Check :size="12" class="is-done" /><span>{{ t('aboutPage.hero.taskSetup') }}<small>{{ t('aboutPage.hero.files', { n: 5 }) }} <b class="add">+1,127</b> <b class="del">-8</b></small></span></li>
          <li><LoaderCircle :size="12" class="is-running" /><span>{{ t('aboutPage.hero.taskDesign') }}<small>{{ t('aboutPage.hero.files', { n: 14 }) }} <b class="add">+12,327</b> <b class="del">-52</b></small></span></li>
          <li><LoaderCircle :size="12" class="is-running" /><span>{{ t('aboutPage.hero.taskFrontend') }}<small>{{ t('aboutPage.hero.files', { n: 22 }) }} <b class="add">+39,233</b> <b class="del">-13</b></small></span></li>
          <li><CircleDashed :size="12" /><span>{{ t('aboutPage.hero.taskBackend') }}<small>{{ t('aboutPage.hero.notStarted') }}</small></span></li>
        </ul>
        <footer>{{ t('aboutPage.hero.taskSummary') }}</footer>
      </section>
      <section class="about-hero-card about-hero-card--vcs">
        <header><GitBranch :size="13" />{{ t('aboutPage.hero.versionControl') }}</header>
        <p><GitCommitHorizontal :size="12" />{{ t('aboutPage.hero.commitAgo') }}</p>
        <code>2e13f57 {{ t('aboutPage.hero.commitDesign') }}</code>
        <code>343dfd8 {{ t('aboutPage.hero.commitStyle') }}</code>
        <p><GitBranch :size="12" />{{ t('aboutPage.hero.branch') }} · <code>feat/ui</code></p>
      </section>
      <section class="about-hero-card about-hero-card--review">
        <header><MessageSquareText :size="13" />{{ t('aboutPage.hero.reviews') }}</header>
        <div class="about-hero-review">
          <span>{{ t('aboutPage.hero.reviewTerminal') }}<small class="is-done"><Check :size="10" />{{ t('aboutPage.hero.reviewPassed') }}</small></span>
        </div>
        <div class="about-hero-review">
          <span>{{ t('aboutPage.hero.reviewSsh') }}<small>{{ t('aboutPage.hero.reviewWaiting') }}</small></span>
          <i class="about-hero-decision about-hero-decision--reject"><X :size="10" /></i>
          <i class="about-hero-decision about-hero-decision--approve"><Check :size="10" /></i>
        </div>
      </section>
    </div>
  </header>
</template>

<style scoped>
.about-hero {
  position: relative;
  isolation: isolate;
  overflow: hidden;
  container-type: inline-size;
  min-height: 320px;
  margin: 4px 0 8px;
  padding: 26px 30px;
  border-radius: 20px;
  color: var(--text-primary);
  background:
    radial-gradient(120% 140% at 0% 0%, color-mix(in srgb, var(--accent-primary) 30%, transparent) 0%, transparent 62%),
    radial-gradient(90% 120% at 100% 100%, color-mix(in srgb, var(--accent-brand, var(--accent-primary)) 24%, transparent) 0%, transparent 70%),
    linear-gradient(135deg, color-mix(in srgb, var(--accent-primary) 16%, var(--bg-secondary)) 0%, var(--bg-primary) 100%);
  box-shadow: var(--shadow-card-subtle);
}
/* Decorations sit under the copy and cards and never take the pointer. */
.about-hero-glow, .about-hero-beam, .about-hero-particles, .about-hero-grain { position: absolute; pointer-events: none; }
.about-hero-glow {
  right: -8%; top: 18%; width: 62%; height: 90%; z-index: -1;
  border-radius: 50%;
  background: radial-gradient(closest-side, color-mix(in srgb, var(--accent-info, var(--accent-primary)) 32%, transparent), transparent);
  filter: blur(24px);
}
.about-hero-beam {
  inset: -40% -20%; z-index: -1;
  background: linear-gradient(112deg, transparent 40%, color-mix(in srgb, var(--accent-soft, var(--accent-primary)) 55%, transparent) 50%, transparent 60%);
  filter: blur(30px);
  opacity: 0.55;
}
.about-hero-particles { inset: 0; width: 100%; height: 100%; z-index: 0; }
.about-hero-grain {
  inset: 0; z-index: 0;
  background-image: url("data:image/svg+xml;utf8,<svg xmlns='http://www.w3.org/2000/svg' width='160' height='160'><filter id='n'><feTurbulence type='fractalNoise' baseFrequency='.85' numOctaves='3' stitchTiles='stitch'/></filter><rect width='100%' height='100%' filter='url(%23n)'/></svg>");
  opacity: 0.16;
  mix-blend-mode: overlay;
}
.about-hero-probe { position: absolute; width: 0; height: 0; overflow: hidden; }
.about-hero-probe--accent { color: var(--accent-primary); }
.about-hero-probe--glow { color: color-mix(in srgb, var(--accent-soft, var(--accent-primary)) 40%, var(--text-primary)); }

.about-hero-copy { position: relative; z-index: 1; display: flex; flex-direction: column; max-width: 46%; min-height: 268px; }
.about-hero-brand { display: flex; align-items: center; gap: 8px; color: var(--text-primary); }
.about-hero-brand-local { font-size: 17px; font-weight: 600; letter-spacing: 0.04em; }
.about-hero-tagline { margin: auto 0 4px; font-size: 22px; font-weight: 500; line-height: 1.25; color: var(--text-secondary); }
.about-hero-title { margin: 0; font-size: clamp(38px, 8.4cqi, 66px); font-weight: 850; line-height: 0.92; letter-spacing: -0.035em; }
.about-hero-version { margin: 14px 0 0; font-size: 12px; color: var(--text-secondary); font-variant-numeric: tabular-nums; }

.about-hero-cards { position: absolute; z-index: 1; inset: 0 0 0 auto; width: 52%; }
.about-hero-card {
  position: absolute;
  padding: 10px 12px;
  border-radius: 14px;
  font-size: 11px;
  color: var(--text-primary);
  background: color-mix(in srgb, var(--bg-secondary) 58%, transparent);
  backdrop-filter: blur(16px) saturate(1.2);
  -webkit-backdrop-filter: blur(16px) saturate(1.2);
  box-shadow: var(--shadow-card-subtle);
  animation: about-hero-float 7s ease-in-out infinite alternate;
}
.about-hero-card header { display: flex; align-items: center; gap: 6px; margin-bottom: 8px; font-size: 12px; font-weight: 650; }
.about-hero-card ul { display: grid; gap: 6px; margin: 0; padding: 0; list-style: none; }
.about-hero-card li { display: flex; gap: 6px; align-items: flex-start; }
.about-hero-card li span, .about-hero-review span { display: grid; gap: 1px; min-width: 0; }
.about-hero-card small { color: var(--text-muted); font-size: 10px; }
.about-hero-card footer { margin-top: 8px; color: var(--text-muted); font-size: 10px; }
.about-hero-card code { display: block; margin: 2px 0 0 18px; color: var(--text-muted); font-size: 10px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.about-hero-card p { display: flex; align-items: center; gap: 6px; margin: 6px 0 0; }
.about-hero-card p:first-of-type { margin-top: 0; }
.about-hero-card p code { display: inline; margin: 0; }
.about-hero-card .add { color: var(--accent-success); font-weight: 500; }
.about-hero-card .del { color: var(--accent-danger); font-weight: 500; }
.about-hero-card .is-done { color: var(--accent-success); }
.about-hero-card .is-running { color: var(--accent-primary); }
/* Tasks take the left column, the other two stack on the right: edges may meet as on the poster,
   text never sits under another card. */
.about-hero-card--tasks { top: 26px; left: 0; width: min(45%, 176px); animation-delay: -2s; }
.about-hero-card--vcs { top: 22px; right: 14px; width: min(46%, 182px); animation-delay: -4s; }
.about-hero-card--review { bottom: 18px; right: 14px; width: min(46%, 182px); }
.about-hero-review { display: flex; align-items: center; gap: 6px; }
.about-hero-review + .about-hero-review { margin-top: 7px; }
.about-hero-review span { flex: 1; }
.about-hero-review small { display: inline-flex; align-items: center; gap: 3px; }
.about-hero-review small.is-done { color: var(--accent-success); }
.about-hero-decision { display: inline-grid; place-items: center; width: 16px; height: 16px; border-radius: 4px; color: #fff; font-style: normal; }
.about-hero-decision--reject { background: var(--accent-danger); }
.about-hero-decision--approve { background: var(--accent-success); }

@keyframes about-hero-float { from { transform: translateY(0); } to { transform: translateY(-6px); } }
@media (prefers-reduced-motion: reduce) { .about-hero-card { animation: none; } }
/* A narrow column keeps the brand and title; the illustration needs the room it was drawn for. */
@container (max-width: 640px) {
  .about-hero-cards { display: none; }
  .about-hero-copy { max-width: none; }
}
</style>
