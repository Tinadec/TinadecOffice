<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { Activity, AlertTriangle, ArrowRight, Stethoscope } from '@lucide/vue'
import { useUie } from '../../useUie'
import { useResponsiveMode } from '@/composables/useElementSize'
import { homeController } from '@/controllers/HomeController'
import { focusDockCard } from '@/lib/homeDockSync'
import { catalogEntriesForPage } from './featureCatalog'
import GitStatusWidget from './GitStatusWidget.vue'
import SessionRunWidget from './SessionRunWidget.vue'
import WorkspaceQuickWidget from './WorkspaceQuickWidget.vue'

const { t, te } = useI18n()
const uie = useUie()
const c = homeController

function label(key: string, fallback: string, params?: Record<string, unknown>): string {
  return te(key) ? String(t(key, params ?? {})) : fallback
}

const currentContext = computed(() => {
  const project = c.currentProject.value
  const session = c.currentSession.value
  if (!project || !session) {
    return { ready: false, label: t('context.homeContextMissing') }
  }
  return {
    ready: true,
    label: t('context.homeContextReady', { project: project.name, session: session.title }),
  }
})

const pendingApprovalCount = computed(
  () => (c.approvals?.value ?? []).filter((a) => a.status === 'pending').length,
)

const readinessStatus = computed(() => c.readiness?.value?.status ?? 'ready')
const recentEventCount = computed(() => c.recentEvents?.value?.length ?? 0)
const isWorking = computed(() => Boolean(c.working?.value))
const activeRunCount = computed(() => c.activeRuns?.value?.length ?? 0)
const workspaceRootCount = computed(() => {
  const proj = c.currentProject.value as (typeof c.currentProject.value & { workspace_roots?: unknown[]; roots?: unknown[] }) | null
  const list = proj?.workspace_roots ?? proj?.roots
  if (Array.isArray(list) && list.length > 0) return list.length
  return proj ? 1 : 0
})

const rootRef = ref<HTMLElement | null>(null)
const { isCompact } = useResponsiveMode(rootRef)

// Catalog entries grouped into Development Workspace & Orchestration/Governance.
// Git lives in the Tier 2 Live Bento; Doctor & Events live in the compact bottom status pill.
const launcherEntries = computed(() =>
  catalogEntriesForPage('home')
    .filter((entry) => entry.descriptorId !== 'git' && entry.group !== 'system')
    .map((entry) =>
      entry.descriptorId === 'approval'
        ? { ...entry, badge: () => pendingApprovalCount.value }
        : entry,
    ),
)

const devFeatures = computed(() => launcherEntries.value.filter((entry) => entry.group === 'dev'))
const govFeatures = computed(() => launcherEntries.value.filter((entry) => entry.group !== 'dev'))

function openCard(descriptorId: string, state?: Record<string, unknown>) {
  uie.dispatch({
    command: {
      type: 'openCard',
      scope: uie.scope.value,
      descriptorId,
      ...(state ? { state } : {}),
    },
    source: 'user',
    expectedRevision: uie.snapshot.value.revision,
  })
}

function openGitFile(filePath: string) {
  focusDockCard(
    {
      card: 'git',
      filePath,
      gitTab: 'changes',
      gitSubView: 'diff',
    } as Parameters<typeof focusDockCard>[0],
    { uie },
  )
  openCard('git')
}

function openGitTab(tab: 'changes' | 'history' | 'branches') {
  focusDockCard(
    {
      card: 'git',
      gitTab: tab,
    } as Parameters<typeof focusDockCard>[0],
    { uie },
  )
  openCard('git')
}

function openDiagnostics(tab: 'readiness' | 'events') {
  openCard('doctor', { tab })
  if (typeof window !== 'undefined') {
    window.dispatchEvent(new CustomEvent('tinadec:doctor-tab', { detail: { tab } }))
  }
}
</script>

