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

const sections: { id: DemoSection; label: string; icon: any }[] = [
  { id: 'buttons', label: 'Buttons', icon: MousePointer2 },
  { id: 'inputs', label: 'Inputs', icon: Type },
  { id: 'cards', label: 'Cards', icon: CreditCard },
  { id: 'feedback', label: 'Feedback', icon: Bell },
  { id: 'data', label: 'Data display', icon: Table2 },
  { id: 'layout', label: 'Layout', icon: LayoutGrid },
]

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
          <h1>Library</h1>
        </div>
        <p class="library-nav-hint">TinadecUI component gallery</p>
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
          <h2>Buttons</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>Variants</template>
              <div class="library-row">
                <UiButton>Default</UiButton>
                <UiButton variant="secondary">Secondary</UiButton>
                <UiButton variant="ghost">Ghost</UiButton>
                <UiButton variant="outline">Outline</UiButton>
                <UiButton variant="destructive">Destructive</UiButton>
              </div>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>Sizes</template>
              <div class="library-row">
                <UiButton size="sm">Small</UiButton>
                <UiButton>Default</UiButton>
                <UiButton size="lg">Large</UiButton>
                <UiButton size="icon"><Search :size="14" /></UiButton>
              </div>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>States</template>
              <div class="library-row">
                <UiButton disabled>Disabled</UiButton>
                <UiButton :loading="true">Loading</UiButton>
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Inputs -->
        <div v-else-if="active === 'inputs'" class="library-section">
          <h2>Inputs</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>Text input</template>
              <div class="library-stack">
                <UiInput v-model="demoInput" placeholder="Type something…" />
                <p class="library-meta">value: {{ demoInput || '—' }}</p>
              </div>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>Switch</template>
              <div class="library-row">
                <UiSwitch v-model="demoSwitch" />
                <span class="library-meta">{{ demoSwitch ? 'on' : 'off' }}</span>
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Cards -->
        <div v-else-if="active === 'cards'" class="library-section">
          <h2>Cards</h2>
          <div class="library-grid">
            <UiCard title="Default card" description="Card with title and description">
              <p class="library-meta">Body slot content. Use <code>header</code>/<code>footer</code> slots for chrome.</p>
              <template #footer><UiButton size="sm">Action</UiButton></template>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>Badges</template>
              <div class="library-row">
                <UiBadge>Default</UiBadge>
                <UiBadge variant="secondary">Secondary</UiBadge>
                <UiBadge variant="outline">Outline</UiBadge>
                <UiBadge variant="destructive">Destructive</UiBadge>
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Feedback -->
        <div v-else-if="active === 'feedback'" class="library-section">
          <h2>Feedback</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>Notes</template>
              <p class="library-meta">Notifications use the island host (App.vue). Trigger via <code>useNotifications()</code> — not rendered here to avoid spill.</p>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>Icons</template>
              <div class="library-row">
                <Sparkles :size="18" /> <SlidersHorizontal :size="18" /> <ToggleLeft :size="18" /> <Square :size="18" />
              </div>
            </UiCard>
          </div>
        </div>

        <!-- Data display -->
        <div v-else-if="active === 'data'" class="library-section">
          <h2>Data display</h2>
          <UiCard>
            <template #header>Table</template>
            <div class="library-table">
              <div class="library-table-head"><span>#</span><span>Name</span><span>Status</span><span>Owner</span></div>
              <div v-for="r in demoRows" :key="r.id" class="library-table-row">
                <span>{{ r.id }}</span><span>{{ r.name }}</span><span><UiBadge :variant="r.status === 'active' ? 'default' : 'secondary'">{{ r.status }}</UiBadge></span><span class="library-meta">{{ r.owner }}</span>
              </div>
            </div>
          </UiCard>
          <UiCard class="library-demo-card" style="margin-top:12px">
            <template #header>List</template>
            <ul class="library-list">
              <li v-for="r in demoRows" :key="r.id" class="library-list-item"><ListTodo :size="14" /> {{ r.name }}</li>
            </ul>
          </UiCard>
        </div>

        <!-- Layout -->
        <div v-else-if="active === 'layout'" class="library-section">
          <h2>Layout</h2>
          <div class="library-grid">
            <UiCard class="library-demo-card">
              <template #header>TinadecUI</template>
              <p class="library-meta">Home/Market use <code>UieCanvas</code> + <code>UieColumn</code>. Library uses a standalone scroll layout.</p>
            </UiCard>
            <UiCard class="library-demo-card">
              <template #header>Shell</template>
              <p class="library-meta">Shell: <code>.shell</code> flex column with <code>AppHeader</code> + scroll content. Panels: TinadecUI three-column engine.</p>
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
