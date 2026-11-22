<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { CalendarClock, CheckSquare, ListChecks, Play, Plus, Trash2 } from '@lucide/vue'
import { UiButton } from '@/components/ui'
import { useNotifications } from '@/composables/useNotifications'
import { useHostAccess } from '@/lib/hostAccess'
import {
  companionApi,
  type CompanionPlan,
  type CompanionSchedule,
  type CompanionSummary,
  type CompanionTodo,
} from '@/lib/companionApi'

const props = defineProps<{
  companion: CompanionSummary | null
  activeConversationId?: string | null
}>()

const { t } = useI18n()
const { notify } = useNotifications()
const { canAccessBackend } = useHostAccess()

const activeSubtab = ref<'schedules' | 'plans' | 'todos'>('schedules')
const loading = ref(false)
const schedules = ref<CompanionSchedule[]>([])
const plans = ref<CompanionPlan[]>([])
const todos = ref<CompanionTodo[]>([])

const scheduleFormOpen = ref(false)
const scheduleTitle = ref('')
const scheduleCron = ref('0 9 * * 1-5')
const schedulePrompt = ref('')

const planFormOpen = ref(false)
const planTitle = ref('')
const planSummary = ref('')

const todoFormOpen = ref(false)
const todoTitle = ref('')
const todoPriority = ref<'low' | 'medium' | 'high'>('medium')

let loadSeq = 0

async function loadAll() {
  const id = props.companion?.id
  if (!id || !canAccessBackend.value) {
    schedules.value = []
    plans.value = []
    todos.value = []
    return
  }
  const seq = ++loadSeq
  loading.value = true
  try {
    const [s, p, td] = await Promise.all([
      companionApi.listSchedules(id),
      companionApi.listPlans(id),
      companionApi.listTodos(id),
    ])
    if (seq !== loadSeq) return
    schedules.value = s ?? []
    plans.value = p ?? []
    todos.value = td ?? []
  } catch (error) {
    if (seq === loadSeq) notify.error(error)
  } finally {
    if (seq === loadSeq) loading.value = false
  }
}

watch(() => [props.companion?.id, canAccessBackend.value] as const, () => {
  void loadAll()
}, { immediate: true })

async function addSchedule() {
  if (!props.companion || !scheduleTitle.value.trim() || !schedulePrompt.value.trim()) return
  try {
    const created = await companionApi.createSchedule(props.companion.id, {
      title: scheduleTitle.value.trim(),
      prompt: schedulePrompt.value.trim(),
      cron_expression: scheduleCron.value.trim() || null,
      target_conversation_id: props.activeConversationId ?? null,
      one_shot: !scheduleCron.value.trim(),
      enabled: true,
    })
    schedules.value = [created, ...schedules.value]
    scheduleTitle.value = ''
    schedulePrompt.value = ''
    scheduleFormOpen.value = false
  } catch (error) {
    notify.error(error)
  }
}

async function toggleSchedule(item: CompanionSchedule) {
  if (!props.companion) return
  try {
    const updated = await companionApi.patchSchedule(props.companion.id, item.id, item.revision, { enabled: !item.enabled })
    schedules.value = schedules.value.map((row) => (row.id === updated.id ? updated : row))
  } catch (error) {
    notify.error(error)
  }
}

async function runScheduleNow(item: CompanionSchedule) {
  if (!props.companion) return
  try {
    const result = await companionApi.triggerSchedule(props.companion.id, item.id)
    if (result?.schedule) {
      schedules.value = schedules.value.map((row) => (row.id === result.schedule.id ? result.schedule : row))
    }
  } catch (error) {
    notify.error(error)
  }
}

async function removeSchedule(item: CompanionSchedule) {
  if (!props.companion) return
  try {
    await companionApi.deleteSchedule(props.companion.id, item.id)
    schedules.value = schedules.value.filter((row) => row.id !== item.id)
  } catch (error) {
    notify.error(error)
  }
}