<template>
  <div ref="rootRef" class="panel-home-root">
    <section class="panel-home" :class="{ 'panel-home-compact': isCompact }">
      <!-- Tier 1: Project/Session Context Header + Conditional Pending Approvals Strip -->
      <div class="panel-home-header">
        <h2>{{ t('context.homeTitle') }}</h2>
        <p v-if="!isCompact">{{ t('context.homeSubtitle') }}</p>
        <span class="panel-home-context" :class="{ ready: currentContext.ready }">
          {{ currentContext.label }}
        </span>
      </div>

      <button
        v-if="pendingApprovalCount > 0"
        type="button"
        class="panel-home-attention"
        data-testid="home-attention-approvals"
        @click="openCard('approval')"
      >
        <AlertTriangle :size="14" aria-hidden="true" />
        <span class="panel-home-attention-text">
          {{ label('context.bentoPendingApprovals', `${pendingApprovalCount} pending approvals`, { count: pendingApprovalCount }) }}
        </span>
        <span class="panel-home-attention-cta">
          {{ label('context.bentoReviewNow', 'Review now') }}
          <ArrowRight :size="12" aria-hidden="true" />
        </span>
      </button>

      <!-- Tier 2: Live Bento (3 cards) -->
      <div class="panel-home-bento" data-testid="home-bento-deck">
        <SessionRunWidget
          :session-id="c.currentSession.value?.id ?? null"
          :working="isWorking"
          :active-run-count="activeRunCount"
          :orchestration="c.orchestration?.value ?? null"
          @open-orchestration="openCard('orchestration')"
          @open-agent="openCard('agent')"
        />

        <GitStatusWidget
          :project="c.currentProject.value"
          @open="openCard('git')"
          @open-file="openGitFile"
          @open-tab="openGitTab"
        />

        <WorkspaceQuickWidget
          :project="c.currentProject.value"
          :root-count="workspaceRootCount"
          @open-code="openCard('codeEditor')"
          @open-terminal="openCard('terminal')"
          @open-browser="openCard('browser')"
          @open-workspace="openCard('workspace')"
        />
      </div>

      <!-- Tier 3: Grouped Launcher Rows -->
      <div class="panel-home-groups">
        <div class="panel-home-group" data-testid="home-group-dev">
          <div class="panel-home-group-title">{{ label('context.bentoGroupDev', 'Development Workspace') }}</div>
          <div class="panel-home-grid">
            <button
              v-for="feature in devFeatures"
              :key="feature.descriptorId"
              type="button"
              class="panel-home-card"
              :class="{ 'panel-home-card-compact': isCompact }"
              @click="openCard(feature.descriptorId)"
            >
              <div class="panel-home-card-icon" :style="{ '--card-color': feature.color }">
                <component :is="feature.icon" :size="isCompact ? 16 : 18" />
              </div>
              <div class="panel-home-card-body">
                <span class="panel-home-card-title">{{ t(feature.titleKey) }}</span>
                <span v-if="!isCompact" class="panel-home-card-desc">{{ t(feature.descKey) }}</span>
              </div>
            </button>
          </div>
        </div>

        <div class="panel-home-group" data-testid="home-group-gov">
          <div class="panel-home-group-title">{{ label('context.bentoGroupGov', 'Orchestration & Governance') }}</div>
          <div class="panel-home-grid">
            <button
              v-for="feature in govFeatures"
              :key="feature.descriptorId"
              type="button"
              class="panel-home-card"
              :class="{ 'panel-home-card-compact': isCompact }"
              @click="openCard(feature.descriptorId)"
            >
              <div class="panel-home-card-icon" :style="{ '--card-color': feature.color }">
                <component :is="feature.icon" :size="isCompact ? 16 : 18" />
              </div>
              <div class="panel-home-card-body">
                <span class="panel-home-card-title">{{ t(feature.titleKey) }}</span>
                <span v-if="!isCompact" class="panel-home-card-desc">{{ t(feature.descKey) }}</span>
              </div>
              <span v-if="feature.badge && feature.badge() > 0" class="panel-home-card-badge">
                {{ feature.badge() }}
              </span>
            </button>
          </div>
        </div>
      </div>

      <!-- Compact Bottom Status Pill opening unified Diagnostics & Events -->
      <div class="panel-home-status-bar" data-testid="home-status-pill">
        <button
          type="button"
          class="panel-home-status-pill"
          :class="{ 'is-warn': readinessStatus !== 'ready' }"
          @click="openDiagnostics('readiness')"
        >
          <Stethoscope :size="13" aria-hidden="true" />
          <span>
            {{
              readinessStatus === 'ready'
                ? label('context.bentoSystemReady', 'System ready')
                : label('context.bentoSystemAttention', 'System attention')
            }}
          </span>
        </button>
        <span class="panel-home-status-sep" aria-hidden="true">·</span>
        <button
          type="button"
          class="panel-home-status-pill"
          data-testid="home-status-events-btn"
          @click="openDiagnostics('events')"
        >
          <Activity :size="13" aria-hidden="true" />
          <span>{{ label('context.bentoEventsCount', `Events ${recentEventCount}`, { count: recentEventCount }) }}</span>
        </button>
      </div>
    </section>
  </div>
</template>

<style scoped>
.panel-home-root {
  width: 100%;
  height: 100%;
}

