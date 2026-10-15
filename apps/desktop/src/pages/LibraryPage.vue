<script setup lang="ts">
import { ref, computed } from 'vue'
import { useI18n } from 'vue-i18n'
import {
  Component, LayoutGrid, MousePointer2, Type, Square, ToggleLeft,
  ListTodo, Search, SlidersHorizontal, Bell, Table2, CreditCard, Sparkles,
} from '@lucide/vue'
import AppHeader from '@/components/AppHeader.vue'
import { UiCard, UiButton, UiBadge, UiInput, UiSwitch } from '@/components/ui'

const { t } = useI18n()

type DemoSection = 'buttons' | 'inputs' | 'cards' | 'feedback' | 'data' | 'layout'
const active = ref<DemoSection>('buttons')

const sections = computed<{ id: DemoSection; label: string; icon: any }[]>(() => [
  { id: 'buttons', label: t('library.sections.buttons'), icon: MousePointer2 },
  { id: 'inputs', label: t('library.sections.inputs'), icon: Type },
  { id: 'cards', label: t('library.sections.cards'), icon: CreditCard },
  { id: 'feedback', label: t('library.sections.feedback'), icon: Bell },
  { id: 'data', label: t('library.sections.data'), icon: Table2 },
  { id: 'layout', label: t('library.sections.layout'), icon: LayoutGrid },
])

const demoInput = ref('')
const demoSwitch = ref(true)
const demoRows = computed(() => [
  { id: 1, name: 'Alpha', status: 'active', owner: 'tinadec' },
  { id: 2, name: 'Beta', status: 'draft', owner: 'system' },
  { id: 3, name: 'Gamma', status: 'active', owner: 'tinadec' },
])
</script>

<template>
  <main class="shell">
    <div class="top-drag-bar" />
    <AppHeader />
    <div class="library">
      <aside class="library-nav">
        <div class="library-nav-head">
          <Component :size="16" />
          <h1>{{ t('library.title') }}</h1>
        </div>
        <p class="library-nav-hint">{{ t('library.hint') }}</p>
        <nav class="library-nav-list">
          <button
            v-for="s in sections"
            :key="s.id"
            class="library-nav-item"
            :class="{ active: active === s.id }"
            @click="active = s.id"
          >
            <component :is="s.icon" :size="14" />
            <span>{{ s.label }}</span>
          </button>
        </nav>
      </aside>

      <section class="library-content">
        <!-- Buttons -->
        <div v-if="active === 'buttons'" class="library-section">
          <h2>{{ t('library.sections.buttons') }}</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.buttons.variants') }}</template>
              <div class="library-row">
                <UiButton>{{ t('library.variants.default') }}</UiButton>
                <UiButton variant="secondary">{{ t('library.variants.secondary') }}</UiButton>
                <UiButton variant="ghost">{{ t('library.variants.ghost') }}</UiButton>
                <UiButton variant="outline">{{ t('library.variants.outline') }}</UiButton>
                <UiButton variant="destructive">{{ t('library.variants.destructive') }}</UiButton>
              </div>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.buttons.sizes') }}</template>
              <div class="library-row">
                <UiButton size="sm">{{ t('library.buttons.small') }}</UiButton>
                <UiButton>{{ t('library.variants.default') }}</UiButton>
                <UiButton size="lg">{{ t('library.buttons.large') }}</UiButton>
                <UiButton size="icon"><Search :size="14" /></UiButton>
              </div>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.buttons.states') }}</template>
              <div class="library-row">
                <UiButton disabled>{{ t('library.buttons.disabled') }}</UiButton>
                <UiButton :loading="true">{{ t('library.buttons.loading') }}</UiButton>
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Inputs -->
        <div v-else-if="active === 'inputs'" class="library-section">
          <h2>{{ t('library.sections.inputs') }}</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.inputs.text') }}</template>
              <div class="library-stack">
                <UiInput v-model="demoInput" :placeholder="t('library.inputs.placeholder')" />
                <p class="library-meta">{{ t('library.inputs.value', { value: demoInput || '—' }) }}</p>
              </div>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.inputs.switch') }}</template>
              <div class="library-row">
                <UiSwitch v-model="demoSwitch" />
                <span class="library-meta">{{ demoSwitch ? t('library.inputs.on') : t('library.inputs.off') }}</span>
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Cards -->
        <div v-else-if="active === 'cards'" class="library-section">
          <h2>{{ t('library.sections.cards') }}</h2>
          <div class="library-grid">
            <UiCard :title="t('library.cards.title')" :description="t('library.cards.description')">
              <i18n-t keypath="library.cards.body" tag="p" scope="global" class="library-meta">
                <template #header><code>header</code></template>
                <template #footer><code>footer</code></template>
              </i18n-t>
              <template #footer><UiButton size="sm">{{ t('library.cards.action') }}</UiButton></template>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.cards.badges') }}</template>
              <div class="library-row">
                <UiBadge>{{ t('library.variants.default') }}</UiBadge>
                <UiBadge variant="secondary">{{ t('library.variants.secondary') }}</UiBadge>
                <UiBadge variant="outline">{{ t('library.variants.outline') }}</UiBadge>
                <UiBadge variant="destructive">{{ t('library.variants.destructive') }}</UiBadge>
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Feedback -->
        <div v-else-if="active === 'feedback'" class="library-section">
          <h2>{{ t('library.sections.feedback') }}</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.feedback.notes') }}</template>
              <i18n-t keypath="library.feedback.notesBody" tag="p" scope="global" class="library-meta">
                <template #call><code>useNotifications()</code></template>
              </i18n-t>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.feedback.icons') }}</template>
              <div class="library-row">
                <Sparkles :size="18" /> <SlidersHorizontal :size="18" /> <ToggleLeft :size="18" /> <Square :size="18" />
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Data display -->
        <div v-else-if="active === 'data'" class="library-section">
          <h2>{{ t('library.sections.data') }}</h2>
          <UiCard>
            <template #header>{{ t('library.data.table') }}</template>
            <div class="library-table">
              <div class="library-table-head"><span>#</span><span>{{ t('library.data.name') }}</span><span>{{ t('library.data.status') }}</span><span>{{ t('library.data.owner') }}</span></div>
              <div v-for="r in demoRows" :key="r.id" class="library-table-row">
                <span>{{ r.id }}</span><span>{{ r.name }}</span><span><UiBadge :variant="r.status === 'active' ? 'default' : 'secondary'">{{ r.status === 'active' ? t('library.data.active') : t('library.data.draft') }}</UiBadge></span><span class="library-meta">{{ r.owner }}</span>
              </div>
            </div>
          </UiCard>
          <UiCard class="library-demo-card" style="margin-top:12px">
            <template #header>{{ t('library.data.list') }}</template>
            <ul class="library-list">
              <li v-for="r in demoRows" :key="r.id" class="library-list-item"><ListTodo :size="14" /> {{ r.name }}</li>
            </ul>
          </UiCard>
        </div>

        <!-- Layout -->
        <div v-else-if="active === 'layout'" class="library-section">
          <h2>{{ t('library.sections.layout') }}</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>TinadecUI</template>
              <i18n-t keypath="library.layout.uie" tag="p" scope="global" class="library-meta">
                <template #canvas><code>UieCanvas</code></template>
                <template #column><code>UieColumn</code></template>
              </i18n-t>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>{{ t('library.layout.shell') }}</template>
              <i18n-t keypath="library.layout.shellBody" tag="p" scope="global" class="library-meta">
                <template #shell><code>.shell</code></template>
                <template #header><code>AppHeader</code></template>
              </i18n-t>
            </UiCard>
          </div>
        </div>
      </section>
    </div>
  </main>