async function addPlan() {
  if (!props.companion || !planTitle.value.trim()) return
  try {
    const created = await companionApi.createPlan(props.companion.id, {
      title: planTitle.value.trim(),
      summary: planSummary.value.trim() || null,
      status: 'active',
      steps: [],
    })
    plans.value = [created, ...plans.value]
    planTitle.value = ''
    planSummary.value = ''
    planFormOpen.value = false
  } catch (error) {
    notify.error(error)
  }
}

async function cyclePlanStatus(plan: CompanionPlan) {
  if (!props.companion) return
  const next = plan.status === 'active' ? 'completed' : 'active'
  try {
    const updated = await companionApi.patchPlan(props.companion.id, plan.id, plan.revision, { status: next })
    plans.value = plans.value.map((row) => (row.id === updated.id ? updated : row))
  } catch (error) {
    notify.error(error)
  }
}

async function removePlan(plan: CompanionPlan) {
  if (!props.companion) return
  try {
    await companionApi.deletePlan(props.companion.id, plan.id)
    plans.value = plans.value.filter((row) => row.id !== plan.id)
  } catch (error) {
    notify.error(error)
  }
}

async function addTodo() {
  if (!props.companion || !todoTitle.value.trim()) return
  try {
    const created = await companionApi.createTodo(props.companion.id, {
      title: todoTitle.value.trim(),
      priority: todoPriority.value,
      status: 'pending',
    })
    todos.value = [created, ...todos.value]
    todoTitle.value = ''
    todoFormOpen.value = false
  } catch (error) {
    notify.error(error)
  }
}

async function toggleTodo(todo: CompanionTodo) {
  if (!props.companion) return
  const next = todo.status === 'done' ? 'pending' : 'done'
  try {
    const updated = await companionApi.patchTodo(props.companion.id, todo.id, todo.revision, { status: next })
    todos.value = todos.value.map((row) => (row.id === updated.id ? updated : row))
  } catch (error) {
    notify.error(error)
  }
}

async function removeTodo(todo: CompanionTodo) {
  if (!props.companion) return
  try {
    await companionApi.deleteTodo(props.companion.id, todo.id)
    todos.value = todos.value.filter((row) => row.id !== todo.id)
  } catch (error) {
    notify.error(error)
  }
}

const activeScheduleCount = computed(() => schedules.value.filter((s) => s.enabled).length)
const activePlanCount = computed(() => plans.value.filter((p) => p.status === 'active').length)
const pendingTodoCount = computed(() => todos.value.filter((item) => item.status !== 'done' && item.status !== 'cancelled').length)

defineExpose({ reload: loadAll })
</script>