.panel-home {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 16px 13px 12px;
  height: 100%;
  overflow-y: auto;
}
.panel-home-header { display: flex; flex-direction: column; gap: 3px; }
.panel-home-header h2 { margin: 0; font-size: 15px; font-weight: 700; color: var(--text-primary); }
.panel-home-header p { margin: 0; font-size: 12px; color: var(--text-muted); line-height: 1.4; }
.panel-home-context { font-size: 11px; color: var(--text-muted); line-height: 1.4; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.panel-home-context.ready { color: var(--text-secondary); }

.panel-home-attention {
  display: flex; align-items: center; gap: 8px; padding: 8px 10px;
  border-radius: 10px; border: 1px solid color-mix(in srgb, var(--accent-warning) 45%, var(--border-muted));
  background: color-mix(in srgb, var(--accent-warning) 12%, var(--surface-raised));
  color: var(--accent-warning); font-size: 12px; cursor: pointer; text-align: left;
}
.panel-home-attention:hover {
  background: color-mix(in srgb, var(--accent-warning) 18%, var(--surface-raised));
}
.panel-home-attention-text { flex: 1; font-weight: 600; color: var(--text-primary); }
.panel-home-attention-cta { display: inline-flex; align-items: center; gap: 4px; font-size: 11px; font-weight: 600; white-space: nowrap; }

.panel-home-bento {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.panel-home-groups {
  display: flex;
  flex-direction: column;
  gap: 10px;
}
.panel-home-group {
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.panel-home-group-title {
  font-size: 11px;
  font-weight: 600;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.03em;
  padding: 0 2px;
}

.panel-home-grid { display: grid; grid-template-columns: 1fr 1fr; gap: 7px; }

.panel-home-compact {
  padding: 12px 10px;
  gap: 10px;
}
.panel-home-compact .panel-home-grid {
  grid-template-columns: 1fr;
  gap: 5px;
}
.panel-home-card-compact {
  flex-direction: row;
  align-items: center;
  padding: 7px 9px;
  gap: 9px;
}
.panel-home-card-compact .panel-home-card-icon {
  width: 26px;
  height: 26px;
  flex-shrink: 0;
}
.panel-home-card-compact .panel-home-card-body {
  flex-direction: column;
  gap: 0;
}

.panel-home-card {
  position: relative;
  display: flex;
  flex-direction: row;
  align-items: center;
  gap: 9px;
  padding: 9px 10px;
  background: var(--surface-raised);
  border: 1px solid transparent;
  border-radius: 10px;
  box-shadow: var(--shadow-subtle);
  cursor: pointer;
  transition: all 0.16s ease;
  text-align: left;
  overflow: hidden;
}
.panel-home-card:hover {
  background: var(--surface-hover);
  border-color: var(--card-color, var(--accent-primary));
  box-shadow: var(--shadow-panel);
  transform: translateY(-1px);
}
.panel-home-card:active { transform: translateY(0); }
.panel-home-card-icon {
  display: grid; place-items: center; width: 30px; height: 30px; border-radius: 8px; flex-shrink: 0;
  background: color-mix(in srgb, var(--card-color, var(--accent-primary)) 14%, transparent);
  color: var(--card-color, var(--accent-primary));
}
.panel-home-card-body { display: flex; flex-direction: column; gap: 1px; min-width: 0; flex: 1; }
.panel-home-card-title { font-size: 12px; font-weight: 600; color: var(--text-primary); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.panel-home-card-desc { font-size: 10.5px; color: var(--text-muted); line-height: 1.3; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.panel-home-card-badge {
  display: inline-flex; align-items: center; justify-content: center;
  min-width: 18px; height: 18px; padding: 0 5px; font-size: 10px; font-weight: 700;
  color: #fff; background: var(--accent-primary); border-radius: 999px; flex-shrink: 0;
}

.panel-home-status-bar {
  margin-top: auto;
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 6px 10px;
  border-radius: 999px;
  border: 1px solid var(--border-muted);
  background: var(--surface-raised);
  align-self: center;
}
.panel-home-status-pill {
  display: inline-flex; align-items: center; gap: 5px;
  border: 0; background: transparent; color: var(--text-secondary);
  font-size: 11px; cursor: pointer; padding: 2px 4px; border-radius: 6px;
}
.panel-home-status-pill:hover { color: var(--text-primary); background: var(--surface-hover); }
.panel-home-status-pill.is-warn { color: var(--accent-warning); }
.panel-home-status-sep { color: var(--text-muted); font-size: 11px; }
</style>