</template>

<style scoped>
.shell { position: relative; height: 100vh; display: flex; flex-direction: column; min-height: 0; overflow: hidden; background: transparent; }
.library { flex: 1; min-height: 0; display: flex; gap: 12px; padding: 12px; overflow: hidden; }
.library-nav { width: 220px; flex-shrink: 0; display: flex; flex-direction: column; gap: 10px; padding: 14px; border-radius: 12px; background: var(--surface-section); border: 1px solid var(--border-muted); overflow-y: auto; }
.library-nav-head { display: flex; align-items: center; gap: 8px; font-weight: 600; }
.library-nav-head h1 { margin: 0; font-size: 14px; }
.library-nav-hint { margin: 0; font-size: 12px; color: var(--text-secondary); }
.library-nav-list { display: flex; flex-direction: column; gap: 4px; margin-top: 4px; }
.library-nav-item { display: flex; align-items: center; gap: 8px; width: 100%; text-align: left; padding: 8px 10px; border-radius: 8px; border: 1px solid transparent; background: transparent; color: var(--text-primary); cursor: pointer; font-size: 13px; }
.library-nav-item:hover { background: var(--surface-hover); border-color: var(--border-muted); }
.library-nav-item.active { background: var(--surface-active); border-color: color-mix(in srgb, var(--accent-primary) 55%, var(--border-muted)); color: var(--text-primary); }
.library-content { flex: 1; min-width: 0; overflow-y: auto; padding: 2px 2px 16px 0; }
.library-section h2 { margin: 0 0 12px; font-size: 16px; font-weight: 600; }
.library-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 12px; }
.library-demo-card :deep(.library-row) { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.library-stack { display: flex; flex-direction: column; gap: 8px; }
.library-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.library-meta { font-size: 12px; color: var(--text-secondary); margin: 0; }
.library-table { display: flex; flex-direction: column; gap: 4px; }
.library-table-head, .library-table-row { display: grid; grid-template-columns: 40px 1fr 100px 90px; gap: 8px; align-items: center; padding: 6px 8px; border-radius: 6px; font-size: 12px; }
.library-table-head { font-weight: 600; color: var(--text-secondary); background: var(--surface-hover); }
.library-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
.library-list-item { display: flex; align-items: center; gap: 8px; padding: 6px 8px; border-radius: 6px; background: var(--surface-hover); font-size: 13px; }
</style>