<template>
  <div class="spt-root" data-testid="companion-planning-panel">
    <div class="spt-subtabs" role="tablist">
      <button
        type="button"
        role="tab"
        :aria-selected="activeSubtab === 'schedules'"
        class="spt-subtab"
        :class="{ active: activeSubtab === 'schedules' }"
        data-testid="spt-tab-schedules"
        @click="activeSubtab = 'schedules'"
      >
        <CalendarClock :size="13" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.schedulesTitle') }}</span>
        <span class="spt-count">{{ activeScheduleCount }}</span>
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="activeSubtab === 'plans'"
        class="spt-subtab"
        :class="{ active: activeSubtab === 'plans' }"
        data-testid="spt-tab-plans"
        @click="activeSubtab = 'plans'"
      >
        <ListChecks :size="13" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.plansTitle') }}</span>
        <span class="spt-count">{{ activePlanCount }}</span>
      </button>
      <button
        type="button"
        role="tab"
        :aria-selected="activeSubtab === 'todos'"
        class="spt-subtab"
        :class="{ active: activeSubtab === 'todos' }"
        data-testid="spt-tab-todos"
        @click="activeSubtab = 'todos'"
      >
        <CheckSquare :size="13" aria-hidden="true" />
        <span>{{ t('companionCenter.dock.todosTitle') }}</span>
        <span class="spt-count">{{ pendingTodoCount }}</span>
      </button>
    </div>

    <div v-if="!companion" class="spt-empty">{{ t('companionCenter.noSelection') }}</div>

    <!-- Schedules -->
    <section v-else-if="activeSubtab === 'schedules'" class="spt-section">
      <div class="spt-head">
        <strong>{{ t('companionCenter.dock.schedulesTitle') }}</strong>
        <UiButton size="sm" variant="outline" data-testid="add-schedule-toggle" @click="scheduleFormOpen = !scheduleFormOpen">
          <Plus :size="12" aria-hidden="true" />
          <span>{{ t('companionCenter.dock.addSchedule') }}</span>
        </UiButton>
      </div>
      <form v-if="scheduleFormOpen" class="spt-form" data-testid="schedule-form" @submit.prevent="addSchedule">
        <input v-model="scheduleTitle" class="spt-input" :placeholder="t('companionCenter.dock.titlePlaceholder')" required />
        <input v-model="scheduleCron" class="spt-input" :placeholder="t('companionCenter.dock.cronPlaceholder')" />
        <textarea v-model="schedulePrompt" class="spt-textarea" rows="2" :placeholder="t('companionCenter.dock.promptPlaceholder')" required />
        <div class="spt-form-actions">
          <UiButton type="submit" size="sm" data-testid="save-schedule-btn">{{ t('companionCenter.dock.addSchedule') }}</UiButton>
        </div>
      </form>
      <p v-if="!schedules.length && !loading" class="spt-empty">{{ t('companionCenter.dock.emptySchedules') }}</p>
      <ul v-else class="spt-list">
        <li v-for="item in schedules" :key="item.id" class="spt-item" data-testid="schedule-row">
          <div class="spt-item-main">
            <div class="spt-item-top">
              <strong>{{ item.title }}</strong>
              <code v-if="item.cron_expression" class="spt-cron">{{ item.cron_expression }}</code>
            </div>
            <p class="spt-item-desc">{{ item.prompt }}</p>
          </div>
          <div class="spt-item-actions">
            <button type="button" class="spt-icon-btn" :title="t('companionCenter.dock.triggerNow')" data-testid="trigger-schedule-btn" @click="runScheduleNow(item)">
              <Play :size="13" aria-hidden="true" />
            </button>
            <input type="checkbox" :checked="item.enabled" :aria-label="item.title" @change="toggleSchedule(item)" />
            <button type="button" class="spt-icon-btn is-danger" :title="t('companionCenter.dock.deleteItem')" @click="removeSchedule(item)">
              <Trash2 :size="13" aria-hidden="true" />
            </button>
          </div>
        </li>
      </ul>
    </section>

    <!-- Plans -->
    <section v-else-if="activeSubtab === 'plans'" class="spt-section">
      <div class="spt-head">
        <strong>{{ t('companionCenter.dock.plansTitle') }}</strong>
        <UiButton size="sm" variant="outline" data-testid="add-plan-toggle" @click="planFormOpen = !planFormOpen">
          <Plus :size="12" aria-hidden="true" />
          <span>{{ t('companionCenter.dock.addPlan') }}</span>
        </UiButton>
      </div>
      <form v-if="planFormOpen" class="spt-form" data-testid="plan-form" @submit.prevent="addPlan">
        <input v-model="planTitle" class="spt-input" :placeholder="t('companionCenter.dock.titlePlaceholder')" required />
        <textarea v-model="planSummary" class="spt-textarea" rows="2" :placeholder="t('companionCenter.dock.promptPlaceholder')" />
        <div class="spt-form-actions">
          <UiButton type="submit" size="sm" data-testid="save-plan-btn">{{ t('companionCenter.dock.addPlan') }}</UiButton>
        </div>
      </form>
      <p v-if="!plans.length && !loading" class="spt-empty">{{ t('companionCenter.dock.emptyPlans') }}</p>
      <ul v-else class="spt-list">
        <li v-for="plan in plans" :key="plan.id" class="spt-item" data-testid="plan-row">
          <div class="spt-item-main">
            <div class="spt-item-top">
              <strong>{{ plan.title }}</strong>
              <button type="button" class="spt-status-pill" :class="`is-${plan.status}`" @click="cyclePlanStatus(plan)">{{ plan.status }}</button>
            </div>
            <p v-if="plan.summary" class="spt-item-desc">{{ plan.summary }}</p>
          </div>
          <div class="spt-item-actions">
            <button type="button" class="spt-icon-btn is-danger" :title="t('companionCenter.dock.deleteItem')" @click="removePlan(plan)">
              <Trash2 :size="13" aria-hidden="true" />
            </button>
          </div>
        </li>
      </ul>
    </section>

    <!-- Todos -->
    <section v-else class="spt-section">
      <div class="spt-head">
        <strong>{{ t('companionCenter.dock.todosTitle') }}</strong>
        <UiButton size="sm" variant="outline" data-testid="add-todo-toggle" @click="todoFormOpen = !todoFormOpen">
          <Plus :size="12" aria-hidden="true" />
          <span>{{ t('companionCenter.dock.addTodo') }}</span>
        </UiButton>
      </div>
      <form v-if="todoFormOpen" class="spt-form" data-testid="todo-form" @submit.prevent="addTodo">
        <input v-model="todoTitle" class="spt-input" :placeholder="t('companionCenter.dock.titlePlaceholder')" required />
        <select v-model="todoPriority" class="spt-input">
          <option value="low">low</option>
          <option value="medium">medium</option>
          <option value="high">high</option>
        </select>
        <div class="spt-form-actions">
          <UiButton type="submit" size="sm" data-testid="save-todo-btn">{{ t('companionCenter.dock.addTodo') }}</UiButton>
        </div>
      </form>
      <p v-if="!todos.length && !loading" class="spt-empty">{{ t('companionCenter.dock.emptyTodos') }}</p>
      <ul v-else class="spt-list">
        <li v-for="todo in todos" :key="todo.id" class="spt-item" :class="{ 'is-done': todo.status === 'done' }" data-testid="todo-row">
          <label class="spt-todo-check">
            <input type="checkbox" :checked="todo.status === 'done'" @change="toggleTodo(todo)" />
            <span class="spt-todo-title">{{ todo.title }}</span>
            <span class="spt-priority">{{ todo.priority }}</span>
          </label>
          <button type="button" class="spt-icon-btn is-danger" :title="t('companionCenter.dock.deleteItem')" @click="removeTodo(todo)">
            <Trash2 :size="13" aria-hidden="true" />
          </button>
        </li>
      </ul>
    </section>
  </div>
</template>

<style scoped>
.spt-root { display: flex; flex-direction: column; gap: 10px; min-height: 0; min-width: 0; }
.spt-subtabs { display: flex; gap: 4px; flex-wrap: wrap; }
.spt-subtab {
  display: inline-flex; align-items: center; justify-content: center; gap: 5px;
  height: 28px; padding: 0 8px; flex-shrink: 0; white-space: nowrap;
  border: 1px solid var(--border-muted); border-radius: var(--radius-control, 6px);
  background: transparent; color: var(--text-muted); font-size: 12px; cursor: pointer;
  transition: background-color 120ms ease, color 120ms ease, border-color 120ms ease;
}
.spt-subtab:hover { background: var(--surface-hover, var(--surface-raised)); color: var(--text-primary); }
.spt-subtab:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
.spt-subtab.active {
  background: var(--surface-raised); color: var(--text-primary); border-color: var(--border-card); font-weight: 600;
}
.spt-count {
  display: inline-flex; align-items: center; justify-content: center;
  min-width: 18px; height: 16px; padding: 0 5px; border-radius: 999px;
  background: color-mix(in srgb, var(--accent-primary) 14%, transparent);
  color: var(--accent-primary); font-size: 11px; font-weight: 600;
  font-variant-numeric: tabular-nums; white-space: nowrap; flex-shrink: 0;
}
.spt-section { display: flex; flex-direction: column; gap: 8px; min-width: 0; }
.spt-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; font-size: 12px; min-width: 0; }
.spt-head strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.spt-head :deep(button),
.spt-form-actions :deep(button) {
  display: inline-flex; align-items: center; justify-content: center; gap: 6px;
  height: 28px; padding: 0 10px; border-radius: var(--radius-control, 6px);
  font-size: 12px; white-space: nowrap; flex-shrink: 0;
}
.spt-form {
  display: flex; flex-direction: column; gap: 6px; padding: 8px;
  border-radius: var(--radius-card, 10px); border: 1px solid var(--border-muted); background: var(--surface-raised);
}
.spt-input {
  width: 100%; height: 30px; padding: 0 8px; box-sizing: border-box;
  border-radius: var(--radius-control, 6px); border: 1px solid var(--border-muted);
  background: var(--surface-input, var(--surface-section)); color: var(--text-primary); font: inherit; font-size: 12px;
}
.spt-textarea {
  width: 100%; padding: 6px 8px; box-sizing: border-box;
  border-radius: var(--radius-control, 6px); border: 1px solid var(--border-muted);
  background: var(--surface-input, var(--surface-section)); color: var(--text-primary); font: inherit; font-size: 12px;
}
.spt-input:focus-visible,
.spt-textarea:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  border-color: var(--accent-primary);
}
.spt-form-actions { display: flex; justify-content: flex-end; }
.spt-list { display: flex; flex-direction: column; gap: 6px; margin: 0; padding: 0; list-style: none; }
.spt-item {
  display: flex; align-items: flex-start; justify-content: space-between; gap: 8px;
  padding: 8px 10px; border-radius: var(--radius-card, 10px); border: 1px solid var(--border-muted);
  background: var(--surface-raised); min-width: 0;
}
.spt-item.is-done .spt-todo-title { text-decoration: line-through; opacity: 0.6; }
.spt-item-main { min-width: 0; flex: 1; }
.spt-item-top { display: flex; align-items: center; gap: 6px; font-size: 12px; flex-wrap: wrap; min-width: 0; }
.spt-item-top strong { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.spt-cron {
  font-size: 11px; padding: 1px 6px; border-radius: 6px;
  border: 1px solid var(--border-muted);
  background: color-mix(in srgb, var(--text-muted) 10%, transparent);
  font-variant-numeric: tabular-nums; white-space: nowrap; flex-shrink: 0;
}
.spt-item-desc { margin: 3px 0 0; font-size: 11px; color: var(--text-muted); overflow-wrap: anywhere; }
.spt-item-actions { display: flex; align-items: center; gap: 4px; flex-shrink: 0; }
.spt-item-actions input[type="checkbox"],
.spt-todo-check input[type="checkbox"] { flex-shrink: 0; cursor: pointer; }
.spt-item-actions input[type="checkbox"]:focus-visible,
.spt-todo-check input[type="checkbox"]:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 2px;
}
.spt-icon-btn {
  display: inline-flex; align-items: center; justify-content: center;
  width: 26px; height: 26px; flex-shrink: 0; white-space: nowrap;
  border: 1px solid transparent; border-radius: var(--radius-control, 6px);
  background: transparent; color: var(--text-muted); cursor: pointer;
}
.spt-icon-btn:hover { background: var(--bg-hover); color: var(--text-primary); }
.spt-icon-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
  color: var(--text-primary);
}
.spt-icon-btn.is-danger:hover { color: var(--accent-danger); }
.spt-status-pill {
  display: inline-flex; align-items: center; justify-content: center;
  height: 22px; padding: 0 7px; border: 1px solid var(--border-muted); border-radius: 999px;
  background: transparent; color: var(--text-muted); font-size: 11px;
  white-space: nowrap; flex-shrink: 0; cursor: pointer;
}
.spt-status-pill:hover { background: var(--surface-hover, var(--surface-section)); color: var(--text-primary); }
.spt-status-pill:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--accent-primary) 65%, transparent);
  outline-offset: 1px;
}
.spt-status-pill.is-completed {
  color: var(--accent-success);
  border-color: color-mix(in srgb, var(--accent-success) 36%, transparent);
  background: color-mix(in srgb, var(--accent-success) 10%, transparent);
}
.spt-todo-check { display: flex; align-items: center; gap: 6px; flex: 1; min-width: 0; font-size: 12px; cursor: pointer; }
.spt-todo-title { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.spt-priority {
  font-size: 11px; color: var(--text-muted); text-transform: uppercase;
  white-space: nowrap; flex-shrink: 0;
}
.spt-empty { margin: 8px 0; font-size: 12px; color: var(--text-muted); text-align: center; }
</style>
